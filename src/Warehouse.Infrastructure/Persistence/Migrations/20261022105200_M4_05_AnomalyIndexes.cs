using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4_05_AnomalyIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_status_created_at",
                table: "stock_documents",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_balances_negative",
                table: "stock_balances",
                column: "item_id",
                filter: "quantity < 0");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_document_events",
                table: "audit_logs",
                column: "occurred_at",
                filter: "entity_type = 'stock_document' AND action IN ('document.cancel', 'document.return', 'document.reversal_create')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stock_documents_status_created_at",
                table: "stock_documents");

            migrationBuilder.DropIndex(
                name: "ix_stock_balances_negative",
                table: "stock_balances");

            migrationBuilder.DropIndex(
                name: "ix_audit_logs_document_events",
                table: "audit_logs");
        }
    }
}
