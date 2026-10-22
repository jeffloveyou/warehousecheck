using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4_04_ReservationExpiryAndFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at",
                table: "stock_reservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "stock_posting_failures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    command = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    doc_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    error_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    trace_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_posting_failures", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_expiry",
                table: "stock_reservations",
                column: "expires_at",
                filter: "status = 'Open' AND expires_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_occurred_at",
                table: "stock_movements",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_stock_posting_failures_occurred_at",
                table: "stock_posting_failures",
                column: "occurred_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stock_posting_failures");

            migrationBuilder.DropIndex(
                name: "ix_stock_reservations_expiry",
                table: "stock_reservations");

            migrationBuilder.DropIndex(
                name: "ix_stock_movements_occurred_at",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "stock_reservations");
        }
    }
}
