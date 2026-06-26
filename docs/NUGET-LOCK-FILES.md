# NuGet Lock Files

## Problem

.NET projects using NuGet Central Package Management
([`src/Directory.Packages.props`](../src/Directory.Packages.props)) pin _direct_
dependency versions, but do not pin _transitive_ dependency versions — the
packages that your packages depend on. As a result:

- Two restores at different times may resolve different transitive versions even
  if no `.csproj` or `Directory.Packages.props` changed.
- CI may silently pick up a newly published transitive package between the time a
  developer restores locally and the time CI runs.
- Supply-chain attacks targeting transitive dependencies (typosquatting,
  dependency confusion, compromised patch releases) are harder to detect because
  there is no committed record of what versions were previously resolved.

The goal is to lock the full dependency graph — direct **and** transitive — at
exact versions, enforce that lock everywhere it is restored (PR CI **and** the
Docker image builds), and keep it automatically up to date when Dependabot bumps
a direct dependency.

## How it works

### 1. Lock files are enabled globally

NuGet generates a `packages.lock.json` next to each project's `.csproj`,
recording the resolved version of every package in the full dependency graph.
Committed to source control, it becomes an auditable, diffable record of exactly
what is restored.

This is enabled with `RestorePackagesWithLockFile` in **both**
[`src/dms/Directory.Build.props`](../src/dms/Directory.Build.props) and
[`src/config/Directory.Build.props`](../src/config/Directory.Build.props):

```xml
<PropertyGroup>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
</PropertyGroup>
```

> [!NOTE]
> There is no shared `src/Directory.Build.props`, and a nested
> `Directory.Build.props` does not auto-import a parent — so the property must be
> present in **both** files. Each `src` project has its own committed
> `packages.lock.json`.

### 2. Locked mode in PR CI

Each PR workflow has a dedicated `verify-lock-files` gate job that runs
`dotnet restore <solution> --locked-mode`
([DMS](../.github/workflows/on-dms-pullrequest.yml),
[Config](../.github/workflows/on-config-pullrequest.yml)). `--locked-mode` fails
fast if a committed lock file is out of sync with the `.csproj` /
`Directory.Packages.props` state — for example, if a package reference is added
without regenerating. No `paths:` change is needed: lock files live under
`src/dms/**` / `src/config/**`, which the existing filters already match.

### 3. Locked mode in the Docker image builds

The published images are the security-critical artifact, so they are built from
the committed lock graph too. [`src/dms/Dockerfile`](../src/dms/Dockerfile) and
[`src/config/Dockerfile`](../src/config/Dockerfile) copy each project's
`packages.lock.json` alongside its `.csproj` and pass `--locked-mode` to every
`dotnet restore`.

> [!IMPORTANT]
> **Maintenance hotspot:** each Dockerfile's `packages.lock.json` COPY list must
> mirror its `.csproj` COPY list. A future `<ProjectReference>` change can pull a
> new project into the restore closure; if its lock file is not also copied, the
> in-image `--locked-mode` restore breaks. A forgotten COPY entry is caught in CI
> by [`LockFileParity.Tests.ps1`](../eng/docker-compose/tests/LockFileParity.Tests.ps1),
> but the lock-file _contents_ are still validated only by the `--locked-mode`
> restore, so build both images after changing project references.

### 4. Dependabot cooldown

[`.github/dependabot.yml`](../.github/dependabot.yml) declares a `cooldown` block
that delays version-update PRs for freshly published packages, giving security
vendors time to flag poisoned releases first:

```yaml
cooldown:
  default-days: 7
  semver-patch-days: 5
  semver-minor-days: 7
  semver-major-days: 14
```

> [!NOTE]
> Cooldown applies to **version updates only**. Dependabot **security** updates
> are not delayed, so vulnerability fixes still arrive immediately. Use the
> current `*-days` schema keys — the older `semver-patch:` short form is silently
> ignored by GitHub.

### 5. Auto-regeneration on Dependabot PRs

