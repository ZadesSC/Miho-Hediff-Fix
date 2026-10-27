# Shared 1.6 / 1.7 runtime validation

Validated on 2026-10-26 with the Steam-independent laptop installations:

- RimWorld 1.6.4871 rev595: 18 startup/gameplay assertions and 11 fresh-process
  save/reload assertions passed; zero probe-recorded game errors in the final runs.
- RimWorld 1.7.4985 rev1269: the same 18 + 11 assertions passed; zero
  probe-recorded game errors in the final runs.
- Both games loaded identical DLL bytes, SHA256
  `6927a97e7185de69b72e4c122901923fcde6f97be9f95026c8d4258fa9d6719f`.
  This DLL was built against the 1.6 API targeting .NET Framework 4.8.
- Both selected only the root load folder. Harmony, version-specific HAR,
  the installed Miho 1.6 content, and all five DLC were enabled.
- Steam initialization failed as intended in all four final processes.

The probe verifies the loaded assembly hash, patch registration, replacement of
original comps on both target races, one aggregator per spawned Miho, all ten
Miho rules, missing-source timer reset, delay before application, exactly one
severity increment, stopping after source removal, public CompTick execution,
legacy-comp suppression, and timer persistence/continuation after restarting the
same game version. See `Tests/Runtime/README.md` for reproduction.

## Conflict found and corrected

The installed dependency bundles `MihoOptimization.dll`, whose aggregator uses
the same `remainingRuleTicks` save key as this mod. Initially both aggregators
ran on the same pawn, wrote duplicate XML elements, and restored the wrong timer.
This mod now initializes that upstream bootstrap before replacing its aggregator
comp, preventing startup ordering from adding it back. Its other optimizations
remain enabled. The final tests used the dependency unchanged and verified that
only this mod's aggregator remained, with the saved timer restored exactly.

An initial severity assertion used a naturally maxed-out Miho hediff; the fixture
now sets it to 0.2 before checking the expected increment to 0.3. Earlier random
quicktest generation also logged an implant-part error in 1.6 and a tribal-leader
work-tag generation error in 1.7. Final runs with the named non-magic Miho
colonist fixture completed without recorded game errors. These tests cover this
fix's integration, not every feature of the upstream mods. Miho's installed
About.xml still declares only 1.6; 1.7 selects its 1.6 content by game fallback.
Startup discovery also logs warnings from unrelated inactive Workshop mods.

## Evidence

Raw reports and disposable saves are outside the runtime package at
`/Users/darren/codex_workspace/miho-validation-20261026/`:

- `reference-manifest.json`: game versions and dependency DLL hashes.
- `runtime/results/1.6-shared/` and `runtime/results/1.7-shared/`: final prepare
  and resume JSONL assertions, summaries, manifests, saves, and full logs.
- Earlier result folders preserve the fixture failures and duplicate-save-key
  finding. Only the `*-shared` runs validate the final DLL.

The GitHub package system separately validates fingerprints, supported versions,
load folders, XML, archive contents and checksums. Its shared 14-test suite and
consumer workflow actionlint checks passed locally. It does not launch RimWorld.
