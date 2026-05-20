using System.Diagnostics;
using DuckDB.NET.Data;
using PerformanceMonitorLite.Services;

/*
 * CompactionRepro — standalone reproducer for issue #933.
 *
 * Runs the *real* production compaction merge code against a set of parquet
 * files and reports memory behavior + pass/fail.
 *
 * IMPORTANT: this tool does NOT reimplement the merge. It links the actual
 * production source (Lite/Services/ParquetCompaction.cs, via a <Compile Link>
 * in the csproj) and calls ParquetCompaction.BuildSizeBudgetedBatches +
 * MergeBatchToFile directly — the same calls ArchiveService.CompactParquetFiles
 * makes. Earlier versions of this repro kept hand-copied merge loops that
 * silently drifted from production; fixes "passed the repro" and still OOM'd on
 * real installs. If you change the merge algorithm, change it in
 * ParquetCompaction.cs and this tool picks it up automatically.
 *
 * Input sources (pick one):
 *   --source-file <path>   Split an existing monthly parquet into N per-cycle
 *                          chunks, then compact. Closest to a real backlog.
 *   --merge-files <a,b,..> Compact the given files directly (no split). Use this
 *                          to run against a reporter's actual archive files.
 *   --synthetic            Generate a query_snapshots-shaped source, then split.
 *
 * Tuning knobs (defaults = current production values from ParquetCompaction):
 *   --memory-limit <str>   DuckDB memory_limit per merge connection. Default: 4GB
 *   --threads <int>        DuckDB threads per merge connection. Default: 2
 *   --row-group-size <int> Output ROW_GROUP_SIZE. Default: 8192
 *   --max-batch-mb <int>   Per-batch on-disk input budget (MB). Default: 200
 *   --table <name>         Table name (drives exclude-column logic). Default: query_snapshots
 *
 * Other options:
 *   --num-files <int>      Chunks to split --source-file/--synthetic into. Default: 15
 *   --synthetic-rows <int> Synthetic row count. Default: 30000
 *   --synthetic-plan-kb <n> Synthetic plan XML KB per row. Default: 100
 *   --cycles <int>         Re-run the full compaction N times (memory-release test). Default: 1
 *   --keep                 Don't delete the temp dir after the run.
 *
 * Examples:
 *   # Run against a reporter's actual failing files
 *   dotnet run -c Release -- --merge-files "C:/archive/20260501_query_snapshots.parquet,C:/archive/202605_query_snapshots.parquet"
 *
 *   # Reproduce the production path on a real monthly file split into 88 chunks
 *   dotnet run -c Release -- --source-file "%LOCALAPPDATA%/PerformanceMonitorLite/archive/202605_query_snapshots.parquet" --num-files 88
 *
 *   # Sweep the batch budget down to find a value query_snapshots survives
 *   dotnet run -c Release -- --source-file ".../202605_query_snapshots.parquet" --num-files 88 --max-batch-mb 40
 */

var sourceFile = GetArg(args, "--source-file", "");
var mergeFilesArg = GetArg(args, "--merge-files", "");
var synthetic = args.Contains("--synthetic");
var syntheticRows = int.Parse(GetArg(args, "--synthetic-rows", "30000"));
var syntheticPlanKb = int.Parse(GetArg(args, "--synthetic-plan-kb", "100"));
if (string.IsNullOrEmpty(sourceFile) && string.IsNullOrEmpty(mergeFilesArg) && !synthetic)
{
    Console.Error.WriteLine("error: --source-file <path> OR --merge-files <a.parquet,...> OR --synthetic required");
    Console.Error.WriteLine("  --source-file:  split the given monthly parquet into chunks, then compact (full repro)");
    Console.Error.WriteLine("  --merge-files:  compact the given comma-separated files directly (skip split)");
    Console.Error.WriteLine("  --synthetic:    generate a query_snapshots-shaped source file (see --synthetic-rows, --synthetic-plan-kb)");
    return 2;
}
if (!string.IsNullOrEmpty(sourceFile) && !File.Exists(sourceFile))
{
    Console.Error.WriteLine($"error: source file not found: {sourceFile}");
    return 2;
}

var table = GetArg(args, "--table", "query_snapshots");
var memoryLimit = GetArg(args, "--memory-limit", ParquetCompaction.DefaultMemoryLimit);
var threads = int.Parse(GetArg(args, "--threads", ParquetCompaction.DefaultThreads.ToString()));
var rowGroupSize = int.Parse(GetArg(args, "--row-group-size", ParquetCompaction.DefaultRowGroupSize.ToString()));
var maxBatchMb = int.Parse(GetArg(args, "--max-batch-mb", (ParquetCompaction.MaxBatchInputBytes / (1024 * 1024)).ToString()));
var maxBatchBytes = maxBatchMb * 1024L * 1024L;
var numFiles = int.Parse(GetArg(args, "--num-files", "15"));
var cycles = int.Parse(GetArg(args, "--cycles", "1"));
var keep = args.Contains("--keep");

