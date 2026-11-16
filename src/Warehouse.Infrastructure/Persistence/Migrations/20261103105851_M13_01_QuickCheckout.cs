using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M13_01_QuickCheckout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "project_reference",
                table: "stock_documents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recipient_name",
                table: "stock_documents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "recipient_user_id",
                table: "stock_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_project_reference",
                table: "stock_documents",
                column: "project_reference");

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_recipient_user_id",
                table: "stock_documents",
                column: "recipient_user_id");

            migrationBuilder.AddForeignKey(
                name: "fk_stock_documents_users_recipient_user_id",
                table: "stock_documents",
                column: "recipient_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_stock_documents_users_recipient_user_id",
                table: "stock_documents");

            migrationBuilder.DropIndex(
                name: "ix_stock_documents_project_reference",
                table: "stock_documents");

            migrationBuilder.DropIndex(
                name: "ix_stock_documents_recipient_user_id",
                table: "stock_documents");

            migrationBuilder.DropColumn(
                name: "project_reference",
                table: "stock_documents");

            migrationBuilder.DropColumn(
                name: "recipient_name",
                table: "stock_documents");

            migrationBuilder.DropColumn(
                name: "recipient_user_id",
                table: "stock_documents");
        }
    }
}
