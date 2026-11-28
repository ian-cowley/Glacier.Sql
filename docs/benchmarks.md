# Glacier.Sql Empirical Benchmarks

All measurements on this page were executed directly in `Release` mode (`-c Release`) using `benchmarks/Glacier.Sql.Benchmarks`.

---

## Reference Workstation

- **CPU:** AMD Ryzen AI 9 HX 370 (12C/24T Zen 5, AVX-512)
- **Discrete GPU:** NVIDIA GeForce RTX 4060 Laptop GPU (8 GB VRAM, `sm_89`)
- **Integrated GPU:** AMD Radeon 890M Graphics (RDNA 3.5)
- **RAM:** 31 GB
- **OS:** Windows 11 (`10.0.26300.0`)
- **Runtime:** `.NET 10.0.15` (`.NET SDK 11.0.100`, Release x64)

---

## How to Reproduce

Run the direct Release microbenchmark suite (100,000 iterations for lexer/parser, 50,000 iterations for query planning):

```bash
dotnet run --project benchmarks/Glacier.Sql.Benchmarks/Glacier.Sql.Benchmarks.csproj -c Release
```

Or invoke BenchmarkDotNet:

```bash
dotnet run --project benchmarks/Glacier.Sql.Benchmarks/Glacier.Sql.Benchmarks.csproj -c Release -- --bdn --filter *
```

---

## 1. SQL Tokenization (`SqlLexer.Tokenize`, 100,000 iterations)

| Workload | Throughput | Mean Latency | Allocated / Op |
| :--- | ---: | ---: | ---: |
| Simple `SELECT` | 770,402 ops/s | 1.30 us | 1,184.0 B/op |
| Complex `JOIN` (`GROUP BY`, `AGG`) | 358,339 ops/s | 2.79 us | 5,160.0 B/op |
| `INSERT` Batch | 2,309,367 ops/s | 0.43 us | 1,808.0 B/op |
| `CREATE TABLE` DDL (Constraints) | 2,099,552 ops/s | 0.48 us | 2,072.0 B/op |

---

## 2. AST Generation (`TSqlParser.Parse`, 100,000 iterations)

| Workload | Throughput | Mean Latency | Allocated / Op |
| :--- | ---: | ---: | ---: |
| Simple `SELECT` AST | 817,643 ops/s | 1.22 us | 640.0 B/op |
| Complex `JOIN` AST | 615,680 ops/s | 1.62 us | 1,480.0 B/op |
| `INSERT` Statement AST | 2,432,948 ops/s | 0.41 us | 512.0 B/op |
| `CREATE TABLE` AST | 2,173,615 ops/s | 0.46 us | 664.0 B/op |

---

## 3. Query Plan & Expression Compilation (`QueryPlanner.PlanQuery`, 50,000 iterations)

| Workload | Throughput | Mean Latency | Allocated / Op |
| :--- | ---: | ---: | ---: |
| Plan Simple `SELECT` | 96,739 ops/s | 10.34 us | 6,848.4 B/op |
| Plan Complex `JOIN` | 64,757 ops/s | 15.44 us | 12,712.8 B/op |
| Plan Aggregate + `GROUP BY` | 155,558 ops/s | 6.43 us | 11,928.0 B/op |
| Compile Scalar Expression | 929,596 ops/s | 1.08 us | 1,992.1 B/op |
| Compile Predicate Expression | 706,584 ops/s | 1.42 us | 2,608.2 B/op |
