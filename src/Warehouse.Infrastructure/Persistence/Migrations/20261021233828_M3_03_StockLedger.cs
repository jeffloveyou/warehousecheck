using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3_03_StockLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "opening_doc_no_seq");

            migrationBuilder.AddColumn<Guid>(
                name: "opening_document_id",
                table: "locations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "inventory_lots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_no = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory_lots", x => x.id);
                    table.CheckConstraint("ck_inventory_lots_lot_no", "btrim(lot_no) <> ''");
                    table.ForeignKey(
                        name: "fk_inventory_lots_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "processed_commands",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    command_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    result_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_commands", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "stock_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    doc_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    doc_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    return_comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_documents", x => x.id);
                    table.CheckConstraint("ck_stock_documents_posted", "(status = 'Posted') = (posted_at IS NOT NULL AND approved_by_id IS NOT NULL)");
                    table.CheckConstraint("ck_stock_documents_status", "status IN ('Draft', 'Submitted', 'Posted', 'Cancelled')");
                    table.CheckConstraint("ck_stock_documents_type", "doc_type IN ('Opening')");
                    table.ForeignKey(
                        name: "fk_stock_documents_warehouses_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "item_serials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serial_no = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_serials", x => x.id);
                    table.CheckConstraint("ck_item_serials_serial_no", "btrim(serial_no) <> ''");
                    table.ForeignKey(
                        name: "fk_item_serials_inventory_lots_lot_id",
                        column: x => x.lot_id,
                        principalTable: "inventory_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_item_serials_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_balances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    serial_id = table.Column<Guid>(type: "uuid", nullable: true),
                    stock_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_balances", x => x.id);
                    table.CheckConstraint("ck_stock_balances_non_negative", "quantity >= 0");
                    table.CheckConstraint("ck_stock_balances_serial_quantity", "serial_id IS NULL OR quantity IN (0, 1)");
                    table.CheckConstraint("ck_stock_balances_status", "stock_status IN ('Available', 'Quarantine')");
                    table.ForeignKey(
                        name: "fk_stock_balances_inventory_lots_lot_id",
                        column: x => x.lot_id,
                        principalTable: "inventory_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_balances_item_serials_serial_id",
                        column: x => x.serial_id,
                        principalTable: "item_serials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_balances_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_balances_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_document_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_no = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    serial_no = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    serial_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_document_lines", x => x.id);
                    table.CheckConstraint("ck_stock_document_lines_empty_location", "item_id IS NOT NULL OR quantity = 0");
                    table.CheckConstraint("ck_stock_document_lines_quantity", "quantity >= 0");
                    table.ForeignKey(
                        name: "fk_stock_document_lines_inventory_lots_lot_id",
                        column: x => x.lot_id,
                        principalTable: "inventory_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_document_lines_item_serials_serial_id",
                        column: x => x.serial_id,
                        principalTable: "item_serials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_document_lines_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_document_lines_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_document_lines_stock_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "stock_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_movements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    serial_id = table.Column<Guid>(type: "uuid", nullable: true),
                    stock_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity_delta = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_movements", x => x.id);
                    table.CheckConstraint("ck_stock_movements_delta", "quantity_delta <> 0");
                    table.CheckConstraint("ck_stock_movements_status", "stock_status IN ('Available', 'Quarantine')");
                    table.ForeignKey(
                        name: "fk_stock_movements_inventory_lots_lot_id",
                        column: x => x.lot_id,
                        principalTable: "inventory_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_item_serials_serial_id",
                        column: x => x.serial_id,
                        principalTable: "item_serials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_stock_document_lines_line_id",
                        column: x => x.line_id,
                        principalTable: "stock_document_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_stock_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "stock_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_locations_opening_document_id",
                table: "locations",
                column: "opening_document_id");

            migrationBuilder.CreateIndex(
                name: "ix_inventory_lots_item_id_lot_no",
                table: "inventory_lots",
                columns: new[] { "item_id", "lot_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_item_serials_item_id_serial_no",
                table: "item_serials",
                columns: new[] { "item_id", "serial_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_item_serials_lot_id",
                table: "item_serials",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_balances_location_id_item_id",
                table: "stock_balances",
                columns: new[] { "location_id", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_balances_lot_id",
                table: "stock_balances",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "ux_stock_balances_dimension",
                table: "stock_balances",
                columns: new[] { "item_id", "location_id", "lot_id", "serial_id", "stock_status" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_stock_balances_serial_in_stock",
                table: "stock_balances",
                column: "serial_id",
                unique: true,
                filter: "serial_id IS NOT NULL AND quantity > 0");

            migrationBuilder.CreateIndex(
                name: "ix_stock_document_lines_document_id_line_no",
                table: "stock_document_lines",
                columns: new[] { "document_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_document_lines_item_id",
                table: "stock_document_lines",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_document_lines_location_id",
                table: "stock_document_lines",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_document_lines_lot_id",
                table: "stock_document_lines",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_document_lines_serial_id",
                table: "stock_document_lines",
                column: "serial_id");

            migrationBuilder.CreateIndex(
                name: "ux_stock_document_lines_key",
                table: "stock_document_lines",
                columns: new[] { "document_id", "location_id", "item_id", "lot_no", "serial_no" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_doc_no",
                table: "stock_documents",
                column: "doc_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_doc_type_status_created_at",
                table: "stock_documents",
                columns: new[] { "doc_type", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_warehouse_id",
                table: "stock_documents",
                column: "warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_document_id",
                table: "stock_movements",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_item_id_occurred_at",
                table: "stock_movements",
                columns: new[] { "item_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_line_id",
                table: "stock_movements",
                column: "line_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_location_id_occurred_at",
                table: "stock_movements",
                columns: new[] { "location_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_lot_id",
                table: "stock_movements",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_serial_id",
                table: "stock_movements",
                column: "serial_id");

            migrationBuilder.AddForeignKey(
                name: "fk_locations_stock_documents_opening_document_id",
                table: "locations",
                column: "opening_document_id",
                principalTable: "stock_documents",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // 庫存帳保護（ADR-0014，錯誤代碼 23001）：
            // 1. 異動帳只能新增：UPDATE／DELETE／TRUNCATE 一律拒絕。
            // 2. 單據不可刪除；已過帳或已取消的單據不可再修改。
            // 3. 明細只能在草稿時新增、修改、刪除；送出後只允許過帳流程填入 lot_id／serial_id。
            migrationBuilder.Sql("""
                CREATE FUNCTION stock_movements_append_only() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '庫存異動帳只能新增，不可修改或刪除；更正請用調整單' USING ERRCODE = 'restrict_violation';
                END;
                $$;
                CREATE TRIGGER trg_stock_movements_append_only BEFORE UPDATE OR DELETE ON stock_movements
                    FOR EACH ROW EXECUTE FUNCTION stock_movements_append_only();
                CREATE TRIGGER trg_stock_movements_no_truncate BEFORE TRUNCATE ON stock_movements
                    FOR EACH STATEMENT EXECUTE FUNCTION stock_movements_append_only();

                CREATE FUNCTION stock_documents_protect() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION '庫存單據不可刪除（%），請改為取消', OLD.doc_no USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.status IN ('Posted', 'Cancelled') THEN
                        RAISE EXCEPTION '已過帳或已取消的單據不可修改（%）', OLD.doc_no USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER trg_stock_documents_protect BEFORE UPDATE OR DELETE ON stock_documents
                    FOR EACH ROW EXECUTE FUNCTION stock_documents_protect();

                CREATE FUNCTION stock_document_lines_protect() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    doc_status text;
                BEGIN
                    -- 不可把明細搬到別張單（否則已過帳的明細可被移到草稿單再修改，M3-03 審查 #3）
                    IF TG_OP = 'UPDATE' AND NEW.document_id <> OLD.document_id THEN
                        RAISE EXCEPTION '明細不可移到其他單據' USING ERRCODE = 'restrict_violation';
                    END IF;
                    SELECT status INTO doc_status FROM stock_documents WHERE id = COALESCE(NEW.document_id, OLD.document_id);
                    IF doc_status = 'Draft' THEN
                        RETURN COALESCE(NEW, OLD);
                    END IF;
                    IF TG_OP = 'UPDATE' AND doc_status = 'Submitted'
                       AND (NEW.document_id, NEW.line_no, NEW.item_id, NEW.location_id, NEW.lot_no, NEW.serial_no, NEW.expiry_date, NEW.quantity)
                           IS NOT DISTINCT FROM
                           (OLD.document_id, OLD.line_no, OLD.item_id, OLD.location_id, OLD.lot_no, OLD.serial_no, OLD.expiry_date, OLD.quantity) THEN
                        RETURN NEW; -- 過帳時填入 lot_id／serial_id
                    END IF;
                    RAISE EXCEPTION '只有草稿單據可以修改明細' USING ERRCODE = 'restrict_violation';
                END;
                $$;
                CREATE TRIGGER trg_stock_document_lines_protect BEFORE INSERT OR UPDATE OR DELETE ON stock_document_lines
                    FOR EACH ROW EXECUTE FUNCTION stock_document_lines_protect();
                """);
        }

        /// <inheritdoc />
        /// <remarks>僅供開發／測試：會刪除全部庫存帳。正式環境回滾保留資料表，只切回舊版程式（docs/11 §10）。</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_stock_movements_append_only ON stock_movements;
                DROP TRIGGER IF EXISTS trg_stock_movements_no_truncate ON stock_movements;
                DROP TRIGGER IF EXISTS trg_stock_documents_protect ON stock_documents;
                DROP TRIGGER IF EXISTS trg_stock_document_lines_protect ON stock_document_lines;
                DROP FUNCTION IF EXISTS stock_movements_append_only();
                DROP FUNCTION IF EXISTS stock_documents_protect();
                DROP FUNCTION IF EXISTS stock_document_lines_protect();
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_locations_stock_documents_opening_document_id",
                table: "locations");

            migrationBuilder.DropTable(
                name: "processed_commands");

            migrationBuilder.DropTable(
                name: "stock_balances");

            migrationBuilder.DropTable(
                name: "stock_movements");

            migrationBuilder.DropTable(
                name: "stock_document_lines");

            migrationBuilder.DropTable(
                name: "item_serials");

            migrationBuilder.DropTable(
                name: "stock_documents");

            migrationBuilder.DropTable(
                name: "inventory_lots");

            migrationBuilder.DropIndex(
                name: "ix_locations_opening_document_id",
                table: "locations");

            migrationBuilder.DropColumn(
                name: "opening_document_id",
                table: "locations");

            migrationBuilder.DropSequence(
                name: "opening_doc_no_seq");
        }
    }
}
