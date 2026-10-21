using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M1_02_ItemWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "item_request_no_seq");

            migrationBuilder.AddColumn<Guid>(
                name: "created_by_id",
                table: "items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "normalized_name",
                table: "items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "normalized_part_no",
                table: "items",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "item_code_counters",
                columns: table => new
                {
                    prefix = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    last_number = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_code_counters", x => x.prefix);
                    table.CheckConstraint("ck_item_code_counters_last_number", "last_number >= 1");
                    table.CheckConstraint("ck_item_code_counters_prefix_format", "prefix ~ '^[A-Z0-9]{2,4}$'");
                });

            migrationBuilder.CreateTable(
                name: "item_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    from_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    to_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    item_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    duplicate_of_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_reviews", x => x.id);
                    table.CheckConstraint("ck_item_reviews_action", "action IN ('Submit', 'Update', 'Resubmit', 'Approve', 'Return', 'Reject', 'Withdraw', 'Deactivate', 'Reactivate')");
                    table.CheckConstraint("ck_item_reviews_comment_required", "action NOT IN ('Return', 'Reject') OR btrim(coalesce(comment, '')) <> ''");
                    table.CheckConstraint("ck_item_reviews_duplicate_on_reject", "duplicate_of_item_id IS NULL OR (action = 'Reject' AND duplicate_of_item_id <> item_id)");
                    table.CheckConstraint("ck_item_reviews_from_status", "from_status IN ('PendingApproval', 'Returned', 'Rejected', 'Active', 'Inactive')");
                    table.CheckConstraint("ck_item_reviews_item_code_on_approve", "(action = 'Approve') = (item_code IS NOT NULL)");
                    table.CheckConstraint("ck_item_reviews_to_status", "to_status IN ('PendingApproval', 'Returned', 'Rejected', 'Active', 'Inactive')");
                    table.ForeignKey(
                        name: "fk_item_reviews_items_duplicate_of_item_id",
                        column: x => x.duplicate_of_item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_item_reviews_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_items_normalized_name",
                table: "items",
                column: "normalized_name");

            migrationBuilder.CreateIndex(
                name: "ix_items_normalized_part_no",
                table: "items",
                column: "normalized_part_no");

            migrationBuilder.CreateIndex(
                name: "ix_item_reviews_duplicate_of_item_id",
                table: "item_reviews",
                column: "duplicate_of_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_item_reviews_item_id_occurred_at",
                table: "item_reviews",
                columns: new[] { "item_id", "occurred_at" });

            // 既有資料回填（M1-02 之前以 SQL 寫入的物料）：比對欄位用 PostgreSQL 近似 TextNormalizer 的規則
            migrationBuilder.Sql("""
                UPDATE items SET
                    normalized_name = upper(btrim(regexp_replace(normalize(name, NFKC), '\s+', ' ', 'g'))),
                    normalized_part_no = nullif(regexp_replace(upper(normalize(coalesce(manufacturer_part_no, ''), NFKC)), '[\s./_-]', '', 'g'), '');
                """);

            // 號碼接續既有資料（空資料庫時不做任何事）：申請單號 sequence 與每個前綴的料號計數
            migrationBuilder.Sql("""
                SELECT setval('item_request_no_seq', max(substring(request_no FROM 4)::bigint))
                FROM items HAVING count(*) > 0;

                INSERT INTO item_code_counters (prefix, last_number)
                SELECT split_part(item_code, '-', 1), max(split_part(item_code, '-', 2)::bigint)
                FROM items WHERE item_code IS NOT NULL
                GROUP BY split_part(item_code, '-', 1);
                """);

            // 審核紀錄只能新增：UPDATE／DELETE 一律拒絕（23001 restrict_violation）
            migrationBuilder.Sql("""
                CREATE FUNCTION item_reviews_append_only() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '審核紀錄只能新增，不可修改或刪除（%）', OLD.id
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER trg_item_reviews_append_only
                    BEFORE UPDATE OR DELETE ON item_reviews
                    FOR EACH ROW EXECUTE FUNCTION item_reviews_append_only();
                """);
        }

        /// <inheritdoc />
        /// <remarks>僅供開發／測試：會刪除審核紀錄與計數器。正式環境回滾保留資料表，只切回舊版程式（docs/11 §10）。</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_item_reviews_append_only ON item_reviews;
                DROP FUNCTION IF EXISTS item_reviews_append_only();
                """);

            migrationBuilder.DropTable(
                name: "item_code_counters");

            migrationBuilder.DropTable(
                name: "item_reviews");

            migrationBuilder.DropIndex(
                name: "ix_items_normalized_name",
                table: "items");

            migrationBuilder.DropIndex(
                name: "ix_items_normalized_part_no",
                table: "items");

            migrationBuilder.DropColumn(
                name: "created_by_id",
                table: "items");

            migrationBuilder.DropColumn(
                name: "normalized_name",
                table: "items");

            migrationBuilder.DropColumn(
                name: "normalized_part_no",
                table: "items");

            migrationBuilder.DropSequence(
                name: "item_request_no_seq");
        }
    }
}
