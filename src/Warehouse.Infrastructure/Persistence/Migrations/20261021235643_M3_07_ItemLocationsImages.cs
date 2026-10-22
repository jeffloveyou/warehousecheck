using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3_07_ItemLocationsImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "item_locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_locations", x => x.id);
                    table.ForeignKey(
                        name: "fk_item_locations_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_item_locations_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_item_locations_warehouses_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "location_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    large_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    thumb_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    large_bytes = table.Column<long>(type: "bigint", nullable: false),
                    thumb_bytes = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    deactivated_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_location_images", x => x.id);
                    table.CheckConstraint("ck_location_images_deactivation", "is_active = (deactivated_at IS NULL)");
                    table.ForeignKey(
                        name: "fk_location_images_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_locations_item_id_location_id",
                table: "item_locations",
                columns: new[] { "item_id", "location_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_item_locations_location_id",
                table: "item_locations",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_item_locations_warehouse_id",
                table: "item_locations",
                column: "warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ux_item_locations_default",
                table: "item_locations",
                columns: new[] { "item_id", "warehouse_id" },
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_location_images_location_id_is_active",
                table: "location_images",
                columns: new[] { "location_id", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_locations");

            migrationBuilder.DropTable(
                name: "location_images");
        }
    }
}
