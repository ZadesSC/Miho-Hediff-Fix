# Shared 1.6 / 1.7 runtime validation

Validated the guarded DLL on 2026-10-26 in the Steam-independent laptop games:
RimWorld 1.6.4871 rev597 and 1.7.4985 rev1269. Both load identical DLL bytes:

`3503da83f1826724f07b5ec8373e36dcf8dfd407fb00a3e1650dca05d60dad63`

The DLL was rebuilt against the 1.6 API targeting .NET Framework 4.8, with zero
warnings/errors. Both games select the root load folder and have Harmony,
version-specific HAR, installed Miho 1.6 content, and all five DLC enabled.
Steam remains uninitialized; no player saves or Workshop files were changed.

## Correctness

- 28 focused startup/rule assertions plus 11 fresh-process save/reload assertions
  passed per version (78 total), with zero recorded game errors in the final runs.
- Checks include a single loaded DLL/aggregator, Harmony registration, original
  comps removed on both races, delayed once-only severity application, source
  removal, suppression of a surviving legacy comp, and timer persistence.
- Class checks cover simultaneous triggers, no repeated severity increases,
  legacy overflow repair, rank-V recognition, all six families' stale starter
  cleanup, and duplicate starter definitions on different body parts.
- Tea, pipe, and restraint remain active with an established class; tea also
  remains active during class unlocking. Abomi output remains additive (0.25 to
  1.25), rather than inheriting upstream's generic output clamp.
- The Abomi fixture is discarded from WorldPawns before persistence testing;
  its upstream weapon graph produced duplicate-ID save/load errors in initial
  fixture attempts. This is not a claim to fix or validate Abomi persistence.

The lifecycle comparison ran 24 cases for each of upstream, the previous branch
build, and the guarded build on both games (144 cases; 48 for the candidate).
All candidate acquisition events and final class/Nojob states matched upstream
exactly in these fixtures. Each game exercised all six families, three Huntress
seeds, repeated/conflicting/after-class pills, overfilled starters, no-pill
controls, and a one-tick confirmation of the accelerated health-interval run.

One ordinary pill acquired class I in about 1.4 game days with the guarded build
and upstream. The previous build took about 39.4 days in 1.6 and 41.4 days in 1.7,
after 20–21 starter applications. The guarded build applies the starter once.
Repeated doses no longer cause 81–85-day acquisition delays; different pills no
longer grant two class families in these dependency-only fixtures.

## Performance

Apple M5, 16 GB RAM, macOS 26.6.2. Values below are microseconds per processing
interval for the aggregator itself; the original ten stub comps are excluded
in every mode. These are component microbenchmarks, not whole-game TPS/FPS.

| Game | State | Upstream | Previous branch | Guarded build |
| --- | --- | ---: | ---: | ---: |
| 1.6 | Idle | 3.559 | 0.619 | 0.446 |
| 1.6 | Established class | 3.528 | 0.633 | 0.456 |
| 1.6 | Unlocking | 3.693 | 0.630 | 0.490 |
| 1.7 | Idle | 1.602 | 0.210 | 0.189 |
| 1.7 | Established class | 1.514 | 0.218 | 0.205 |
| 1.7 | Unlocking | 1.705 | 0.228 | 0.215 |

The guarded build used about 22–28% less processing time than the previous branch
build in the measured 1.6 states, and 5–10% less in 1.7. Precomputed source indexes
offset the added class checks. It used about 86–88% less than installed upstream
in these processing-interval cases. Absolute savings are small; no whole-game
speedup is claimed.

The 1.7 guarded build allocated zero workload bytes per steady processing call
above measurement overhead. Upstream allocated 32 bytes in idle/established states
and 88 while unlocking. Mono's zero readings are not used as allocation evidence.

Each timing sample covers 200,000 calls after 100,000 warmup calls. 1.6 values
are medians of 18 samples across two independent processes. Some initial 1.7
benchmark-only idle samples varied during tiered JIT startup, so final 1.7 values
use nine samples after an additional two-second warmup per state. The initial
runs, repeat runs, and robust-warmup results are all preserved. The two standalone
binaries are Release builds with the same SDK/project; upstream is its installed
binary. The previous branch reference is commit `323226a` before class safeguards.

## Scope and reproduction

The fixture is a real 10-year-old Miho child ingesting real class pills. Natural
health/stage-giver and component methods run with unmodified timers and severity
rates. Ambient temperature is fixed at 21 C, with needs, AI, aging and unrelated
colony simulation paused. Optional Mohar Framework is absent; its nullifier
integration could change multi-class outcomes in another mod list. Tests establish
acquisition, not every later rank or an entire colony playthrough.

Miho itself still declares only 1.6; 1.7 selects its unchanged 1.6 content by game
fallback. Initial warnings from unrelated inactive Workshop mods and engine audio
loading precede the probe. Final runs recorded no game errors after probe startup.

See `Tests/Runtime/README.md` and `Tests/Lifecycle/README.md`. Raw evidence is at:

`/Users/darren/codex_workspace/miho-guarded-implementation-20261026/`

- `regression/results/guarded-clean-1.6/` and `guarded-clean-1.7/`: focused tests
  and fresh-process reloads, including game logs and disposable saves.
- `lifecycle/results/`: upstream/original/candidate lifecycle and independent
  timing processes; `1.7-*-warm` contains the final robust-warmup measurements.
- `results.json`: all analyzed results and binary hashes, including early timings.
- `before-guards.dll`: previous branch DLL used for the paired comparison.

The earlier exploratory class/safeguard audit remains under
`/Users/darren/codex_workspace/miho-class-lifecycle-20261026/`; its results are not
substituted for the candidate's validation. GitHub additionally checks source/DLL
fingerprints, supported versions, load folders, package XML, archive contents and
checksums. Its workflow does not launch RimWorld or compile this Workshop-dependent
mod; it validates the committed, locally rebuilt DLL.
