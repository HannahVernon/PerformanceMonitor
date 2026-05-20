/*
 * ParquetCompaction — the parquet merge logic used by ArchiveService.CompactParquetFiles.
 *
 * Extracted into a standalone, dependency-free static class so the standalone
 * reproducer (tools/CompactionRepro) can link this exact source and exercise
 * the *real* production merge path. Before this extraction the reproducer kept
 * its own hand-copied merge loops, which silently drifted from production —
 * fixes "passed the repro" while still OOMing on real installs (see #933).
 *
 * This file must stay free of DI, logging, and project dependencies: it is
 * compiled into both the Lite assembly and the CompactionRepro assembly.
 */

using System.IO;
using DuckDB.NET.Data;

namespace PerformanceMonitorLite.Services;

public static class ParquetCompaction
{
    /* Production tuning for the compaction merge connections (#933 followup).
       The reproducer overrides these to sweep values; ArchiveService uses the
       defaults so the production knobs live in exactly one place. */
    public const string DefaultMemoryLimit = "4GB";
    public const int DefaultThreads = 2;
    public const int DefaultRowGroupSize = 8192;

    /* Maximum total on-disk parquet bytes per compaction merge batch. Wide-VARCHAR
       tables (query_snapshots) expand 5-10x on read; this cap keeps the in-memory
       working set during a COPY well below the 4 GB compaction memory_limit even
       on the worst data shapes. Groups exceeding this budget produce multiple
       _ptNNN.parquet output files. See #933 followup — a 72-file query_snapshots
       backlog at 4 GB OOM'd on real allocation pressure during the final merge. */
    public const long MaxBatchInputBytes = 200L * 1024 * 1024; /* 200 MB */

    /* Columns to exclude during compaction — dead weight from legacy archives */
    private static readonly Dictionary<string, string[]> CompactionExcludeColumns = new()
    {
        ["query_store_stats"] = ["query_plan_text"]
    };

    private static string EscapeSqlPath(string path) => path.Replace("'", "''");

