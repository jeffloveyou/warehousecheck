using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// D-32（ADR-0025）料號可於核准時指定、核准後變更。全部是 Expand：
    /// - 新表 item_code_history（只能新增）；item_reviews 的動作與料號約束放寬為也允許 CodeChange。
    /// - items_protect_item_code 改寫：料號仍不可清除、有料號的物料仍不可刪除；變更只在交易內設定
    ///   warehouse.item_code_change＝物料 ID 時允許（ItemWorkflowService.ChangeItemCodeAsync）；
    ///   新料號不可等於其他物料保留中的歷史料號（最後一道防線）。
    /// 舊版程式不改料號，也不讀新表，可在新結構上執行（docs/11 §7）。
    /// </summary>
    public partial class M10_01_ItemCodeChange : Migration
    {
        private const string ProtectItemCodeFunction = """
            CREATE OR REPLACE FUNCTION items_protect_item_code() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE
                reserved_by uuid;
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    IF OLD.item_code IS NOT NULL THEN
                        RAISE EXCEPTION '已發放正式料號的物料不可刪除（%），請改為停用', OLD.item_code
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN OLD;
                END IF;
                IF OLD.item_code IS NOT NULL AND NEW.item_code IS DISTINCT FROM OLD.item_code THEN
                    IF NEW.item_code IS NULL
                        OR coalesce(current_setting('warehouse.item_code_change', true), '') <> OLD.id::text THEN
                        RAISE EXCEPTION '正式料號只能經「變更料號」功能變更，且不可清除（%）', OLD.item_code
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                END IF;
                IF NEW.item_code IS NOT NULL AND NEW.item_code IS DISTINCT FROM OLD.item_code THEN
                    SELECT CASE WHEN h.released THEN NULL ELSE h.item_id END INTO reserved_by
                    FROM item_code_history h WHERE h.code = NEW.item_code
                    ORDER BY h.seq DESC LIMIT 1;
                    IF reserved_by IS NOT NULL AND reserved_by <> NEW.id THEN
                        RAISE EXCEPTION '料號 % 是其他物料保留的歷史料號，不可使用', NEW.item_code
                            USING ERRCODE = 'unique_violation';
                    END IF;
                END IF;
                RETURN NEW;
            END
            $$;
            """;

        /// <summary>M1_01b 的原始版本（Down 用）。</summary>
        private const string OriginalProtectItemCodeFunction = """
            CREATE OR REPLACE FUNCTION items_protect_item_code() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    IF OLD.item_code IS NOT NULL THEN
                        RAISE EXCEPTION '已發放正式料號的物料不可刪除（%），請改為停用', OLD.item_code
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN OLD;
                END IF;
                IF OLD.item_code IS NOT NULL AND NEW.item_code IS DISTINCT FROM OLD.item_code THEN
                    RAISE EXCEPTION '正式料號發放後不可變更或清除（%）', OLD.item_code
                        USING ERRCODE = 'restrict_violation';
                END IF;
                RETURN NEW;
            END
            $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_item_reviews_action",
                table: "item_reviews");

            migrationBuilder.DropCheckConstraint(
                name: "ck_item_reviews_item_code_on_approve",
                table: "item_reviews");

            migrationBuilder.CreateTable(
                name: "item_code_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    new_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    released = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_code_history", x => x.id);
                    table.CheckConstraint("ck_item_code_history_code_format", "code ~ '^[A-Z0-9]{2,4}-[0-9]{6,}$' AND new_code ~ '^[A-Z0-9]{2,4}-[0-9]{6,}$'");
                    table.CheckConstraint("ck_item_code_history_reason", "btrim(reason) <> ''");
                    table.ForeignKey(
                        name: "fk_item_code_history_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_item_reviews_action",
                table: "item_reviews",
                sql: "action IN ('Submit', 'Update', 'Resubmit', 'Approve', 'Return', 'Reject', 'Withdraw', 'Deactivate', 'Reactivate', 'CodeChange')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_item_reviews_item_code_on_approve",
                table: "item_reviews",
                sql: "(action IN ('Approve', 'CodeChange')) = (item_code IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_item_code_history_code_seq",
                table: "item_code_history",
                columns: new[] { "code", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_item_code_history_item_id_occurred_at",
                table: "item_code_history",
                columns: new[] { "item_id", "occurred_at" });

            migrationBuilder.Sql("""
                CREATE FUNCTION item_code_history_append_only() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '歷史料號只能新增，不可修改或刪除（%）', OLD.id
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER trg_item_code_history_append_only
                    BEFORE UPDATE OR DELETE ON item_code_history
                    FOR EACH ROW EXECUTE FUNCTION item_code_history_append_only();
                """);
            migrationBuilder.Sql(ProtectItemCodeFunction);
        }

        /// <inheritdoc />
        /// <remarks>僅供開發／測試：會刪除歷史料號。正式環境回滾保留資料表，只切回舊版程式（docs/11 §10）。</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(OriginalProtectItemCodeFunction);
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_item_code_history_append_only ON item_code_history;
                DROP FUNCTION IF EXISTS item_code_history_append_only();
                """);
            migrationBuilder.DropTable(
                name: "item_code_history");

            migrationBuilder.DropCheckConstraint(
                name: "ck_item_reviews_action",
                table: "item_reviews");

            migrationBuilder.DropCheckConstraint(
                name: "ck_item_reviews_item_code_on_approve",
                table: "item_reviews");

            // 已有 CodeChange 審核紀錄時（只能新增、刪不掉），舊約束只能以 NOT VALID 加回：不檢查既有列，只擋之後的新增
            migrationBuilder.Sql("""
                ALTER TABLE item_reviews ADD CONSTRAINT ck_item_reviews_action
                    CHECK (action IN ('Submit', 'Update', 'Resubmit', 'Approve', 'Return', 'Reject', 'Withdraw', 'Deactivate', 'Reactivate')) NOT VALID;
                ALTER TABLE item_reviews ADD CONSTRAINT ck_item_reviews_item_code_on_approve
                    CHECK ((action = 'Approve') = (item_code IS NOT NULL)) NOT VALID;
                """);
        }
    }
}
