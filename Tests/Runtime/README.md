# In-game compatibility tests

These tests use the standalone laptop installations described in
`/Users/darren/codex_workspace/rimworld-standalone/README.txt`. They require SDK
10.0.402, installed Workshop copies of Harmony, HAR, Miho, all five DLC, and the
Steam-independent 1.6 and 1.7 game copies. The local paths in `run.py` match that
workspace. The runner uses disposable profiles and restores temporary mod links.
It never launches the Steam installation.

Run from the repository root, choosing a fresh absolute output directory:

```sh
python3 Tests/Runtime/run.py 1.6 --output /absolute/test/output
python3 Tests/Runtime/run.py 1.6 --output /absolute/test/output --resume
python3 Tests/Runtime/run.py 1.7 --output /absolute/test/output
python3 Tests/Runtime/run.py 1.7 --output /absolute/test/output --resume
```

Only the test probe is compiled per version. Both games load the exact same
repository DLL; SHA256 is asserted in each game process. Reports are never
overwritten; use `--tag` for another run in the same output directory.

Coverage: root load-folder selection, a single loaded DLL, Harmony prefix,
original comps replaced by one aggregator on both target races, all ten Miho
rules cached on a real spawned Miho pawn, missing-source reset, delayed/once-only
severity application, source removal, public CompTick execution, suppression of
a surviving legacy comp, replacement of the bundled MihoOptimization aggregator,
and persisted rule timers after a fresh-process reload.

JSONL assertions, summary JSON, manifests, saves, and full logs stay outside the
repository and runtime package. Startup mod-discovery warnings can include other
inactive Workshop mods; the probe records game errors from its constructor onward.

The guarded build adds checks for two simultaneous class pills, avoiding starter
reinforcement, repairing legacy severity, rank-V recognition, cleanup of all six
families and duplicate starter defs, tea/pipe/restraint with an established class,
tea during unlocking, and unchanged additive Abomi severity. The temporary Abomi
is discarded from the map and WorldPawns before saving because its upstream
weapon graph has a separate serialization issue. These are rule checks, not an
Abomi persistence claim. The full suite has 28 prepare and 11 reload assertions
per game version. `Tests/Lifecycle` separately exercises natural class acquisition
and performance without forced-due timers.