    /* Greedily group <paramref name="sortedPaths"/> (smallest-first) into batches
       whose total on-disk bytes don't exceed <paramref name="maxBytes"/>. A single
       file larger than the cap becomes its own one-element batch — that's the
       degenerate case (the cap can't split an individual file) and the caller
       handles it as a single-file pass-through merge. */
    public static List<List<string>> BuildSizeBudgetedBatches(IReadOnlyList<string> sortedPaths, long maxBytes)
    {
        var batches = new List<List<string>>();
        var current = new List<string>();
        long currentBytes = 0;

        foreach (var p in sortedPaths)
        {
            var size = new FileInfo(p.Replace("/", "\\")).Length;
            if (currentBytes + size > maxBytes && current.Count > 0)
            {
                batches.Add(current);
                current = new List<string>();
                currentBytes = 0;
            }
            current.Add(p);
            currentBytes += size;
        }
        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    /* Merge one size-budgeted batch into <paramref name="outputPath"/>. The pragma
       block matches the compaction tuning from #933:
         - memory_limit = 4GB: parquet COPY does allocations that bypass the buffer
           manager and can't be spilled. The cap is a hard ceiling for those, not
           a spill trigger. 4GB leaves real headroom for wide-VARCHAR data within
           the batch-size budget. Aligns with DuckDB's OOM guide (50-60% of RAM).
         - threads = 2: fewer per-thread row-group buffers in flight.
         - ROW_GROUP_SIZE 8192: smaller buffered batch per row group.
         - preserve_insertion_order = false: lets DuckDB stream.
       The memoryLimit/threads/rowGroupSize parameters exist so tools/CompactionRepro
       can sweep them; production callers omit them and get the defaults above. */
    public static void MergeBatchToFile(
        string table,
        List<string> sourcePaths,
        string outputPath,
        string spillDirSql,
        string memoryLimit = DefaultMemoryLimit,
        int threads = DefaultThreads,
        int rowGroupSize = DefaultRowGroupSize)
    {
        var pragma = BuildPragma(memoryLimit, threads, spillDirSql);

        if (sourcePaths.Count <= 2)
        {
            /* Small batch — single-pass merge (also covers the degenerate 1-file case). */
            using var con = new DuckDBConnection("DataSource=:memory:");
            con.Open();
            using (var pragmaCmd = con.CreateCommand())
            {
                pragmaCmd.CommandText = pragma;
                pragmaCmd.ExecuteNonQuery();
            }

            var selectClause = BuildSelectClause(table, sourcePaths);
            var pathList = string.Join(", ", sourcePaths.Select(p => $"'{EscapeSqlPath(p)}'"));
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"COPY (SELECT {selectClause} FROM read_parquet([{pathList}], union_by_name=true)) " +
                              $"TO '{EscapeSqlPath(outputPath)}' (FORMAT PARQUET, COMPRESSION ZSTD, ROW_GROUP_SIZE {rowGroupSize})";
            cmd.ExecuteNonQuery();
            return;
        }

        /* Larger batch — incremental pairwise merge. Caller has already sorted
           smallest-first across the whole group; within a batch we preserve that
           order so the accumulator grows steadily and small files are folded in
           early when memory is cheapest. */
        var currentPath = sourcePaths[0];
        var intermediateFiles = new List<string>();

        for (var i = 1; i < sourcePaths.Count; i++)
        {
            var stepOutput = i < sourcePaths.Count - 1
                ? outputPath + $".step{i}.tmp"
                : outputPath;

            using var con = new DuckDBConnection("DataSource=:memory:");
            con.Open();
            using (var pragmaCmd = con.CreateCommand())
            {
                pragmaCmd.CommandText = pragma;
                pragmaCmd.ExecuteNonQuery();
            }

            var selectClause = BuildSelectClause(table, new[] { currentPath, sourcePaths[i] });
            var pairList = $"'{EscapeSqlPath(currentPath)}', '{EscapeSqlPath(sourcePaths[i])}'";
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"COPY (SELECT {selectClause} FROM read_parquet([{pairList}], union_by_name=true)) " +
                              $"TO '{EscapeSqlPath(stepOutput)}' (FORMAT PARQUET, COMPRESSION ZSTD, ROW_GROUP_SIZE {rowGroupSize})";
            cmd.ExecuteNonQuery();

            if (intermediateFiles.Count > 0)
            {
                var prev = intermediateFiles[^1];
                try { File.Delete(prev); } catch { /* best effort */ }
            }
            intermediateFiles.Add(stepOutput);
            currentPath = stepOutput;
        }
    }

    private static string BuildPragma(string memoryLimit, int threads, string spillDirSql) =>
        $"SET memory_limit = '{memoryLimit}'; SET threads = {threads}; " +
        $"SET preserve_insertion_order = false; SET temp_directory = '{EscapeSqlPath(spillDirSql)}';";

    /* Build the SELECT clause for a compaction COPY, excluding only the
       CompactionExcludeColumns actually present in THIS set of files.
       Detection must be per-merge-set, not global: archive files predating a
       schema change lack the column, so a globally-computed "* EXCLUDE (col)"
       fails the binder on a pair where neither file has it. query_plan_text
       was added to query_store_stats in migration v13 (2026-02-23), so a
       reporter's pre-v13 archives don't carry it. (#933) */
    private static string BuildSelectClause(string table, IReadOnlyList<string> paths)
    {
        if (!CompactionExcludeColumns.TryGetValue(table, out var excludeCols))
        {
            return "*";
        }

        using var schemaCon = new DuckDBConnection("DataSource=:memory:");
        schemaCon.Open();
        var pathList = string.Join(", ", paths.Select(p => $"'{EscapeSqlPath(p)}'"));
        using var schemaCmd = schemaCon.CreateCommand();
        schemaCmd.CommandText = $"SELECT column_name FROM (DESCRIBE SELECT * FROM read_parquet([{pathList}], union_by_name=true))";
        using var reader = schemaCmd.ExecuteReader();
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) existingCols.Add(reader.GetString(0));

        var colsToExclude = excludeCols.Where(c => existingCols.Contains(c)).ToArray();
        return colsToExclude.Length > 0
            ? $"* EXCLUDE ({string.Join(", ", colsToExclude)})"
            : "*";
    }
}
