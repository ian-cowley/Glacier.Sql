namespace Glacier.Sql.Benchmarks;

using System;
using System.Diagnostics;
using BenchmarkDotNet.Running;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--bdn", StringComparison.OrdinalIgnoreCase))
        {
            var bdnArgs = args.Length > 1 ? args[1..] : Array.Empty<string>();
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(bdnArgs);
            return;
        }

        Console.WriteLine("================================================================================");
        Console.WriteLine("          GLACIER.SQL MICROBENCHMARK & VERIFICATION SUITE                       ");
        Console.WriteLine("================================================================================");

        RunTokenizationBenchmarks();
        RunAstGenerationBenchmarks();
        RunQueryPlanCompilationBenchmarks();

        Console.WriteLine("\n================================================================================");
        Console.WriteLine("          ALL GLACIER.SQL MICROBENCHMARKS COMPLETED SUCCESSFULLY                ");
        Console.WriteLine("================================================================================");
    }

    private static void RunTokenizationBenchmarks()
    {
        Console.WriteLine("\n[1] SQL Tokenization Throughput & Allocation (SqlLexer.Tokenize):");
        var bdn = new SqlTokenizationBenchmarks();

        // Warmup
        for (int i = 0; i < 1_000; i++)
        {
            _ = bdn.TokenizeSimpleSelect();
            _ = bdn.TokenizeComplexJoin();
            _ = bdn.TokenizeInsertBatch();
            _ = bdn.TokenizeDdlCreateTable();
        }

        const int iterations = 100_000;

        BenchmarkMethod("Simple SELECT", iterations, () => bdn.TokenizeSimpleSelect());
        BenchmarkMethod("Complex JOIN (GROUP BY, AGG)", iterations, () => bdn.TokenizeComplexJoin());
        BenchmarkMethod("INSERT Batch", iterations, () => bdn.TokenizeInsertBatch());
        BenchmarkMethod("CREATE TABLE DDL (Constraints)", iterations, () => bdn.TokenizeDdlCreateTable());
    }

    private static void RunAstGenerationBenchmarks()
    {
        Console.WriteLine("\n[2] SQL AST Generation Throughput & Allocation (TSqlParser.Parse):");
        var bdn = new SqlAstGenerationBenchmarks();
        bdn.Setup();

        // Warmup
        for (int i = 0; i < 1_000; i++)
        {
            _ = bdn.ParseSimpleSelectAst();
            _ = bdn.ParseComplexJoinAst();
            _ = bdn.ParseInsertBatchAst();
            _ = bdn.ParseDdlCreateTableAst();
        }

        const int iterations = 100_000;

        BenchmarkMethod("Simple SELECT AST", iterations, () => bdn.ParseSimpleSelectAst());
        BenchmarkMethod("Complex JOIN AST", iterations, () => bdn.ParseComplexJoinAst());
        BenchmarkMethod("INSERT Statement AST", iterations, () => bdn.ParseInsertBatchAst());
        BenchmarkMethod("CREATE TABLE AST", iterations, () => bdn.ParseDdlCreateTableAst());
    }

    private static void RunQueryPlanCompilationBenchmarks()
    {
        Console.WriteLine("\n[3] Query Plan Compilation Throughput & Allocation (QueryPlanner.PlanQuery):");
        var bdn = new QueryPlanCompilationBenchmarks();
        bdn.Setup();

        try
        {
            // Warmup
            for (int i = 0; i < 500; i++)
            {
                _ = bdn.PlanSimpleSelect();
                _ = bdn.PlanComplexJoin();
                _ = bdn.PlanAggregateGroupBy();
                _ = bdn.CompileScalarExpression();
                _ = bdn.CompilePredicateExpression();
            }

            const int iterations = 50_000;

            BenchmarkMethod("Plan Simple SELECT", iterations, () => bdn.PlanSimpleSelect());
            BenchmarkMethod("Plan Complex JOIN", iterations, () => bdn.PlanComplexJoin());
            BenchmarkMethod("Plan Aggregate + GROUP BY", iterations, () => bdn.PlanAggregateGroupBy());
            BenchmarkMethod("Compile Scalar Expression", iterations, () => bdn.CompileScalarExpression());
            BenchmarkMethod("Compile Predicate Expression", iterations, () => bdn.CompilePredicateExpression());
        }
        finally
        {
            bdn.Cleanup();
        }
    }

    private static void BenchmarkMethod(string name, int iterations, Action action)
    {
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            action();
        }

        sw.Stop();
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        double elapsedSeconds = sw.Elapsed.TotalSeconds;
        double opsPerSec = iterations / elapsedSeconds;
        double latencyUs = (sw.Elapsed.TotalMilliseconds / iterations) * 1000.0;
        long totalAllocBytes = allocAfter - allocBefore;
        double bytesPerOp = totalAllocBytes / (double)iterations;

        Console.WriteLine($"    {name,-35} | {opsPerSec,11:N0} ops/s | Latency: {latencyUs,6:F2} µs | Alloc: {bytesPerOp,6:F1} B/op");
    }
}
