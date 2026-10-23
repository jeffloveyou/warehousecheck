using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M6_04_Stocktakes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_documents_type",
                table: "stock_documents");

            migrationBuilder.CreateSequence(
                name: "adjustment_doc_no_seq");

            migrationBuilder.CreateSequence(
                name: "stocktake_no_seq");

            migrationBuilder.AddColumn<bool>(
                name: "is_decrease",
                table: "stock_document_lines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "stocktakes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stocktake_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    all_locations = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    snapshot_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    adjustment_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recount_comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stocktakes", x => x.id);
                    table.CheckConstraint("ck_stocktakes_status", "status IN ('Counting', 'Submitted', 'Approved', 'Cancelled')");
                    table.ForeignKey(
                        name: "fk_stocktakes_stock_documents_adjustment_document_id",
                        column: x => x.adjustment_document_id,
                        principalTable: "stock_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stocktakes_warehouses_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stocktake_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stocktake_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    serial_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lot_no = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    serial_no = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    stock_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    snapshot_quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    is_added = table.Column<bool>(type: "boolean", nullable: false),
                    counted_quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    book_quantity_at_count = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    counted_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    counted_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    recount_requested = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stocktake_lines", x => x.id);
                    table.CheckConstraint("ck_stocktake_lines_count_pair", "(counted_quantity IS NULL) = (book_quantity_at_count IS NULL)");
                    table.CheckConstraint("ck_stocktake_lines_counted", "counted_quantity IS NULL OR counted_quantity >= 0");
                    table.ForeignKey(
                        name: "fk_stocktake_lines_inventory_lots_lot_id",
                        column: x => x.lot_id,
                        principalTable: "inventory_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stocktake_lines_item_serials_serial_id",
                        column: x => x.serial_id,
                        principalTable: "item_serials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stocktake_lines_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stocktake_lines_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stocktake_lines_stocktakes_stocktake_id",
                        column: x => x.stocktake_id,
                        principalTable: "stocktakes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stocktake_locations",
                columns: table => new
                {
                    stocktake_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_open = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stocktake_locations", x => new { x.stocktake_id, x.location_id });
                    table.ForeignKey(
                        name: "fk_stocktake_locations_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stocktake_locations_stocktakes_stocktake_id",
                        column: x => x.stocktake_id,
                        principalTable: "stocktakes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_documents_type",
                table: "stock_documents",
                sql: "doc_type IN ('Opening', 'Inbound', 'Outbound', 'Transfer', 'Adjustment')");

            migrationBuilder.CreateIndex(
                name: "ix_stocktake_lines_item_id",
                table: "stocktake_lines",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_stocktake_lines_location_id",
                table: "stocktake_lines",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_stocktake_lines_lot_id",
                table: "stocktake_lines",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_stocktake_lines_serial_id",
                table: "stocktake_lines",
                column: "serial_id");

            migrationBuilder.CreateIndex(
                name: "ix_stocktake_lines_stocktake_id_line_no",
                table: "stocktake_lines",
                columns: new[] { "stocktake_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_stocktake_lines_key",
                table: "stocktake_lines",
                columns: new[] { "stocktake_id", "location_id", "item_id", "lot_no", "serial_no", "stock_status" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_stocktake_locations_open",
                table: "stocktake_locations",
                column: "location_id",
                unique: true,
                filter: "is_open");

            migrationBuilder.CreateIndex(
                name: "ix_stocktakes_adjustment_document_id",
                table: "stocktakes",
                column: "adjustment_document_id");

            migrationBuilder.CreateIndex(
                name: "ix_stocktakes_stocktake_no",
                table: "stocktakes",
                column: "stocktake_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stocktakes_warehouse_id_status_created_at",
                table: "stocktakes",
                columns: new[] { "warehouse_id", "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stocktake_lines");

            migrationBuilder.DropTable(
                name: "stocktake_locations");

            migrationBuilder.DropTable(
                name: "stocktakes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_documents_type",
                table: "stock_documents");

            migrationBuilder.DropColumn(
                name: "is_decrease",
                table: "stock_document_lines");

            migrationBuilder.DropSequence(
                name: "adjustment_doc_no_seq");

            migrationBuilder.DropSequence(
                name: "stocktake_no_seq");

            // NOT VALID：已有盤點調整單時仍可降版（舊版不會寫入 Adjustment；既有資料保留，見 docs/11）
            migrationBuilder.Sql(
                "ALTER TABLE stock_documents ADD CONSTRAINT ck_stock_documents_type CHECK (doc_type IN ('Opening', 'Inbound', 'Outbound', 'Transfer')) NOT VALID;");
        }
    }
}
