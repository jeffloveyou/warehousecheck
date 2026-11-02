using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M12_01_LineKeyReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_stock_document_lines_key",
                table: "stock_document_lines");

            migrationBuilder.CreateIndex(
                name: "ux_stock_document_lines_key",
                table: "stock_document_lines",
                columns: new[] { "document_id", "location_id", "target_location_id", "item_id", "lot_no", "serial_no", "reservation_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_stock_document_lines_key",
                table: "stock_document_lines");

            migrationBuilder.CreateIndex(
                name: "ux_stock_document_lines_key",
                table: "stock_document_lines",
                columns: new[] { "document_id", "location_id", "target_location_id", "item_id", "lot_no", "serial_no" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
