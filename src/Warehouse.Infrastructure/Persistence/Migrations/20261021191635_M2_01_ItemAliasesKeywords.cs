using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_01_ItemAliasesKeywords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "item_aliases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    normalized_text = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    deactivated_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deactivated_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_aliases", x => x.id);
                    table.CheckConstraint("ck_item_aliases_deactivation", "is_active = (deactivated_at IS NULL) AND (deactivated_at IS NULL) = (deactivated_by_id IS NULL)");
                    table.CheckConstraint("ck_item_aliases_text_not_blank", "btrim(text) <> '' AND normalized_text <> ''");
                    table.ForeignKey(
                        name: "fk_item_aliases_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "item_keywords",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    normalized_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    deactivated_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deactivated_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_keywords", x => x.id);
                    table.CheckConstraint("ck_item_keywords_deactivation", "is_active = (deactivated_at IS NULL) AND (deactivated_at IS NULL) = (deactivated_by_id IS NULL)");
                    table.CheckConstraint("ck_item_keywords_text_not_blank", "btrim(text) <> '' AND normalized_text <> ''");
                    table.ForeignKey(
                        name: "fk_item_keywords_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_aliases_item_id",
                table: "item_aliases",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ux_item_aliases_active_normalized_text",
                table: "item_aliases",
                column: "normalized_text",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_item_keywords_normalized_text",
                table: "item_keywords",
                column: "normalized_text");

            migrationBuilder.CreateIndex(
                name: "ux_item_keywords_active_item_text",
                table: "item_keywords",
                columns: new[] { "item_id", "normalized_text" },
                unique: true,
                filter: "is_active");

            // 索引只停用、不刪除（docs/03 §3）：禁止 DELETE；更新時只允許「有效 → 停用」並寫入停用者，
            // 文字、物料、建立者不可改，停用後也不可改回有效（要恢復就新增一筆）。錯誤代碼 23001。
            migrationBuilder.Sql("""
                CREATE FUNCTION item_index_protect() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION '搜尋索引只能停用，不可刪除（%）', OLD.text
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.id <> OLD.id OR NEW.item_id <> OLD.item_id OR NEW.text <> OLD.text
                       OR NEW.normalized_text <> OLD.normalized_text
                       OR NEW.created_by_id <> OLD.created_by_id OR NEW.created_by_name <> OLD.created_by_name
                       OR NEW.created_at <> OLD.created_at OR (NEW.is_active AND NOT OLD.is_active) THEN
                        RAISE EXCEPTION '搜尋索引建立後只能停用，不可修改內容或重新啟用（%）', OLD.text
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    -- 已停用的紀錄：停用者與時間是稽核資料，不可改寫
                    IF NOT OLD.is_active AND (NEW.deactivated_at IS DISTINCT FROM OLD.deactivated_at
                       OR NEW.deactivated_by_id IS DISTINCT FROM OLD.deactivated_by_id
                       OR NEW.deactivated_by_name IS DISTINCT FROM OLD.deactivated_by_name) THEN
                        RAISE EXCEPTION '已停用的搜尋索引不可修改停用紀錄（%）', OLD.text
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE FUNCTION item_index_no_truncate() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '搜尋索引不可整批清除（%）', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER trg_item_aliases_protect BEFORE UPDATE OR DELETE ON item_aliases
                    FOR EACH ROW EXECUTE FUNCTION item_index_protect();
                CREATE TRIGGER trg_item_keywords_protect BEFORE UPDATE OR DELETE ON item_keywords
                    FOR EACH ROW EXECUTE FUNCTION item_index_protect();
                CREATE TRIGGER trg_item_aliases_no_truncate BEFORE TRUNCATE ON item_aliases
                    FOR EACH STATEMENT EXECUTE FUNCTION item_index_no_truncate();
                CREATE TRIGGER trg_item_keywords_no_truncate BEFORE TRUNCATE ON item_keywords
                    FOR EACH STATEMENT EXECUTE FUNCTION item_index_no_truncate();
                """);
        }

        /// <inheritdoc />
        /// <remarks>僅供開發／測試：會刪除全部搜尋索引。正式環境回滾保留資料表，只切回舊版程式（docs/11 §10）。</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_item_aliases_protect ON item_aliases;
                DROP TRIGGER IF EXISTS trg_item_keywords_protect ON item_keywords;
                DROP TRIGGER IF EXISTS trg_item_aliases_no_truncate ON item_aliases;
                DROP TRIGGER IF EXISTS trg_item_keywords_no_truncate ON item_keywords;
                DROP FUNCTION IF EXISTS item_index_protect();
                DROP FUNCTION IF EXISTS item_index_no_truncate();
                """);

            migrationBuilder.DropTable(
                name: "item_aliases");

            migrationBuilder.DropTable(
                name: "item_keywords");
        }
    }
}
