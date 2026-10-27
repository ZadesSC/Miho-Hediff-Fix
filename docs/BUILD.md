# GitHub packages and releases

This repository uses the shared RimWorld build system pinned in both workflows
and `.github/rimworld-build.json`. See the [shared reference](https://github.com/ZadesSC/HighDensityHydroponics/blob/25c60977b5dbd240e3624ea963e734699139cb43/docs/rimworld-build-system.md).

Pushes and pull requests to `main` or `dev` produce an installable ZIP and
SHA256SUMS under Actions → Build dev package. Create release accepts calendar
versions (`vYYYY.MM.DD[.N]`) or allocates one when left blank. Stable releases
use `main`; prereleases can use `dev`. Publishing a release is a separate action.

CI validates committed DLLs against `.github/assembly-fingerprints.json` because
Miho's Workshop assembly is required to compile and is not available through the
shared reference-package system. No game or third-party DLLs are redistributed.
The initial infrastructure fingerprint records the existing source and binary;
it does not establish that the existing DLL was rebuilt from those sources.
After rebuilding and validating a source change, refresh the manifest using the
pinned shared helper's `fingerprint --root .` command and commit both together.

## Rebuilding the shared DLL

Build once against the 1.6 API, then validate the same DLL on both games:

```sh
dotnet build Source/MihoHediffFix.csproj -c Release \
  -p:RimWorldManagedDir="/path/to/RimWorld/Managed" \
  -p:HarmonyAssembly="/path/to/Harmony/1.6/Assemblies/0Harmony.dll" \
  -p:MihoAssembly="/path/to/Miho/1.6/Assemblies/CheckHediffMakeHediff.dll"
```

The project targets .NET Framework 4.8 and outputs only this mod's DLL/symbols
to root `Assemblies`. Neither reference assemblies nor dependencies are copied.
`LoadFolders.xml` selects the same root content for 1.6 and 1.7. Replace the old
installation folder when updating so an obsolete `1.6/Assemblies` cannot linger.

After in-game validation, refresh the committed fingerprints. GitHub checks
source/DLL drift and package contents; it does not run the game or rebuild this
Workshop-dependent mod.
