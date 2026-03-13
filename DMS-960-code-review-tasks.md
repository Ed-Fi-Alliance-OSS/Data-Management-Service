# Code Review Tasks: DMS-960 — Authoritative Golden Directory Comparisons (No DB)

**Scope**: 2 C# files changed (1 new, 1 modified), 7 golden/fixture files added (~392K lines total, 14MB).

---

## Correctness Risks / Gaps

### Task 1: Resolve `fixture.json` fallback ambiguity when both locations exist

**File**: `FixtureConfig.cs:44-48`

The new fallback checks the fixture root first, then `inputs/`. If a fixture directory ever contains `fixture.json` in both locations (root and `inputs/`), the root file wins silently with no warning or error.

- **Evidence**: `FixtureConfig.cs:41-48` — the first `File.Exists` check at line 42 takes priority; the `inputs/` path at line 47 is only reached when the root file is absent.
- **Impact**: Low probability today (only one authoritative fixture), but if a contributor copies the small-fixture pattern into an authoritative directory and also has an `inputs/fixture.json`, they'll get the wrong config with no error.
- **Action**: Log a warning or throw when both files exist, or pick a single canonical location and remove the fallback. The dual-path convention is already a maintenance hazard.

---

## Test Coverage Gaps

### Task 2: Add unit test for the `fixture.json` fallback path

**File**: `FixtureConfig.cs` (new code path in `FixtureConfigReader.Read`)

The `FixtureConfigReader.Read` method gained a new code path (fall back to `inputs/fixture.json`), but there is no unit test exercising it in isolation. The authoritative golden test implicitly exercises it, but if the fallback breaks, the failure will manifest as a cryptic `FileNotFoundException` rather than a clear regression signal.

- **Why it matters**: The fallback is the only behavioral change to existing infrastructure in this PR. A dedicated test like `Given_FixtureConfigReader_When_FixtureJson_In_Inputs` would pin this contract.
- **Action**: Create a temp directory with `fixture.json` only in `inputs/`, call `FixtureConfigReader.Read`, assert it returns a valid config.

---

## Simplification / Dead-Code Opportunities

### Task 3: Extract shared base class to eliminate test duplication

**Files**: `AuthoritativeDdlGoldenTests.cs:13-89` vs `FixtureRunnerTests.cs:41-115`

Every test method is identical except the class name and the fixture path. Seven copy-pasted test methods.

- **Impact**: If the fixture output contract changes (e.g., a new artifact type is added), every fixture test class must be updated manually. With the authoritative fixture now added, there are two nearly-identical test classes (three if someone adds `sample/`).
- **Action**: Extract a base class or a parameterized `[TestFixtureSource]` that takes the fixture path. The per-fixture class would only declare the path and category. This eliminates ~70 lines of duplication per fixture and makes adding new fixtures a one-liner.

---

## Maintainability Risks

### Task 4: Standardize `fixture.json` placement convention

**Files**: `Fixtures/small/minimal/fixture.json` (root) vs `Fixtures/authoritative/ds-5.2/inputs/fixture.json` (nested)

Small fixtures (which predated this PR) place `fixture.json` at the fixture root. The new authoritative fixture places it in `inputs/fixture.json`. The fallback in `FixtureConfig.cs` papers over this, but a contributor adding a new fixture has no clear signal about which convention to follow.

- **Action**: Pick one convention and migrate the other. If `inputs/` is the authoritative pattern going forward, move the small fixture configs there too and remove the root-level check. If root is canonical, move the authoritative `fixture.json` out of `inputs/`.

### Task 5: Centralize fixture path resolution to reduce fragility

**File**: `AuthoritativeDdlGoldenTests.cs:22-24`

The path `Path.Combine(projectRoot, "..", "Fixtures", "authoritative", "ds-5.2")` navigates up from the test project directory to the shared `backend/Fixtures/` directory. This works but couples the test to the relative directory layout. If the test project or fixtures directory is ever moved, this breaks with a non-obvious `DirectoryNotFoundException`.

- **Evidence**: Small fixtures use `Fixtures/` *inside* the test project (`FixtureRunnerTests.cs:50`), while authoritative fixtures use a sibling directory outside the project. Two different navigation patterns for the same concept.
- **Action**: Consider a shared constant or helper method (e.g., `FixtureTestHelper.FindAuthoritativeFixtureRoot()`) to centralize the path resolution, especially since Plans tests use similar navigation (`../Fixtures/authoritative/...`).
