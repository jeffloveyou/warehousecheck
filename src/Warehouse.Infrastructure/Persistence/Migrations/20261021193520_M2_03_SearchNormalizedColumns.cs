using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_03_SearchNormalizedColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "normalized_brand",
                table: "items",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "normalized_specification",
                table: "items",
                type: "text",
                nullable: true);

            // 既有資料回填：近似 TextNormalizer.Normalize（與 M1_02 回填相同的限制，見 docs/04 §3.8）
            migrationBuilder.Sql("""
                UPDATE items SET
                    normalized_brand = CASE WHEN brand IS NULL THEN NULL
                        ELSE upper(btrim(regexp_replace(normalize(brand, NFKC), '\s+', ' ', 'g'))) END,
                    normalized_specification = CASE WHEN specification IS NULL THEN NULL
                        ELSE upper(btrim(regexp_replace(normalize(specification, NFKC), '\s+', ' ', 'g'))) END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "normalized_brand",
                table: "items");

            migrationBuilder.DropColumn(
                name: "normalized_specification",
                table: "items");
        }
    }
}
