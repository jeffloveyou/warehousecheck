using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M8_01_WarehouseSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "warehouses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deleted_by",
                table: "warehouses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_warehouses_deleted_inactive",
                table: "warehouses",
                sql: "deleted_at IS NULL OR NOT is_active");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_warehouses_deleted_inactive",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "deleted_by",
                table: "warehouses");
        }
    }
}
