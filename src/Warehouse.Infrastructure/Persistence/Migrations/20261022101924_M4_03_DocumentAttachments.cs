using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4_03_DocumentAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stock_document_attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_pdf = table.Column<bool>(type: "boolean", nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    file_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    thumb_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_by_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_document_attachments", x => x.id);
                    table.CheckConstraint("ck_stock_document_attachments_kind", "(is_pdf AND thumb_key IS NULL) OR (NOT is_pdf AND thumb_key IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_stock_document_attachments_stock_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "stock_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_document_attachments_document_id_is_active",
                table: "stock_document_attachments",
                columns: new[] { "document_id", "is_active" });

            // 附件保護（使用者 2026-10-22）：不可刪除；單據送出或過帳後不可移除（停用）或改內容，只能新增
            migrationBuilder.Sql("""
                CREATE FUNCTION stock_document_attachments_protect() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    doc_status text;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION '單據附件不可刪除' USING ERRCODE = 'restrict_violation';
                    END IF;
                    SELECT status INTO doc_status FROM stock_documents WHERE id = OLD.document_id;
                    IF doc_status <> 'Draft' THEN
                        RAISE EXCEPTION '單據送出後，附件只能新增、不能修改或移除' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF (NEW.id, NEW.document_id, NEW.file_key, NEW.thumb_key, NEW.is_pdf, NEW.size_bytes)
                       IS DISTINCT FROM (OLD.id, OLD.document_id, OLD.file_key, OLD.thumb_key, OLD.is_pdf, OLD.size_bytes) THEN
                        RAISE EXCEPTION '附件內容不可修改' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER trg_stock_document_attachments_protect BEFORE UPDATE OR DELETE ON stock_document_attachments
                    FOR EACH ROW EXECUTE FUNCTION stock_document_attachments_protect();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_stock_document_attachments_protect ON stock_document_attachments; DROP FUNCTION IF EXISTS stock_document_attachments_protect();");

            migrationBuilder.DropTable(
                name: "stock_document_attachments");
        }
    }
}
