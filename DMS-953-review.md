# Code Review: DMS-953 — DDL Manifest Emitter

**Branch**: `DMS-953`
**Scope**: DDL Manifest Emitter implementation, golden tests, unit tests, DdlEmitCommand integration
**Files reviewed**: 13 DMS-953-specific files (~950 lines)

> Note: This review covers only DMS-953-specific commits (`a1cdc3a4..3ac4ee97`).
> Changes from merged PRs (DMS-996 deadlock retry, DMS-1044 plan contracts) are excluded.

---

## Correctness risks / gaps

### 1. MSSQL statement counter does not trim leading whitespace; PostgreSQL counter does

- **What's wrong**: `CountMssqlStatements` (`DdlManifestEmitter.cs:139`) uses `rawLine.TrimEnd('\r')` then checks `line.SequenceEqual("GO")` — requiring `GO` at column 0 with no leading spaces. Meanwhile, `CountPgsqlStatements` (`DdlManifestEmitter.cs:199`) uses `rawLine.TrimEnd('\r').Trim()` — handling indented lines. If the MSSQL emitter ever produces an indented `GO` line, the count will be wrong silently.
- **Evidence**: `DdlManifestEmitter.cs:139` vs `DdlManifestEmitter.cs:199`
- **Impact**: Incorrect `statement_count` in the DDL manifest for MSSQL if the emitter's output format changes. Currently safe because the MSSQL emitter emits `GO` at column 0.
- **Recommendation**: Apply `Trim()` consistently in `CountMssqlStatements`:
  ```csharp
  var trimmed = rawLine.TrimEnd('\r').Trim();
  // then check trimmed.SequenceEqual("GO") and trimmed[^1] == ';'
  ```
  Also check semicolons on the trimmed line for consistency with PgSQL. This makes both implementations resilient to whitespace changes in emitter output.

---

## Design/spec drift

### 1. Manifest spec lists `ddl.manifest.json` as "Optional (recommended)" — implementation always emits it

- **Spec**: `ddl-generator-testing.md:99-101` — "Optional (recommended for diagnostics and fast diffs)"
- **Code**: `DdlEmitCommand.cs:183-189` — always emits `ddl.manifest.json`
- **Direction**: Update the spec from "Optional" to "Always emitted" to match implementation. The manifest is cheap and valuable — there is no reason to make it optional.

---

## Test coverage gaps

### 1. No test for `DdlManifestEmitter.Emit` with a single-dialect entry

- **Scenario**: When `--dialect pgsql` is used, only one entry is passed to `Emit`. The `ddl[]` array should contain exactly one element.
- **Why it matters**: The golden tests always build both dialects via `EmitDdlManifest`. Unit tests in `DdlManifestEmitterTests` also always pass two entries. A single-entry test would verify the manifest is valid JSON with a one-element array, and that sorting logic doesn't break with one item.
- **Suggested test**: `Given_DdlManifestEmitter_Emit_With_Single_Dialect` — pass one `DdlManifestEntry(SqlDialect.Pgsql, ...)`, assert `ddl.GetArrayLength() == 1` and `ddl[0].GetProperty("dialect").GetString() == "pgsql"`.

---

## Simplification / dead-code opportunities

### 1. Duplicate dialect-to-string mapping logic

- **Evidence**: `DdlManifestEmitter.ToManifestDialect` (`DdlManifestEmitter.cs:222-229`) and `DdlEmitCommand.DialectLabel` (`DdlEmitCommand.cs:217-223`) perform the identical `SqlDialect -> string` mapping.
- **Impact**: Low — two methods, same logic. But if a third dialect is added, both must be updated independently with risk of drift.
- **Recommendation**: Extract to a shared location (e.g., a `SqlDialect` extension method `dialect.ToLabel()`) or have `DdlEmitCommand` call `DdlManifestEmitter.ToManifestDialect` (would need to change visibility from `private` to `internal`). Not blocking.
