using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M5_01_ItemBarcodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "item_barcodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    symbology = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    pack_quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    normalized_text = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    deactivated_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deactivated_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_barcodes", x => x.id);
                    table.CheckConstraint("ck_item_barcodes_deactivation", "is_active = (deactivated_at IS NULL) AND (deactivated_at IS NULL) = (deactivated_by_id IS NULL)");
                    table.CheckConstraint("ck_item_barcodes_pack_quantity", "pack_quantity > 0");
                    table.CheckConstraint("ck_item_barcodes_text_not_blank", "btrim(text) <> '' AND normalized_text <> ''");
                    table.ForeignKey(
                        name: "fk_item_barcodes_items_item_id",
                        column: x => x.item_id,
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_barcodes_item_id",
                table: "item_barcodes",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ux_item_barcodes_active_normalized_text",
                table: "item_barcodes",
                column: "normalized_text",
                unique: true,
                filter: "is_active");

            // 只停用、不刪除（與別名相同，沿用 M2_01 的 item_index_protect 與 item_index_no_truncate）；
            // 另加一個 trigger 禁止改包裝數量與碼制：改包裝數量要停用後重新登錄，舊紀錄保留可稽核。
            migrationBuilder.Sql("""
                CREATE FUNCTION item_barcode_protect_extra() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.pack_quantity <> OLD.pack_quantity OR NEW.symbology IS DISTINCT FROM OLD.symbology THEN
                        RAISE EXCEPTION '原廠條碼的包裝數量與碼制不可修改，請停用後重新登錄（%）', OLD.text
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER trg_item_barcodes_protect BEFORE UPDATE OR DELETE ON item_barcodes
                    FOR EACH ROW EXECUTE FUNCTION item_index_protect();
                CREATE TRIGGER trg_item_barcodes_protect_extra BEFORE UPDATE ON item_barcodes
                    FOR EACH ROW EXECUTE FUNCTION item_barcode_protect_extra();
                CREATE TRIGGER trg_item_barcodes_no_truncate BEFORE TRUNCATE ON item_barcodes
                    FOR EACH STATEMENT EXECUTE FUNCTION item_index_no_truncate();
                """);
        }

        /// <inheritdoc />
        /// <remarks>僅供開發／測試：會刪除全部原廠條碼。正式環境回滾保留資料表，只切回舊版程式（docs/11 §10）。</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_item_barcodes_protect ON item_barcodes;
                DROP TRIGGER IF EXISTS trg_item_barcodes_protect_extra ON item_barcodes;
                DROP TRIGGER IF EXISTS trg_item_barcodes_no_truncate ON item_barcodes;
                DROP FUNCTION IF EXISTS item_barcode_protect_extra();
                """);

            migrationBuilder.DropTable(
                name: "item_barcodes");
        }
    }
}
