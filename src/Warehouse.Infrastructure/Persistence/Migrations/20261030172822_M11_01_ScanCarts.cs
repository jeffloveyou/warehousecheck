using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 快速進出購物車（ADR-0026）：只新增資料表與 stock_documents.from_scan_cart（預設 false），舊版程式不受影響（Expand）。
    /// 單據保護 trigger 放寬一個例外：購物車轉入、仍是草稿的單可以刪除（使用者決定不留單據）；其他單據仍不可刪。
    /// </summary>
    public partial class M11_01_ScanCarts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "from_scan_cart",
                table: "stock_documents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "scan_carts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_carts", x => x.id);
                    table.ForeignKey(
                        name: "fk_scan_carts_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_scan_carts_warehouses_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scan_cart_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cart_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_cart_lines", x => x.id);
                    table.CheckConstraint("ck_scan_cart_lines_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_scan_cart_lines_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_scan_cart_lines_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_scan_cart_lines_scan_carts_cart_id",
                        column: x => x.cart_id,
                        principalTable: "scan_carts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scan_cart_lines_cart_id_line_no",
                table: "scan_cart_lines",
                columns: new[] { "cart_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scan_cart_lines_item_id",
                table: "scan_cart_lines",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_scan_cart_lines_location_id",
                table: "scan_cart_lines",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ux_scan_cart_lines_key",
                table: "scan_cart_lines",
                columns: new[] { "cart_id", "item_id", "location_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_scan_carts_owner_id_updated_at",
                table: "scan_carts",
                columns: new[] { "owner_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_carts_warehouse_id",
                table: "scan_carts",
                column: "warehouse_id");
            migrationBuilder.Sql(DocumentsProtect(allowScanCartDraftDelete: true));
        }

        /// <summary>stock_documents_protect()：單據不可刪除（購物車轉入的草稿例外）；已過帳或已取消的單據不可再修改。</summary>
        private static string DocumentsProtect(bool allowScanCartDraftDelete)
        {
            var exception = allowScanCartDraftDelete
                ? """
                        IF OLD.status = 'Draft' AND OLD.from_scan_cart THEN
                            RETURN OLD;
                        END IF;
                  """
                : "";
            // from_scan_cart 只在建立時（INSERT）決定，之後不可改：否則一般草稿可被改成「可刪除」（審查 5）
            var lockFlag = allowScanCartDraftDelete
                ? """
                    IF NEW.from_scan_cart IS DISTINCT FROM OLD.from_scan_cart THEN
                        RAISE EXCEPTION '單據來源不可修改（%）', OLD.doc_no USING ERRCODE = 'restrict_violation';
                    END IF;
                  """
                : "";
            return $$"""
                CREATE OR REPLACE FUNCTION stock_documents_protect() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                {{exception}}
                        RAISE EXCEPTION '庫存單據不可刪除（%），請改為取消', OLD.doc_no USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.status IN ('Posted', 'Cancelled') THEN
                        RAISE EXCEPTION '已過帳或已取消的單據不可修改（%）', OLD.doc_no USING ERRCODE = 'restrict_violation';
                    END IF;
                {{lockFlag}}
                    RETURN NEW;
                END;
                $$;
                """;
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DocumentsProtect(allowScanCartDraftDelete: false));

            migrationBuilder.DropTable(
                name: "scan_cart_lines");

            migrationBuilder.DropTable(
                name: "scan_carts");

            migrationBuilder.DropColumn(
                name: "from_scan_cart",
                table: "stock_documents");
        }
    }
}
