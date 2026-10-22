using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Inventory;
using Warehouse.Application.Warehousing;
using Warehouse.Domain.Warehousing;
using Warehouse.Infrastructure;
using Warehouse.Infrastructure.Persistence;

namespace FieldBench;

/// <summary>
/// seed：在量測資料庫（warehouse_bench，連線字串取自環境變數 FIELD_BENCH_DB）建立量測資料。
/// 1. 套用全部 Migration（只限量測庫；資料庫名稱不是 warehouse_bench 就拒絕）。
/// 2. 2 萬筆有效物料、2 萬個專屬別名、4.2 萬個共用關鍵字（與 M2-05 量測相同，docs/10 §4.1）。
/// 3. 倉庫 B1、儲位 A-01～A-50，一張期初單放前 50 筆物料各 10 個並核准；A-01 放 3 張圖片。
/// 已經建立過就略過，可重複執行。
/// </summary>
public static class Seed
{
    private const int Items = 20_000;
    private static readonly Actor Creator = new(Guid.Parse("0f1e1d00-0000-4000-8000-000000000001"), "量測建立者", false, IsWarehouseManager: true);
    private static readonly Actor Approver = new(Guid.Parse("0f1e1d00-0000-4000-8000-000000000002"), "量測核准者", false, IsWarehouseManager: true);

    public static async Task<int> RunAsync(string connectionString, string attachmentsDirectory)
    {
        if (new NpgsqlConnectionStringBuilder(connectionString).Database != "warehouse_bench")
        {
            throw new InvalidOperationException("seed 只能用在 warehouse_bench");
        }
        var services = new ServiceCollection()
            .AddLogging()
            .AddWarehouseInfrastructure(connectionString, attachmentsDirectory)
            .BuildServiceProvider();
        var sw = Stopwatch.StartNew();

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            await db.Database.MigrateAsync();
            Console.WriteLine($"Migration 完成（{sw.Elapsed.TotalSeconds:0.0} 秒）");
            if (!await db.Items.AnyAsync(i => i.RequestNo.StartsWith("MR-9")))
            {
                db.Database.SetCommandTimeout(600);
                await db.Database.ExecuteSqlRawAsync(ItemsSql);
                Console.WriteLine($"物料、別名、關鍵字完成（{sw.Elapsed.TotalSeconds:0.0} 秒）");
            }
        }

        Guid warehouseId;
        using (var scope = services.CreateScope())
        {
            var warehouses = scope.ServiceProvider.GetRequiredService<IWarehouseService>();
            if ((await warehouses.ListWarehousesAsync(default)).FirstOrDefault(w => w.Code == "B1") is { } existing)
            {
                Console.WriteLine("倉庫 B1 已存在，略過儲位、期初與圖片");
                return 0;
            }
            warehouseId = await warehouses.CreateWarehouseAsync(new WarehouseInput("B1", "量測倉"), Creator, default);
            var empty = new LocationDetails(null, null, null, null, null, null, null, null, false, false);
            for (var n = 1; n <= 50; n++)
            {
                await warehouses.CreateLocationAsync(warehouseId, new LocationInput($"A-{n:00}", empty), Creator, default);
            }
        }