Dependabot bumps `Directory.Packages.props`. Its native NuGet updater
regenerates the lock files for the **directly** affected projects in its own bump
commit, but it does **not** update lock files for projects affected only
_transitively_ via a `<ProjectReference>` whose graph changed (dependabot-core
[#13950](https://github.com/dependabot/dependabot-core/issues/13950)). Without
help, those would fail the `--locked-mode` gates with `NU1004`.

[`.github/workflows/dependabot-lock-file.yml`](../.github/workflows/dependabot-lock-file.yml)
covers the gap. On a Dependabot PR touching `src/Directory.Packages.props` it
checks out the PR branch, runs `dotnet restore --force-evaluate` on **both**
solutions (the shared `Directory.Packages.props` feeds both), and commits/pushes
any regenerated lock files. `--force-evaluate` re-derives the full graph rather
than reusing cached resolution.

> [!NOTE]
> The workflow pushes with the built-in `GITHUB_TOKEN`
> (`permissions: contents: write`) — **not** a Personal Access Token. **No
> Dependabot secret needs to be provisioned.**

**Expected flow (no transitive change):** when a bump leaves every project's
transitive closure unchanged, Dependabot's own commit already carries the
regenerated lock files, so this workflow finds nothing to commit (no-op) and the
`--locked-mode` gates pass on the first CI run.

**Transitive `<ProjectReference>` change (#13950) — expect this regularly:**
because this codebase has a deep, layered project-reference graph, a bump to one
direct package often shifts the transitive closure of projects that do not
reference it directly. Dependabot leaves those lock files stale, so this workflow
regenerates and pushes them — but a push made with the built-in `GITHUB_TOKEN`
does **not** trigger new workflow runs (GitHub's recursion guard), so the
`--locked-mode` gate does not re-run and stays red on the corrected commit.
Re-triggering the gate automatically would require pushing with a PAT or GitHub
App token — i.e. provisioning a secret — which this design deliberately avoids.
**Treat the manual clear below as a routine step on Dependabot PRs, not a rare
exception.** Recovery, most reliable first:

1. push a **maintainer-owned no-op commit** to the PR branch — a push not made
   with `GITHUB_TOKEN` re-triggers CI, so this is the dependable fix; or
2. **approve the pending workflow run** _if_ GitHub created one — a `GITHUB_TOKEN`
   push usually creates no run, so there is often nothing to approve.

> [!WARNING]
> Two actions that look like fixes but are **not** reliable here:
>
> - **`@dependabot recreate`** rebuilds the PR from scratch, which still omits the
>   transitive lock updates (#13950); the gate fails again and the workflow
>   re-pushes via `GITHUB_TOKEN`, returning to the same dead-end.
> - **"Re-run jobs"** on the failed run re-runs against the original commit SHA,
>   not the pushed lock-file commit.

## Regenerating lock files locally

After adding/removing a package or changing a `<ProjectReference>`, regenerate
and commit the affected lock files:

```bash
dotnet restore src/dms/EdFi.DataManagementService.sln --force-evaluate
dotnet restore src/config/EdFi.DmsConfigurationService.sln --force-evaluate
```

Local `Restore` (without `--locked-mode`) stays non-locked so day-to-day builds
consult the lock files without forcing a regenerate step; CI and the Docker
builds are the enforcement points.

> [!NOTE]
> Lock files are normally OS-independent. If a Linux `--locked-mode` run (the CI
> gate on Ubuntu, or the Docker build on Alpine) ever reports a mismatch that
> does not reproduce on Windows, regenerate on Linux — via WSL or the SDK
> container — and commit the result.

## Maintainer notes

- **Build-script templates reproduce the full restore graph.** `BuildAndPublish`
  in [`build-dms.ps1`](../build-dms.ps1) / [`build-config.ps1`](../build-config.ps1)
  regenerates each area's `Directory.Build.props` from the `SetDMSAssemblyInfo`
  here-string. Those templates must reproduce the **restore-relevant** content of
  the committed props — `RestorePackagesWithLockFile` **and** the analyzer
  `PackageReference`s (`Microsoft.CodeAnalysis.CSharp.CodeStyle`,
  `SonarAnalyzer.CSharp`, including their `PrivateAssets`/`IncludeAssets`) — or a
  regenerated props would restore a different graph and dirty/break the lock
  files. CI enforces this parity via
  [`LockFileParity.Tests.ps1`](../eng/docker-compose/tests/LockFileParity.Tests.ps1).
- **`find` over shell glob.** Shell `**` expansion is off by default on Ubuntu
  runners; stage lock files with
  `find src -name "packages.lock.json" -print0 | xargs -0 --no-run-if-empty git add`.
- **Idempotent commit.** `git diff --staged --quiet || git commit …` exits cleanly
  when nothing changed while still surfacing real failures.
- **First live Dependabot PR is the true end-to-end test** of the regeneration
  workflow — the Dependabot trigger cannot be exercised on demand.