var tempDir = Path.Combine(Path.GetTempPath(), $"CompactionRepro_{Guid.NewGuid():N}");
Directory.CreateDirectory(tempDir);

var mergeFiles = string.IsNullOrEmpty(mergeFilesArg)
    ? new List<string>()
    : mergeFilesArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
foreach (var mf in mergeFiles)
{
    if (!File.Exists(mf))
    {
        Console.Error.WriteLine($"error: --merge-files entry not found: {mf}");
        return 2;
    }
}

if (synthetic)
{
    sourceFile = Path.Combine(tempDir, "synthetic_query_snapshots.parquet").Replace("\\", "/");
    Console.WriteLine($"Mode:     synthetic+split+merge");
    Console.WriteLine($"Synthetic: {syntheticRows} rows, ~{syntheticPlanKb} KB plan XML per row");
    Console.WriteLine($"Source:   {sourceFile} (will be generated)");
}
else if (mergeFiles.Count > 0)
{
    Console.WriteLine($"Mode:     merge-files (no split — real files)");
    Console.WriteLine($"Inputs:");
    foreach (var mf in mergeFiles)
        Console.WriteLine($"  {mf} ({new FileInfo(mf).Length / 1024.0 / 1024.0:F1} MB)");
}
else
{
    Console.WriteLine($"Mode:     split+merge (full repro)");
    Console.WriteLine($"Source:   {sourceFile} ({new FileInfo(sourceFile).Length / 1024.0 / 1024.0:F1} MB)");
}
Console.WriteLine($"Temp dir: {tempDir}");
using (var versionCon = new DuckDBConnection("DataSource=:memory:"))
{
    versionCon.Open();
    using var versionCmd = versionCon.CreateCommand();
    versionCmd.CommandText = "SELECT version()";
    Console.WriteLine($"Engine:   DuckDB {versionCmd.ExecuteScalar()}");
}
Console.WriteLine($"Code:     ParquetCompaction (linked from Lite/Services — real production merge)");
Console.WriteLine($"Table:    {table}");
Console.WriteLine($"Settings: memory_limit={memoryLimit}, threads={threads}, ROW_GROUP_SIZE={rowGroupSize}, max-batch={maxBatchMb} MB");
if (mergeFiles.Count == 0)
    Console.WriteLine($"Splitting source into {numFiles} chunks");
Console.WriteLine();