        List<string> codes;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            codes = await db.Items.Where(i => i.RequestNo.StartsWith("MR-9")).OrderBy(i => i.RequestNo).Take(50).Select(i => i.ItemCode!).ToListAsync();
        }
        Guid documentId;
        using (var scope = services.CreateScope())
        {
            var opening = scope.ServiceProvider.GetRequiredService<IOpeningService>();
            documentId = await opening.CreateAsync(warehouseId, "量測期初", Creator, default);
            for (var n = 0; n < codes.Count; n++)
            {
                await opening.AddLineAsync(documentId, new OpeningLineInput($"A-{n + 1:00}", codes[n], null, null, null, 10m), Creator, default);
            }
        }
        await WithVersion(services, documentId, (s, v) => s.SubmitAsync(documentId, v, Creator, default));
        await WithVersion(services, documentId, (s, v) => s.ApproveAsync(documentId, v, "seed-" + documentId, Approver, default));
        Console.WriteLine($"倉庫、50 個儲位、期初過帳完成（{sw.Elapsed.TotalSeconds:0.0} 秒）");

        using (var scope = services.CreateScope())
        {
            var warehouses = scope.ServiceProvider.GetRequiredService<IWarehouseService>();
            var images = scope.ServiceProvider.GetRequiredService<ILocationImageService>();
            var location = (await warehouses.GetLocationByCodeAsync("B1", "A-01", default))!;
            for (var n = 0; n < 3; n++)
            {
                var png = Png.Gradient(2400, 1800, n);
                await images.UploadAsync(location.Id, new MemoryStream(png), png.Length, Creator, default);
            }
        }
        Console.WriteLine($"圖片完成（{sw.Elapsed.TotalSeconds:0.0} 秒）");
        return 0;
    }

    private static async Task WithVersion(ServiceProvider services, Guid documentId, Func<IOpeningService, uint, Task> action)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var version = await db.StockDocuments.Where(d => d.Id == documentId).Select(d => EF.Property<uint>(d, "Version")).SingleAsync();
        await action(scope.ServiceProvider.GetRequiredService<IOpeningService>(), version);
    }

    // 與 M2-05 searchbench 相同的資料形狀（docs/10 §4.1）；分類、單位用種子資料的固定 id
    private static readonly string ItemsSql = $"""
        INSERT INTO items (id, request_no, item_code, status, name, category_id, base_unit_id, tracking_mode, brand,
            manufacturer_part_no, specification, allow_negative_stock, negative_stock_limit, created_at, updated_at,
            normalized_name, normalized_part_no, normalized_brand, normalized_specification)
        SELECT gen_random_uuid(), 'MR-9' || lpad(g::text, 6, '0'), 'PF-' || lpad(g::text, 6, '0'), 'Active',
            n.name, '0b1f6a10-0002-4c3a-9a00-000000000002', '0b1f6a10-0001-4c3a-9a00-000000000001', 'None',
            b.brand, 'PN-' || g, 'DC24V 規格說明 ' || g, false, 0, now(), now() - (g || ' seconds')::interval,
            upper(n.name), 'PN' || g, upper(b.brand), upper('DC24V 規格說明 ' || g)
        FROM generate_series(1, {Items}) g
        CROSS JOIN LATERAL (SELECT '量測物料 ' || g || ' ' || (ARRAY['伺服','馬達','感測器','電纜','螺絲','軸承','閥','繼電器'])[1 + g % 8] AS name) n
        CROSS JOIN LATERAL (SELECT (ARRAY['三菱','SMC','OMRON','台達'])[1 + g % 4] AS brand) b;

        INSERT INTO item_aliases (id, item_id, text, normalized_text, created_by_id, created_by_name, created_at, is_active)
        SELECT gen_random_uuid(), id, '別名' || substring(request_no FROM 4), '別名' || substring(request_no FROM 4),
            gen_random_uuid(), '量測', now(), true
        FROM items WHERE request_no LIKE 'MR-9%';

        INSERT INTO item_keywords (id, item_id, text, normalized_text, created_by_id, created_by_name, created_at, is_active)
        SELECT gen_random_uuid(), id, t, upper(t), gen_random_uuid(), '量測', now(), true
        FROM (SELECT id, substring(request_no FROM 4)::int AS g FROM items WHERE request_no LIKE 'MR-9%') x
        CROSS JOIN LATERAL (VALUES ('俗稱' || (x.g % 5000)), ('用途' || (x.g % 10))) v(t);

        INSERT INTO item_keywords (id, item_id, text, normalized_text, created_by_id, created_by_name, created_at, is_active)
        SELECT gen_random_uuid(), id, '量測AMP', '量測AMP', gen_random_uuid(), '量測', now(), true
        FROM items WHERE request_no LIKE 'MR-9%' AND substring(request_no FROM 4)::int <= 2000;

        ANALYZE items; ANALYZE item_aliases; ANALYZE item_keywords;
        """;
}

/// <summary>不靠影像套件產生 PNG（漸層），給 seed 上傳圖片用。</summary>
public static class Png
{
    public static byte[] Gradient(int width, int height, int seed)
    {
        var raw = new byte[(width * 3 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 3 + 1);
            for (var x = 0; x < width; x++)
            {
                raw[row + 1 + x * 3] = (byte)(x * 255 / width);
                raw[row + 2 + x * 3] = (byte)(y * 255 / height);
                raw[row + 3 + x * 3] = (byte)(seed * 80);
            }
        }
        using var compressed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw);
        }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(body);
        var crc = 0xFFFFFFFFu;
        foreach (var b in body)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        var tail = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tail, crc ^ 0xFFFFFFFFu);
        stream.Write(tail);
    }
}
