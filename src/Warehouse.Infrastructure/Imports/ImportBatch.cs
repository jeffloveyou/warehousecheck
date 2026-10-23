using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Warehouse.Application.Imports;

namespace Warehouse.Infrastructure.Imports;

/// <summary>
/// 匯入批次（import_batches，M6-03）：保存解析後的列（JSON）與試算問題，確認時依保存的列執行，不必重新上傳。
/// 確認後狀態改 Committed；同一批次只能確認一次（鎖批次列後檢查狀態）。
/// </summary>
public sealed class ImportBatch
{
    public Guid Id { get; set; }

    public ImportKind Kind { get; set; }

    public ImportStatus Status { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>上傳檔案的 SHA-256（稽核用）。</summary>
    public string FileHash { get; set; } = string.Empty;

    public Guid? TargetId { get; set; }

    /// <summary>解析後的列：[{ "row": 2, "cells": { "品名": "…" } }]。</summary>
    public string RowsJson { get; set; } = "[]";

    /// <summary>試算（或確認失敗）的問題清單。</summary>
    public string IssuesJson { get; set; } = "[]";

    public int RowCount { get; set; }

    public int ErrorCount { get; set; }

    public int WarningCount { get; set; }

    public Guid CreatedById { get; set; }

    public string CreatedByName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CommittedAt { get; set; }
}

public class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> builder)
    {
        builder.ToTable("import_batches", t =>
        {
            t.HasCheckConstraint("ck_import_batches_kind", "kind IN ('Items', 'Indexes', 'Locations', 'OpeningLines')");
            t.HasCheckConstraint("ck_import_batches_status", "status IN ('Validated', 'Committed')");
        });
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(b => b.FileName).HasMaxLength(200);
        builder.Property(b => b.FileHash).HasMaxLength(64);
        builder.Property(b => b.RowsJson).HasColumnType("jsonb");
        builder.Property(b => b.IssuesJson).HasColumnType("jsonb");
        builder.Property(b => b.CreatedByName).HasMaxLength(100);
        builder.HasIndex(b => new { b.CreatedById, b.CreatedAt });
    }
}