try
{
    if (synthetic)
    {
        Console.WriteLine($"[0/3] Generating synthetic source ({syntheticRows} rows, ~{syntheticPlanKb} KB plan/row)...");
        var sw = Stopwatch.StartNew();
        GenerateSyntheticSource(sourceFile, syntheticRows, syntheticPlanKb);
        sw.Stop();
        var size = new FileInfo(sourceFile).Length / 1024.0 / 1024.0;
        Console.WriteLine($"      Generated {size:F1} MB in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine();
    }

    List<string> sourcePaths;
    if (mergeFiles.Count > 0)
    {
        Console.WriteLine($"[1/3] Skipping split — using {mergeFiles.Count} provided files");
        sourcePaths = mergeFiles.Select(p => p.Replace("\\", "/")).ToList();
    }
    else
    {
        Console.WriteLine($"[1/3] Splitting source file into {numFiles} chunks...");
        var sw = Stopwatch.StartNew();
        sourcePaths = SplitSourceFile(sourceFile, tempDir, numFiles);
        sw.Stop();
        var totalSourceBytes = sourcePaths.Sum(p => new FileInfo(p).Length);
        Console.WriteLine($"      Wrote {sourcePaths.Count} files, {totalSourceBytes / 1024.0 / 1024.0:F1} MB total in {sw.ElapsedMilliseconds} ms");
    }
    Console.WriteLine();

    /* Mirror ArchiveService.CompactParquetFiles: sort smallest-first, then bucket
       into size-budgeted batches. This is the exact production sequencing. */
    var sorted = sourcePaths
        .OrderBy(p => new FileInfo(p.Replace("/", "\\")).Length)
        .ToList();
    var batches = ParquetCompaction.BuildSizeBudgetedBatches(sorted, maxBatchBytes);
    Console.WriteLine($"[2/3] BuildSizeBudgetedBatches: {sorted.Count} files -> {batches.Count} batch(es) at {maxBatchMb} MB budget");
    for (var i = 0; i < batches.Count; i++)
    {
        var batchBytes = batches[i].Sum(p => new FileInfo(p.Replace("/", "\\")).Length);
        Console.WriteLine($"      batch {i + 1}: {batches[i].Count} files, {batchBytes / 1024.0 / 1024.0:F1} MB on disk");
    }
    Console.WriteLine();

    Console.WriteLine($"[3/3] Running MergeBatchToFile per batch (real production code), {cycles} cycle(s)...");
    var spillDir = Path.Combine(tempDir, "duckdb_tmp").Replace("\\", "/");
    Directory.CreateDirectory(spillDir);

    var process = Process.GetCurrentProcess();
    process.Refresh();
    var startWorkingSet = process.WorkingSet64;
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    process.Refresh();
    startWorkingSet = process.WorkingSet64;
    Console.WriteLine($"      baseline working set (after GC): {startWorkingSet / 1024.0 / 1024.0:F0} MB");

    /* Background sampler — polls working set during the (opaque) merge calls so
       the reported peak reflects mid-merge pressure, not just between-batch gaps. */
    long peakWorkingSet = startWorkingSet;
    var samplerStop = false;
    var sampler = new Thread(() =>
    {
        var p = Process.GetCurrentProcess();
        while (!Volatile.Read(ref samplerStop))
        {
            p.Refresh();
            var ws = p.WorkingSet64;
            if (ws > peakWorkingSet) peakWorkingSet = ws;
            Thread.Sleep(100);
        }
    }) { IsBackground = true };
    sampler.Start();

    var compactionSw = Stopwatch.StartNew();
    var perCycleWorkingSet = new List<(long peak, long postGc)>();
    var success = false;
    string? failureMessage = null;
    var outputPaths = new List<string>();
    long compactedFileBytes = 0;

    for (var cycle = 1; cycle <= cycles && (cycle == 1 || success); cycle++)
    {
        if (cycles > 1) Console.WriteLine($"      --- cycle {cycle}/{cycles} ---");
        var cycleStartPeak = peakWorkingSet;
        outputPaths.Clear();
        success = false;

        try
        {
            for (var i = 0; i < batches.Count; i++)
            {
                var outName = batches.Count == 1
                    ? $"{table}.parquet"
                    : $"{table}_pt{i + 1:D3}.parquet";
                var outPath = Path.Combine(tempDir, $"c{cycle}_{outName}").Replace("\\", "/");
                if (File.Exists(outPath)) File.Delete(outPath);

                var batchSw = Stopwatch.StartNew();
                ParquetCompaction.MergeBatchToFile(
                    table, batches[i], outPath, spillDir,
                    memoryLimit, threads, rowGroupSize);
                batchSw.Stop();

                process.Refresh();
                if (process.WorkingSet64 > peakWorkingSet) peakWorkingSet = process.WorkingSet64;
                var outSize = new FileInfo(outPath).Length / 1024.0 / 1024.0;
                Console.WriteLine($"      batch {i + 1}/{batches.Count}: {batches[i].Count} files -> {outSize:F1} MB " +
                                  $"in {batchSw.Elapsed.TotalSeconds:F1}s | peak WS {peakWorkingSet / 1024.0 / 1024.0:F0} MB");
                outputPaths.Add(outPath);
            }

            compactedFileBytes = outputPaths.Sum(p => new FileInfo(p).Length);
            success = true;
        }
        catch (Exception ex)
        {
            failureMessage = ex.Message;
            break;
        }

        var cyclePeak = peakWorkingSet;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep(500);
        process.Refresh();
        var postGc = process.WorkingSet64;
        perCycleWorkingSet.Add((cyclePeak - cycleStartPeak, postGc));
        if (cycles > 1)
            Console.WriteLine($"      cycle {cycle} peak +{(cyclePeak - cycleStartPeak) / 1024.0 / 1024.0:F0} MB | post-GC WS {postGc / 1024.0 / 1024.0:F0} MB");
    }
    compactionSw.Stop();

    samplerStop = true;
    sampler.Join(1000);
    process.Refresh();
    if (process.WorkingSet64 > peakWorkingSet) peakWorkingSet = process.WorkingSet64;

    Console.WriteLine();
    Console.WriteLine("Result:");
    Console.WriteLine($"      Status:           {(success ? "SUCCESS" : "FAILURE")}");
    Console.WriteLine($"      Wall time:        {compactionSw.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"      Baseline WS:      {startWorkingSet / 1024.0 / 1024.0:F0} MB");
    Console.WriteLine($"      Peak WS:          {peakWorkingSet / 1024.0 / 1024.0:F0} MB (+{(peakWorkingSet - startWorkingSet) / 1024.0 / 1024.0:F0} MB)");
    if (cycles > 1 && perCycleWorkingSet.Count > 0)
    {
        Console.WriteLine($"      Post-GC WS by cycle:");
        for (var i = 0; i < perCycleWorkingSet.Count; i++)
        {
            var (peak, postGc) = perCycleWorkingSet[i];
            Console.WriteLine($"        cycle {i + 1}: peak +{peak / 1024.0 / 1024.0:F0} MB, post-GC {postGc / 1024.0 / 1024.0:F0} MB");
        }
        var drift = (perCycleWorkingSet[^1].postGc - perCycleWorkingSet[0].postGc) / 1024.0 / 1024.0;
        Console.WriteLine($"      WS drift (last - first post-GC): {drift:+0;-0;0} MB");
    }
    if (success)
    {
        Console.WriteLine($"      Output:           {outputPaths.Count} part file(s), {compactedFileBytes / 1024.0 / 1024.0:F1} MB total");

        /* Row-count round-trip: total output rows must equal total source rows. */
        var srcSqlList = string.Join(", ", sourcePaths.Select(p => $"'{p.Replace("'", "''").Replace("\\", "/")}'"));
        var outSqlList = string.Join(", ", outputPaths.Select(p => $"'{p.Replace("'", "''").Replace("\\", "/")}'"));
        using var verifyCon = new DuckDBConnection("DataSource=:memory:");
        verifyCon.Open();
        using var verifyCmd = verifyCon.CreateCommand();
        verifyCmd.CommandText =
            $"SELECT (SELECT COUNT(*) FROM read_parquet([{outSqlList}], union_by_name=true)) AS out_rows, " +
            $"       (SELECT COUNT(*) FROM read_parquet([{srcSqlList}], union_by_name=true)) AS src_rows";
        using var verifyReader = verifyCmd.ExecuteReader();
        verifyReader.Read();
        var actualRows = verifyReader.GetInt64(0);
        var expectedRows = verifyReader.GetInt64(1);
        Console.WriteLine($"      Row count:        {actualRows} (expected {expectedRows}) {(actualRows == expectedRows ? "OK" : "MISMATCH")}");
    }
    else
    {
        Console.WriteLine($"      Failure:          {failureMessage}");
    }

    var spillBytes = Directory.Exists(spillDir)
        ? Directory.GetFiles(spillDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
        : 0;
    Console.WriteLine($"      Spill on disk:    {spillBytes / 1024.0 / 1024.0:F1} MB ({(spillBytes > 0 ? "spilled" : "did not spill")})");

    return success ? 0 : 1;
}
finally
{
    if (!keep)
    {
        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine($"Temp dir retained: {tempDir}");
    }
}

static List<string> SplitSourceFile(string sourceFile, string outDir, int numChunks)
{
    /* Split a real monthly parquet into N chunks using row-number bucketing.
       Each chunk is written as ZSTD parquet matching the production per-cycle
       archive format (ArchiveService writes per-cycle files with FORMAT PARQUET,
       COMPRESSION ZSTD and the DuckDB default row group size). Empty chunks are
       skipped. This connection runs with DuckDB defaults (no memory_limit) — the
       merge connections set their own via ParquetCompaction. */
    var sourceSql = sourceFile.Replace("'", "''").Replace("\\", "/");

    using var con = new DuckDBConnection("DataSource=:memory:");
    con.Open();

    long totalRows;
    using (var countCmd = con.CreateCommand())
    {
        countCmd.CommandText = $"SELECT COUNT(*) FROM read_parquet('{sourceSql}')";
        totalRows = Convert.ToInt64(countCmd.ExecuteScalar());
    }
    Console.WriteLine($"      Source has {totalRows} rows; splitting into {numChunks} chunks");

    var paths = new List<string>();
    for (var i = 0; i < numChunks; i++)
    {
        var path = Path.Combine(outDir, $"src_{i:D3}.parquet").Replace("\\", "/");
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            $"COPY (SELECT * FROM read_parquet('{sourceSql}') " +
            $"  WHERE (collection_id % {numChunks}) = {i}) " +
            $"TO '{path.Replace("'", "''")}' (FORMAT PARQUET, COMPRESSION ZSTD)";
        cmd.ExecuteNonQuery();
        if (new FileInfo(path).Length > 0) paths.Add(path);
    }
    return paths;
}

static string GetArg(string[] args, string key, string defaultValue)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == key) return args[i + 1];
    return defaultValue;
}

static void GenerateSyntheticSource(string outputPath, int rows, int planKb)
{
    /* Generate query_snapshots-shaped parquet with high-entropy plan XML so
       ZSTD can't collapse content to a single dictionary entry. We aggregate
       a list of md5() hashes per row — each hash uses (collection_id, op_index)
       as a unique seed, defeating both per-row and cross-row compression.

       NOTE: this generates UNIFORM-width plan XML. Real plan XML is heavy-tailed
       (mostly small, a few multi-MB plans). Uniform synthetic data does not
       reproduce the row-group memory spikes a fat tail causes — prefer
       --merge-files against a reporter's real archive when one is available. */
    var sourceSql = outputPath.Replace("'", "''").Replace("\\", "/");
    const int opTagBytes = 46;
    var opsPerPlan = Math.Max(4, (planKb * 1024) / opTagBytes);

    using var con = new DuckDBConnection("DataSource=:memory:");
    con.Open();

    using var cmd = con.CreateCommand();
    cmd.CommandText = $@"
COPY (
    SELECT
        i AS collection_id,
        TIMESTAMP '2026-04-01 00:00:00' + INTERVAL (i) MINUTE AS collection_time,
        ((i % 4) + 1)::INTEGER AS server_id,
        ('Server' || ((i % 4) + 1)::VARCHAR) AS server_name,
        ((i % 200) + 50)::INTEGER AS session_id,
        ('db_' || ((i % 10) + 1)::VARCHAR) AS database_name,
        '00:00:00' AS elapsed_time_formatted,
        ('SELECT * FROM t_' || (i % 1000)::VARCHAR || ' WHERE c = ''' || md5(i::VARCHAR) || '''') AS query_text,
        ('<plan id=""' || i::VARCHAR || '"">' ||
         list_aggregate(
             list_transform(generate_series(1, {opsPerPlan}),
                            j -> '<op id=""' || md5((i::VARCHAR || ':' || j::VARCHAR)) || '""/>'),
             'string_agg', '') ||
         '</plan>') AS query_plan,
        ('<liveplan id=""' || i::VARCHAR || '"">' ||
         list_aggregate(
             list_transform(generate_series(1, {opsPerPlan}),
                            j -> '<op id=""' || md5(('L:' || i::VARCHAR || ':' || j::VARCHAR)) || '""/>'),
             'string_agg', '') ||
         '</liveplan>') AS live_query_plan,
        CASE (i % 5) WHEN 0 THEN 'running' WHEN 1 THEN 'suspended' WHEN 2 THEN 'sleeping' WHEN 3 THEN 'background' ELSE 'rollback' END AS status,
        CASE WHEN i % 7 = 0 THEN ((i % 200) + 1)::INTEGER ELSE NULL END AS blocking_session_id,
        CASE (i % 4) WHEN 0 THEN 'PAGEIOLATCH_SH' WHEN 1 THEN 'CXPACKET' WHEN 2 THEN 'LCK_M_S' ELSE NULL END AS wait_type,
        ((i * 13) % 5000)::BIGINT AS wait_time_ms,
        ('PAGE: 1:' || (i % 1000000)::VARCHAR) AS wait_resource,
        ((i * 17) % 60000)::BIGINT AS cpu_time_ms,
        ((i * 23) % 120000)::BIGINT AS total_elapsed_time_ms,
        ((i * 31) % 1000000)::BIGINT AS reads,
        ((i * 41) % 10000)::BIGINT AS writes,
        ((i * 43) % 5000000)::BIGINT AS logical_reads,
        ((i % 1000) / 100.0)::DECIMAL(18,2) AS granted_query_memory_gb,
        'READ_COMMITTED' AS transaction_isolation_level,
        ((i % 8) + 1)::INTEGER AS dop,
        ((i % 16) + 1)::INTEGER AS parallel_worker_count,
        ('login_' || (i % 50)::VARCHAR) AS login_name,
        ('HOST-' || (i % 20)::VARCHAR) AS host_name,
        ('Program_' || (i % 30)::VARCHAR) AS program_name,
        (i % 5)::INTEGER AS open_transaction_count,
        ((i % 100))::DECIMAL(5,2) AS percent_complete
    FROM generate_series(1, {rows}) t(i)
) TO '{sourceSql}' (FORMAT PARQUET, COMPRESSION ZSTD, ROW_GROUP_SIZE 122880)";
    cmd.ExecuteNonQuery();
}
