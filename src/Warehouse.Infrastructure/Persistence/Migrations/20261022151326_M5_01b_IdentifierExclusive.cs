using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 專屬別名與原廠條碼互斥下放到資料庫（M5-01 審查 S1）：程式層已在識別碼鎖下檢查，
    /// 但 A/B 相容窗口或回滾期間，舊版程式新增別名不會取鎖、也不查條碼表。trigger 讓任何版本寫入都受同一條規則保護。
    /// 鎖鍵與程式的 <c>AdvisoryLocks.LockIdentifierAsync</c> 相同（hashtext('ident:' || normalized_text)），同一交易內重複取鎖不會卡住。
    /// 衝突時以 unique_violation（23505）回報：程式既有的 23505 處理會重新檢查並回覆清楚的訊息。
    /// 只新增函式與 trigger（Expand），舊版程式不需修改。
    /// </summary>
    public partial class M5_01b_IdentifierExclusive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION item_identifier_exclusive() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT NEW.is_active THEN
                        RETURN NEW;
                    END IF;
                    PERFORM pg_advisory_xact_lock(hashtext('ident:' || NEW.normalized_text));
                    IF TG_TABLE_NAME = 'item_aliases' THEN
                        IF EXISTS (SELECT 1 FROM item_barcodes WHERE is_active AND normalized_text = NEW.normalized_text) THEN
                            RAISE EXCEPTION '這段文字已是有效的原廠條碼，不能當專屬別名（%）', NEW.text
                                USING ERRCODE = 'unique_violation';
                        END IF;
                    ELSIF EXISTS (SELECT 1 FROM item_aliases WHERE is_active AND normalized_text = NEW.normalized_text) THEN
                        RAISE EXCEPTION '這個條碼已是有效的專屬別名，不能登錄為原廠條碼（%）', NEW.text
                            USING ERRCODE = 'unique_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER trg_item_aliases_identifier_exclusive BEFORE INSERT ON item_aliases
                    FOR EACH ROW EXECUTE FUNCTION item_identifier_exclusive();
                CREATE TRIGGER trg_item_barcodes_identifier_exclusive BEFORE INSERT ON item_barcodes
                    FOR EACH ROW EXECUTE FUNCTION item_identifier_exclusive();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_item_aliases_identifier_exclusive ON item_aliases;
                DROP TRIGGER IF EXISTS trg_item_barcodes_identifier_exclusive ON item_barcodes;
                DROP FUNCTION IF EXISTS item_identifier_exclusive();
                """);
        }
    }
}
