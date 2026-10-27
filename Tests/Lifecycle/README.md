# Class acquisition and aggregator benchmarks

This harness runs inside the Steam-independent laptop games documented in
`/Users/darren/codex_workspace/rimworld-standalone/README.txt`. It requires the
same SDK, Harmony, HAR, Miho, and five DLC as `Tests/Runtime`.

Modes:

- `upstream`: installed Miho optimizer, standalone mod disabled.
- `original`: a supplied previous standalone DLL; the probe removes the upstream
  aggregator so only that original implementation runs.
- `candidate`: the repository DLL, including its own upstream-aggregator
  replacement. The probe does not remove the upstream component in this mode.

Other upstream apparel/form-change optimizations remain enabled in every mode.
No dependency DLLs are modified or redistributed.

Supply a disposable baseline save from the same game version. The runner copies
it to a fresh profile, adjusts its mod metadata, and removes the previous probe
GameComponent. Reports are never overwritten; use a new output directory or tag
when changing the candidate DLL.

```sh
# The previous branch build before class safeguards (Release, same toolchain):
git show 323226a:Assemblies/MihoHediffFix.dll > /absolute/test/original.dll

python3 Tests/Lifecycle/run.py 1.6 candidate \
  --baseline /absolute/1.6-baseline.rws --output /absolute/test/output
python3 Tests/Lifecycle/run.py 1.6 original \
  --reference-dll /absolute/test/original.dll \
  --baseline /absolute/1.6-baseline.rws --output /absolute/test/output
python3 Tests/Lifecycle/run.py 1.6 upstream \
  --baseline /absolute/1.6-baseline.rws --output /absolute/test/output
```

Repeat with 1.7 and its baseline. For an independent benchmark-only process,
add `--repeat --tag unique-repeat-tag`. Fresh-process persistence is covered by
`Tests/Runtime/run.py`, rather than this lifecycle harness.

Each full process tests 24 cases: six class families, three Huntress seeds,
repeated pills, concurrent different pills, a pill after natural acquisition,
a legacy starter at severity 1.5, no-pill controls, and a one-tick confirmation.
The real `Thing.Ingested`, `HealthTickInterval`, and `CompTick` methods run on a
10-year-old Miho child. Timers, severity rates, and hediff givers are unmodified.
The fixture stays at 21 C; needs, AI, aging, and the rest of colony simulation
are paused to exclude unrelated deaths. Most cases advance health in 60-tick
intervals; the primary Huntress case also runs one tick at a time.

Benchmarks call the actual aggregator only, excluding the original ten checking
comps in every mode. Each of idle, established-class, and unlocking states uses
nine batches of 200,000 processing-interval calls after 100,000 warmup calls
and a time-based warmup (`--warmup-ms`, default 2,000 ms).
Timers are held high only during benchmarks. Lists have 64 baseline hediffs,
plus the class/starter where applicable. An independent repeat process reveals
runtime/JIT variability. Use `--warmup-ms 0` only to reproduce the earlier short-
warmup measurements; initial .NET 10 tiering can skew the first idle state. These are component measurements, not whole-game TPS.
Mono allocation counters returning zero are not allocation evidence.

Optional Mohar Framework is not enabled; its class-nullifier integration could
change multi-class results. Tests establish class acquisition and the observed
later-rank events, not every possible rank or full-colony outcome.

The output folder contains JSONL events, full timelines, game/launcher logs,
phase summaries, manifests, and disposable profiles. Source changes and the
exact loaded mod DLL hash should be reviewed with the results.
