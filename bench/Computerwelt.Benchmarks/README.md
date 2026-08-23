# Computerwelt.Benchmarks

The port's performance instrument. It carries bashkit's own benchmark corpus —
`crates/bashkit-bench/src/cases.rs`, all 96 cases in their twelve categories, case for case
— so a number here can be put beside a number from upstream's tables, and adds
micro-benchmarks for the parts that corpus only measures in aggregate.

## Running it

```bash
# The fast loop: every case once through a stopwatch and an allocation counter.
dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --report

# One case, or a family of them.
dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --report --filter fibonacci

# The instrument you publish from. `--job short` trades precision for a run you can wait for.
dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --filter '*SessionBenchmarks*'
dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --filter '*ShellCategory*' --job short

# A loop for an external sampling profiler to watch.
dotnet run -c Release --project bench/Computerwelt.Benchmarks -- --soak --filter fibonacci --seconds 30
```

`--report` is the one to reach for while changing code: it prints µs/op and bytes/op per
case, sorted by cost, and it checks every case against the output upstream recorded — a
case that stopped producing the right answer is not a faster case, and the run exits
non-zero when one does. BenchmarkDotNet is the one to reach for before believing a number.

## What is measured

| Class | Rows | What it answers |
|---|---|---|
| `ShellCorpusBenchmarks` | 96 | upstream's corpus, one row per case |
| `ShellCategoryBenchmarks` | 12 | the same corpus by category, for a run that fits in a coffee break |
| `SessionBenchmarks` | 4 | what building a sandbox costs, apart from running a script in it |
| `ParsingBenchmarks` | 5 | lexing and parsing with no interpreter attached |
| `FileSystemBenchmarks` | 7 | the virtual filesystem, and the builtins that walk it |
| `PythonBenchmarks` | 5 | parse, compile and run, and each stage on its own |

Every class carries `[MemoryDiagnoser]`, because allocation is the number that moves first:
a sandbox is built per call in the corpus benchmarks, exactly as upstream's in-process
runner builds one per run, so what a session costs to stand up is part of every row.

## Reading the corpus rows

A case is upstream's script, unedited, and its expected output is bash's. The categories
are upstream's too:

| Category | Cases | Category | Cases |
|---|--:|---|--:|
| `startup` | 4 | `tools` | 21 |
| `variables` | 8 | `complex` | 7 |
| `arithmetic` | 6 | `large` | 9 |
| `control` | 9 | `subshell` | 6 |
| `strings` | 8 | `io` | 6 |
| `arrays` | 6 | `pipes` | 6 |

Nothing here is a ratchet: these are measurements, not acceptance criteria, and a machine's
numbers only compare with its own. The suites under `tests/` decide whether the port is
correct; this decides whether it is quick.
