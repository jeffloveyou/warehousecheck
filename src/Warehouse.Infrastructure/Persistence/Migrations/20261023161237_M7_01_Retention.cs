using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// M7 資料保留（ADR-0021）：清理用的建立時間索引，以及操作日誌的受控刪除函式。只新增（Expand），舊版不受影響。
    /// </summary>
    public partial class M7_01_Retention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_processed_commands_created_at",
                table: "processed_commands",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_created_at",
                table: "import_batches",
                column: "created_at");

            // 操作日誌仍是「只能新增」，唯一的刪除途徑是 purge_audit_logs：
            // - SECURITY DEFINER，以擁有者（Migration 帳號）身分執行；函式內設交易內旗標，trigger 只在
            //   「旗標開啟＋目前角色是資料表擁有者」時放行。網站帳號不是擁有者（M7-01），自己設旗標也刪不了。
            // - 下限寫死在資料庫：登入紀錄（auth.*）至少 365 天、其他至少 7 年，應用程式設定改不低。
            // - 每次最多刪 p_limit 筆（由舊到新），清理工作分批呼叫，避免長交易與大量鎖。
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION audit_logs_append_only() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE'
                       AND current_setting('warehouse.audit_purge', true) = 'on'
                       AND current_user = (SELECT r.rolname FROM pg_class c JOIN pg_roles r ON r.oid = c.relowner WHERE c.oid = TG_RELID) THEN
                        RETURN OLD;
                    END IF;
                    RAISE EXCEPTION '稽核紀錄只能新增，不可修改或刪除' USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE FUNCTION purge_audit_logs(p_before timestamptz, p_logins boolean, p_limit integer) RETURNS integer
                LANGUAGE plpgsql SECURITY DEFINER SET search_path FROM CURRENT AS $$
                DECLARE
                    deleted integer;
                BEGIN
                    IF p_logins AND p_before > now() - interval '365 days' THEN
                        RAISE EXCEPTION '登入紀錄至少保留 365 天' USING ERRCODE = 'check_violation';
                    END IF;
                    IF NOT p_logins AND p_before > now() - interval '7 years' THEN
                        RAISE EXCEPTION '操作日誌至少保留 7 年' USING ERRCODE = 'check_violation';
                    END IF;
                    PERFORM set_config('warehouse.audit_purge', 'on', true);
                    WITH doomed AS (
                        SELECT id FROM audit_logs
                        WHERE occurred_at < p_before AND (action LIKE 'auth.%') = p_logins
                        ORDER BY occurred_at
                        LIMIT greatest(p_limit, 0))
                    DELETE FROM audit_logs a USING doomed d WHERE a.id = d.id;
                    GET DIAGNOSTICS deleted = ROW_COUNT;
                    PERFORM set_config('warehouse.audit_purge', 'off', true);
                    RETURN deleted;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS purge_audit_logs(timestamptz, boolean, integer);
                CREATE OR REPLACE FUNCTION audit_logs_append_only() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '稽核紀錄只能新增，不可修改或刪除' USING ERRCODE = 'restrict_violation';
                END;
                $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_processed_commands_created_at",
                table: "processed_commands");

            migrationBuilder.DropIndex(
                name: "ix_import_batches_created_at",
                table: "import_batches");
        }
    }
}
