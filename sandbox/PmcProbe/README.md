# PmcProbe

Reads Apple Silicon PMU counters (cycles, instructions, branches, branch
mispredicts) around the same lookup loops as `DryDB.Benchmark`'s
`ReadBenchmark`, for three node layouts (sorted digests + SIMD window,
Eytzinger digests, no digests) × three key patterns (fixed key, a repeating
1000-key sequence the branch predictor can memorize, and a never-repeating
sequence).

Used to verify that the timing differences between predictable and
unpredictable key streams are caused by branch mispredictions, not caches.

## Requirements / caveats

- Apple Silicon Mac only. Measured on M5; the event names come from the
  OS-provided kpep database (`/usr/share/kpep/*.plist`) and exist at least
  since M1.
- Uses the **private** `kperf` / `kperfdata` frameworks (the same machinery
  Instruments uses). Private API: may break on any macOS update. Dev-machine
  diagnostics only — never ship this.
- Needs **root** (`kpc_force_all_ctrs_set`). While running it takes exclusive
  ownership of the PMU, so it cannot run at the same time as Instruments.
  Ownership is released when the process exits.
- The `_NONSPEC` events count retired (non-speculative) instructions only, so
  branch totals are stable across key patterns and only the mispredict counts
  vary.

## Run

```bash
dotnet build -c Release sandbox/PmcProbe/PmcProbe.csproj
sudo dotnet run --no-build -c Release --project sandbox/PmcProbe
```

(`--no-build` matters: without it, root rebuilds into root-owned artifacts.)

## Example output (Apple M5, .NET 10)

| layout | keys | ns/op | cyc/op | inst/op | branch/op | cond/op | miss/op | condMiss/op | condMiss% |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| sorted+simd | fixed | 13.0 | 56.4 | 455.2 | 84.0 | 74.0 | 0.00 | 0.00 | 0.0% |
| sorted+simd | repeat1000 | 24.7 | 106.8 | 569.1 | 81.3 | 68.7 | 0.03 | 0.03 | 0.0% |
| sorted+simd | norepeat | 52.1 | 227.8 | 563.1 | 81.1 | 68.9 | 3.09 | 3.09 | 4.5% |
| eytzinger | fixed | 16.5 | 70.9 | 520.1 | 89.0 | 73.0 | 0.00 | 0.00 | 0.0% |
| eytzinger | repeat1000 | 32.7 | 143.5 | 527.2 | 89.0 | 73.0 | 0.01 | 0.01 | 0.0% |
| eytzinger | norepeat | 33.1 | 144.9 | 527.2 | 89.0 | 73.0 | 0.01 | 0.01 | 0.0% |
| no-digest | fixed | 13.9 | 59.9 | 487.1 | 74.0 | 60.0 | 0.00 | 0.00 | 0.0% |
| no-digest | repeat1000 | 26.3 | 114.3 | 549.1 | 84.7 | 68.3 | 0.02 | 0.02 | 0.0% |
| no-digest | norepeat | 87.2 | 378.5 | 548.5 | 84.6 | 68.2 | 6.07 | 6.07 | 8.9% |

Reading it: branch counts are constant across key patterns (same code runs),
mispredicts are ~0 whenever the predictor can memorize the key stream, jump to
3–6 per lookup on truly random keys, and stay at ~0 for the branch-free
Eytzinger descent. The cycle delta divided by the mispredict delta puts the
effective cost of one mispredict at ~40 cycles on M5.

The kperf setup sequence is a C# port of the well-known
[kpc_demo.c](https://gist.github.com/ibireme/173517c208c7dc333ba962c1f0d67d12)
by ibireme.
