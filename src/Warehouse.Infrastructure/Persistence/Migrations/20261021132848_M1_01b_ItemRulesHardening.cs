using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// M1-01 審查補強（約束／索引／trigger；M1-01 之前的舊版程式不讀這些表，不受影響）：
    /// 料號與申請單號格式、品名不可空白、未允許負庫存時下限為 0、前綴唯一、分類不可指向自己、
    /// 狀態與料號規則改寫為不與 ck_items_status 重疊（語意不變），
    /// 以及「已發放料號不可變更、清除，物料不可刪除」的 trigger（使用者要求：料號發放後不得重新分配）。
    /// </summary>
    public partial class M1_01b_ItemRulesHardening : Migration
    {
        private const string ProtectItemCodeFunction = """
            CREATE FUNCTION items_protect_item_code() RETURNS trigger LANGUAGE plpgsql AS $$
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
            // 料號清不掉 + ck_items_code_matches_status（待核准等狀態必無料號）＝ 已核准的物料也無法倒退回待核准
            migrationBuilder.Sql(ProtectItemCodeFunction);
            migrationBuilder.Sql(
                "CREATE TRIGGER trg_items_protect_item_code BEFORE UPDATE OR DELETE ON items " +
                "FOR EACH ROW EXECUTE FUNCTION items_protect_item_code();");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_code_matches_status",
                table: "items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_code_matches_status",
                table: "items",
                sql: "(status IN ('Active', 'Inactive')) = (item_code IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_item_code_format",
                table: "items",
                sql: "item_code ~ '^[A-Z0-9]{2,4}-[0-9]{6,}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_name_not_blank",
                table: "items",
                sql: "btrim(name) <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_negative_stock_limit_when_disabled",
                table: "items",
                sql: "allow_negative_stock OR negative_stock_limit = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_request_no_format",
                table: "items",
                sql: "request_no ~ '^MR-[0-9]{6,}$'");

            migrationBuilder.CreateIndex(
                name: "ix_item_categories_item_code_prefix",
                table: "item_categories",
                column: "item_code_prefix",
                unique: true,
                filter: "item_code_prefix IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_item_categories_not_self_parent",
                table: "item_categories",
                sql: "parent_id IS NULL OR parent_id <> id");
        }

        /// <summary>僅供開發／測試。正式環境回滾不要執行 Down：保留約束與 trigger，只把流量切回舊版程式。</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_items_protect_item_code ON items;");
            migrationBuilder.Sql("DROP FUNCTION items_protect_item_code();");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_code_matches_status",
                table: "items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_item_code_format",
                table: "items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_name_not_blank",
                table: "items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_negative_stock_limit_when_disabled",
                table: "items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_request_no_format",
                table: "items");

            migrationBuilder.DropIndex(
                name: "ix_item_categories_item_code_prefix",
                table: "item_categories");

            migrationBuilder.DropCheckConstraint(
                name: "ck_item_categories_not_self_parent",
                table: "item_categories");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_code_matches_status",
                table: "items",
                sql: "(status IN ('Active', 'Inactive') AND item_code IS NOT NULL) OR (status IN ('PendingApproval', 'Returned', 'Rejected') AND item_code IS NULL)");
        }
    }
}
