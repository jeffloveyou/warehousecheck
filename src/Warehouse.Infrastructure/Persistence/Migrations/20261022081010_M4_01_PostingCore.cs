using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4_01_PostingCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_balances_non_negative",
                table: "stock_balances");

            // 序號上限改成 <= 1 且為整數（原本 IN (0, 1) 會在 ON CONFLICT 前以預定插入列 −1 檢查，序號出庫被誤拒）
            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_balances_serial_quantity",
                table: "stock_balances");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_balances_serial_quantity",
                table: "stock_balances",
                sql: "serial_id IS NULL OR (quantity <= 1 AND quantity = trunc(quantity))");

            migrationBuilder.AddColumn<bool>(
                name: "is_negative",
                table: "stock_movements",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "stock_reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    remaining_quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    reference_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_reservations", x => x.id);
                    table.CheckConstraint("ck_stock_reservations_closed", "(status = 'Open') = (closed_at IS NULL)");
                    table.CheckConstraint("ck_stock_reservations_consumed", "status <> 'Consumed' OR remaining_quantity = 0");
                    table.CheckConstraint("ck_stock_reservations_quantity", "quantity > 0 AND remaining_quantity >= 0 AND remaining_quantity <= quantity");
                    table.CheckConstraint("ck_stock_reservations_status", "status IN ('Open', 'Consumed', 'Released')");
                    table.ForeignKey(
                        name: "fk_stock_reservations_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_reservations_warehouses_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_open",
                table: "stock_reservations",
                columns: new[] { "item_id", "warehouse_id" },
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_reference_type_reference_id",
                table: "stock_reservations",
                columns: new[] { "reference_type", "reference_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_warehouse_id",
                table: "stock_reservations",
                column: "warehouse_id");

            // 結存不可為負，除非物料允許負庫存（ADR-0004 決策一，取代 ck_stock_balances_non_negative）。
            // 下限是「料號全倉合計」（使用者 2026-10-22），單一維度不設下限，由過帳服務的寫入後檢查負責：
            // - 必須是 AFTER trigger：INSERT … ON CONFLICT DO UPDATE 會先以「預定插入列」（數量＝扣減量，為負）觸發 BEFORE INSERT，會誤拒。
            // - 只在數量變少時檢查，補帳入庫不會被既有負數擋住。
            // - 錯誤沿用 check_violation（23514）與原本的約束名稱，舊版程式在 A/B 切換期間的錯誤對應不變；訊息帶物料與儲位 ID（Npgsql 預設不回傳 DETAIL）。
            migrationBuilder.Sql("""
                CREATE FUNCTION stock_balances_floor() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    allow boolean;
                BEGIN
                    IF TG_OP = 'UPDATE' AND NEW.quantity >= OLD.quantity THEN
                        RETURN NULL;
                    END IF;
                    SELECT allow_negative_stock INTO allow FROM items WHERE id = NEW.item_id;
                    IF NOT COALESCE(allow, false) THEN
                        RAISE EXCEPTION '結存不可為負（物料 % 不允許負庫存，儲位 %）', NEW.item_id, NEW.location_id USING ERRCODE = 'check_violation',
                            CONSTRAINT = 'ck_stock_balances_non_negative', TABLE = 'stock_balances';
                    END IF;
                    RETURN NULL;
                END;
                $$;
                CREATE TRIGGER trg_stock_balances_floor AFTER INSERT OR UPDATE OF quantity ON stock_balances
                    FOR EACH ROW WHEN (NEW.quantity < 0) EXECUTE FUNCTION stock_balances_floor();
                """);
        }

        /// <inheritdoc />
        /// <remarks>僅供開發／測試：預留資料會被刪除；既有負結存保留（CHECK 以 NOT VALID 加回）。</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_stock_balances_floor ON stock_balances; DROP FUNCTION IF EXISTS stock_balances_floor();");

            migrationBuilder.DropTable(
                name: "stock_reservations");

            migrationBuilder.DropColumn(
                name: "is_negative",
                table: "stock_movements");

            // NOT VALID：已有負結存時仍可降版（舊版只做增加，新寫入照樣受檢查）
            migrationBuilder.Sql(
                "ALTER TABLE stock_balances ADD CONSTRAINT ck_stock_balances_non_negative CHECK (quantity >= 0) NOT VALID;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_balances_serial_quantity",
                table: "stock_balances");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_balances_serial_quantity",
                table: "stock_balances",
                sql: "serial_id IS NULL OR quantity IN (0, 1)");
        }
    }
}
