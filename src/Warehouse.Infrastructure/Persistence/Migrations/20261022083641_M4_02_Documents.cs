using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4_02_Documents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_documents_type",
                table: "stock_documents");

            migrationBuilder.DropIndex(
                name: "ux_stock_document_lines_key",
                table: "stock_document_lines");

            migrationBuilder.CreateSequence(
                name: "inbound_doc_no_seq");

            migrationBuilder.CreateSequence(
                name: "outbound_doc_no_seq");

            migrationBuilder.CreateSequence(
                name: "transfer_doc_no_seq");

            migrationBuilder.AddColumn<string>(
                name: "external_no",
                table: "stock_documents",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "partner_id",
                table: "stock_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "purpose_id",
                table: "stock_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reversal_of_document_id",
                table: "stock_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reservation_id",
                table: "stock_document_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "target_location_id",
                table: "stock_document_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "business_partners",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    partner_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_business_partners", x => x.id);
                    table.CheckConstraint("ck_business_partners_type", "partner_type IN ('Vendor', 'Customer', 'Department')");
                });

            migrationBuilder.CreateTable(
                name: "document_purposes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_purposes", x => x.id);
                    table.CheckConstraint("ck_document_purposes_direction", "direction IN ('In', 'Out')");
                });

            migrationBuilder.InsertData(
                table: "document_purposes",
                columns: new[] { "id", "code", "direction", "is_active", "name", "sort_order" },
                values: new object[,]
                {
                    { new Guid("0b1f6a10-0003-4c3a-9a00-000000000001"), "PURCHASE", "In", true, "採購入庫", 10 },
                    { new Guid("0b1f6a10-0003-4c3a-9a00-000000000002"), "PRODUCTION", "In", true, "生產入庫", 20 },
                    { new Guid("0b1f6a10-0003-4c3a-9a00-000000000003"), "RETURN-MATERIAL", "In", true, "退料入庫", 30 },
                    { new Guid("0b1f6a10-0003-4c3a-9a00-000000000004"), "CUSTOMER-RETURN", "In", true, "客戶退貨", 40 },
                    { new Guid("0b1f6a10-0003-4c3a-9a00-000000000005"), "OTHER-IN", "In", true, "其他入庫", 90 },
                    { new Guid("0b1f6a10-0003-4c3a-9a00-000000000011"), "PRODUCTION", "Out", true, "生產領料", 10 },
                    { new Guid("0b1f6a10-0003-4c3a-9a00-000000000012"), "REPAIR", "Out", true, "維修領用", 20 },
                    { new Guid("0b1f6a10-0003-4c3a-9a00-000000000013"), "SCRAP", "Out", true, "報廢", 30 },
                    { new Guid("0b1f6a10-0003-4c3a-9a00-000000000014"), "VENDOR-RETURN", "Out", true, "退貨給廠商", 40 },
                    { new Guid("0b1f6a10-0003-4c3a-9a00-000000000015"), "SALES", "Out", true, "銷售出貨", 50 },
                    { new Guid("0b1f6a10-0003-4c3a-9a00-000000000016"), "OTHER-OUT", "Out", true, "其他出庫", 90 }
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_partner_id",
                table: "stock_documents",
                column: "partner_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_purpose_id",
                table: "stock_documents",
                column: "purpose_id");

            migrationBuilder.CreateIndex(
                name: "ux_stock_documents_reversal",
                table: "stock_documents",
                column: "reversal_of_document_id",
                unique: true,
                filter: "reversal_of_document_id IS NOT NULL AND status <> 'Cancelled'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_documents_type",
                table: "stock_documents",
                sql: "doc_type IN ('Opening', 'Inbound', 'Outbound', 'Transfer')");

            migrationBuilder.CreateIndex(
                name: "ix_stock_document_lines_reservation_id",
                table: "stock_document_lines",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_document_lines_target_location_id",
                table: "stock_document_lines",
                column: "target_location_id");

            migrationBuilder.CreateIndex(
                name: "ux_stock_document_lines_key",
                table: "stock_document_lines",
                columns: new[] { "document_id", "location_id", "target_location_id", "item_id", "lot_no", "serial_no" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_document_lines_target",
                table: "stock_document_lines",
                sql: "target_location_id IS NULL OR target_location_id <> location_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_partners_partner_type_code",
                table: "business_partners",
                columns: new[] { "partner_type", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_business_partners_partner_type_name",
                table: "business_partners",
                columns: new[] { "partner_type", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_document_purposes_direction_code",
                table: "document_purposes",
                columns: new[] { "direction", "code" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_stock_document_lines_locations_target_location_id",
                table: "stock_document_lines",
                column: "target_location_id",
                principalTable: "locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stock_document_lines_stock_reservations_reservation_id",
                table: "stock_document_lines",
                column: "reservation_id",
                principalTable: "stock_reservations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stock_documents_business_partners_partner_id",
                table: "stock_documents",
                column: "partner_id",
                principalTable: "business_partners",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stock_documents_document_purposes_purpose_id",
                table: "stock_documents",
                column: "purpose_id",
                principalTable: "document_purposes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stock_documents_stock_documents_reversal_of_document_id",
                table: "stock_documents",
                column: "reversal_of_document_id",
                principalTable: "stock_documents",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // 明細保護 trigger：新欄位（目的儲位、預留）也納入「送出後不可改」的比對（ADR-0014 決策 3）
            migrationBuilder.Sql(LinesProtect(withMovementColumns: true));
        }

        /// <summary>stock_document_lines_protect()：草稿可改；送出後只允許過帳流程填入 lot_id／serial_id。</summary>
        private static string LinesProtect(bool withMovementColumns)
        {
            var extra = withMovementColumns ? ", NEW.target_location_id, NEW.reservation_id" : "";
            var extraOld = withMovementColumns ? ", OLD.target_location_id, OLD.reservation_id" : "";
            return $$"""
                CREATE OR REPLACE FUNCTION stock_document_lines_protect() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    doc_status text;
                BEGIN
                    -- 不可把明細搬到別張單（否則已過帳的明細可被移到草稿單再修改，M3-03 審查 #3）
                    IF TG_OP = 'UPDATE' AND NEW.document_id <> OLD.document_id THEN
                        RAISE EXCEPTION '明細不可移到其他單據' USING ERRCODE = 'restrict_violation';
                    END IF;
                    SELECT status INTO doc_status FROM stock_documents WHERE id = COALESCE(NEW.document_id, OLD.document_id);
                    IF doc_status = 'Draft' THEN
                        RETURN COALESCE(NEW, OLD);
                    END IF;
                    IF TG_OP = 'UPDATE' AND doc_status = 'Submitted'
                       AND (NEW.document_id, NEW.line_no, NEW.item_id, NEW.location_id, NEW.lot_no, NEW.serial_no, NEW.expiry_date, NEW.quantity{{extra}})
                           IS NOT DISTINCT FROM
                           (OLD.document_id, OLD.line_no, OLD.item_id, OLD.location_id, OLD.lot_no, OLD.serial_no, OLD.expiry_date, OLD.quantity{{extraOld}}) THEN
                        RETURN NEW; -- 過帳時填入 lot_id／serial_id
                    END IF;
                    RAISE EXCEPTION '只有草稿單據可以修改明細' USING ERRCODE = 'restrict_violation';
                END;
                $$;
                """;
        }

        /// <inheritdoc />
        /// <remarks>僅供開發／測試：進出倉單會失去表頭與目的儲位；若已有「只差目的儲位」的移庫明細，重建舊唯一索引會失敗。</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LinesProtect(withMovementColumns: false));

            migrationBuilder.DropForeignKey(
                name: "fk_stock_document_lines_locations_target_location_id",
                table: "stock_document_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_stock_document_lines_stock_reservations_reservation_id",
                table: "stock_document_lines");

            migrationBuilder.DropForeignKey(
                name: "fk_stock_documents_business_partners_partner_id",
                table: "stock_documents");

            migrationBuilder.DropForeignKey(
                name: "fk_stock_documents_document_purposes_purpose_id",
                table: "stock_documents");

            migrationBuilder.DropForeignKey(
                name: "fk_stock_documents_stock_documents_reversal_of_document_id",
                table: "stock_documents");

            migrationBuilder.DropTable(
                name: "business_partners");

            migrationBuilder.DropTable(
                name: "document_purposes");

            migrationBuilder.DropIndex(
                name: "ix_stock_documents_partner_id",
                table: "stock_documents");

            migrationBuilder.DropIndex(
                name: "ix_stock_documents_purpose_id",
                table: "stock_documents");

            migrationBuilder.DropIndex(
                name: "ux_stock_documents_reversal",
                table: "stock_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_documents_type",
                table: "stock_documents");

            migrationBuilder.DropIndex(
                name: "ix_stock_document_lines_reservation_id",
                table: "stock_document_lines");

            migrationBuilder.DropIndex(
                name: "ix_stock_document_lines_target_location_id",
                table: "stock_document_lines");

            migrationBuilder.DropIndex(
                name: "ux_stock_document_lines_key",
                table: "stock_document_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_document_lines_target",
                table: "stock_document_lines");

            migrationBuilder.DropColumn(
                name: "external_no",
                table: "stock_documents");

            migrationBuilder.DropColumn(
                name: "partner_id",
                table: "stock_documents");

            migrationBuilder.DropColumn(
                name: "purpose_id",
                table: "stock_documents");

            migrationBuilder.DropColumn(
                name: "reversal_of_document_id",
                table: "stock_documents");

            migrationBuilder.DropColumn(
                name: "reservation_id",
                table: "stock_document_lines");

            migrationBuilder.DropColumn(
                name: "target_location_id",
                table: "stock_document_lines");

            migrationBuilder.DropSequence(
                name: "inbound_doc_no_seq");

            migrationBuilder.DropSequence(
                name: "outbound_doc_no_seq");

            migrationBuilder.DropSequence(
                name: "transfer_doc_no_seq");

            // NOT VALID：已有進出倉單時仍可降版（舊版只讀寫期初單）
            migrationBuilder.Sql(
                "ALTER TABLE stock_documents ADD CONSTRAINT ck_stock_documents_type CHECK (doc_type IN ('Opening')) NOT VALID;");

            migrationBuilder.CreateIndex(
                name: "ux_stock_document_lines_key",
                table: "stock_document_lines",
                columns: new[] { "document_id", "location_id", "item_id", "lot_no", "serial_no" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
