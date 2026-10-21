using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M1_01_ItemsCategoriesUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "item_categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    item_code_prefix = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_categories", x => x.id);
                    table.CheckConstraint("ck_item_categories_code_format", "code ~ '^[A-Z0-9][A-Z0-9-]{1,19}$'");
                    table.CheckConstraint("ck_item_categories_prefix_format", "item_code_prefix ~ '^[A-Z0-9]{2,4}$'");
                    table.CheckConstraint("ck_item_categories_prefix_required", "parent_id IS NOT NULL OR item_code_prefix IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_item_categories_item_categories_parent_id",
                        column: x => x.parent_id,
                        principalTable: "item_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "units",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    decimal_places = table.Column<short>(type: "smallint", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_units", x => x.id);
                    table.CheckConstraint("ck_units_code_format", "code ~ '^[A-Z0-9]{1,16}$'");
                    table.CheckConstraint("ck_units_decimal_places", "decimal_places BETWEEN 0 AND 4");
                });

            migrationBuilder.CreateTable(
                name: "items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_no = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    item_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tracking_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    brand = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    manufacturer_part_no = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    specification = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    allow_negative_stock = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    negative_stock_limit = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false, defaultValue: 0m),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items", x => x.id);
                    table.CheckConstraint("ck_items_code_matches_status", "(status IN ('Active', 'Inactive') AND item_code IS NOT NULL) OR (status IN ('PendingApproval', 'Returned', 'Rejected') AND item_code IS NULL)");
                    table.CheckConstraint("ck_items_negative_stock_limit", "negative_stock_limit >= 0");
                    table.CheckConstraint("ck_items_negative_stock_tracking", "NOT allow_negative_stock OR tracking_mode = 'None'");
                    table.CheckConstraint("ck_items_status", "status IN ('PendingApproval', 'Returned', 'Rejected', 'Active', 'Inactive')");
                    table.CheckConstraint("ck_items_tracking_mode", "tracking_mode IN ('None', 'Lot', 'Serial')");
                    table.ForeignKey(
                        name: "fk_items_item_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "item_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_units_base_unit_id",
                        column: x => x.base_unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "item_categories",
                columns: new[] { "id", "code", "is_active", "item_code_prefix", "name", "parent_id", "sort_order" },
                values: new object[,]
                {
                    { new Guid("0b1f6a10-0002-4c3a-9a00-000000000001"), "ME", true, "ME", "零件", null, 10 },
                    { new Guid("0b1f6a10-0002-4c3a-9a00-000000000002"), "EL", true, "EL", "電控", null, 20 },
                    { new Guid("0b1f6a10-0002-4c3a-9a00-000000000003"), "RM", true, "RM", "原料", null, 30 },
                    { new Guid("0b1f6a10-0002-4c3a-9a00-000000000004"), "SF", true, "SF", "半成品", null, 40 },
                    { new Guid("0b1f6a10-0002-4c3a-9a00-000000000005"), "FG", true, "FG", "成品", null, 50 },
                    { new Guid("0b1f6a10-0002-4c3a-9a00-000000000006"), "CS", true, "CS", "耗材", null, 60 }
                });

            migrationBuilder.InsertData(
                table: "units",
                columns: new[] { "id", "code", "decimal_places", "is_active", "name" },
                values: new object[,]
                {
                    { new Guid("0b1f6a10-0001-4c3a-9a00-000000000001"), "PCS", (short)0, true, "個" },
                    { new Guid("0b1f6a10-0001-4c3a-9a00-000000000002"), "SET", (short)0, true, "組" },
                    { new Guid("0b1f6a10-0001-4c3a-9a00-000000000003"), "BOX", (short)0, true, "箱" },
                    { new Guid("0b1f6a10-0001-4c3a-9a00-000000000004"), "ROLL", (short)0, true, "卷" },
                    { new Guid("0b1f6a10-0001-4c3a-9a00-000000000005"), "KG", (short)3, true, "公斤" },
                    { new Guid("0b1f6a10-0001-4c3a-9a00-000000000006"), "G", (short)2, true, "公克" },
                    { new Guid("0b1f6a10-0001-4c3a-9a00-000000000007"), "M", (short)3, true, "公尺" },
                    { new Guid("0b1f6a10-0001-4c3a-9a00-000000000008"), "L", (short)3, true, "公升" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_categories_code",
                table: "item_categories",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_item_categories_parent_id",
                table: "item_categories",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_base_unit_id",
                table: "items",
                column: "base_unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_category_id",
                table: "items",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_item_code",
                table: "items",
                column: "item_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_request_no",
                table: "items",
                column: "request_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_status",
                table: "items",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_units_code",
                table: "units",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "items");

            migrationBuilder.DropTable(
                name: "item_categories");

            migrationBuilder.DropTable(
                name: "units");
        }
    }
}
