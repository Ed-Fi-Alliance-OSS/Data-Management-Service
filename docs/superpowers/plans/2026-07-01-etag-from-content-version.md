# Derive `_etag` from `ContentVersion` + representation `variantKey` — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the SHA-256 content-hash `_etag` with a composed, strong-validator tag `"{ContentVersion}-{variantKey}"`, eliminating the per-write hydrate-materialize-hash readback and the per-read rehash.

**Architecture:** `_etag` becomes a string composition of the stored `dms.Document.ContentVersion` (an opaque string) and a short, deterministic `variantKey` that encodes the byte-affecting representation selectors (schema epoch, response format, readable profile, link mode). No document body is hashed on any path; the write path stops reading the document back solely to build a header; the `If-Match` check compares reconstructed tags instead of re-hashing current state.

**Tech Stack:** C# / .NET, xUnit + FluentAssertions (unit), Reqnroll/SpecFlow (E2E `.feature`), PostgreSQL + SQL Server relational backend. Single backend only (the legacy JSON-document backend was removed in `DMS-1239`, commit `57d184e8`).

**Spec (source of truth):** `reference/adr-etag-from-content-version.md` (the ADR) and `reference/adr-etag-from-content-version-designdoc-edits.md` (staged design-doc wording). Read both before starting.

## Human-review / AI-use note

This plan was drafted with AI assistance from the ADR analysis; it has **not** been executed and the ADR is still `Proposed — DRAFT`. Do not merge any resulting code before the development team accepts the ADR and a human reviews both the design-doc edits and the code. Accountability for the design rests with the team.

## Decisions taken in this plan (with ADR anchors, flip at review)

These resolve open forks so the plan is executable. Each is a one-line change to flip if the team disagrees.

1. **Etag format:** `"{ContentVersion}-{variantKey}"`, `ContentVersion` serialized as an **opaque string**, never parsed numerically (ADR §"ETag format…", §`variantKey` encoding). **Strong** validator: `ETag`/`If-Match` **quoted**, no `W/` (ADR §"ETag format…").
2. **`variantKey` = `schemaEpoch "." format "." profileCode "." linkFlag`** — the structured, debuggable form (ADR §`variantKey` encoding). The fixed-length opaque-hash alternative is noted per-component where relevant.
3. **`profileCode` needs new infrastructure.** The ADR assumed "the readable profile's stable compile-time index within the current `MappingSet`" already exists; the code map shows **profiles are keyed by name, with no stable index**. This plan builds a small stable-ordinal registry (Task 1.3). If the team prefers zero new infra, swap `profileCode` to the ADR's opaque-hash alternative (a short hash of the profile name) — same interface, different body.
4. **`If-Match` comparison is strict full-tag** (ADR §"`If-Match` comparison (decision needed)": recommended strict-adherence). Cross-variant `If-Match` returns `412`. A lenient `ContentVersion`-only comparison is the documented alternative; it is one predicate change in Task 4.1.
5. **`ETag` header is currently emitted unquoted** (code map §9) — technically non-conformant. This plan quotes it and makes `If-Match` quote-tolerant (Phase 5). Flag to team: this is a client-visible wire change, acceptable because the contract has not shipped.

## Preconditions to confirm before coding (ADR open question)

The ADR's blocking open question: an **identity-update cascade into referrers** MUST bump each referrer's `ContentVersion`, or `_etag` will not change when a referrer's served bytes change. This is a correctness precondition independent of the etag decision. Task 0.1 verifies it.

## File structure

**New files (pure, unit-tested in isolation before any wiring):**

- `src/dms/core/EdFi.DataManagementService.Core/Utilities/EtagValue.cs` — pure string helpers: `Compose(contentVersion, variantKey)`, `ToHeaderValue` (quote), `TryParseHeaderValue` (unquote). No dependencies. Responsibility: the wire format of an etag, in one place.
- `src/dms/backend/EdFi.DataManagementService.Backend/Etag/VariantKey.cs` — the `variantKey` value type + `VariantKeyFactory` (schemaEpoch derivation, format registry, linkFlag). Responsibility: turn representation context into the `variantKey` token.
- `src/dms/backend/EdFi.DataManagementService.Backend/Etag/ProfileVariantCodeRegistry.cs` — assigns a stable ordinal to each profile within a `MappingSet`. Responsibility: `profileCode`.
- `src/dms/backend/EdFi.DataManagementService.Backend/Etag/EtagComposer.cs` — `IEtagComposer` + impl combining `ContentVersion` (as string) + `VariantKey` via `EtagValue.Compose`. Responsibility: the single place a served etag is built (replaces `RelationalApiMetadataFormatter.FormatEtag` hashing).

**Modified files:**

- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalApiMetadataFormatter.cs` — stop hashing; delegate to `EtagComposer`. The `FormatEtag(ExtractedDescriptorBody)` hash overload becomes obsolete (descriptor etag also composed from `ContentVersion`).
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalReadMaterializer.cs` — `InjectApiMetadata` (lines 199-225): compose etag from `documentMetadata.ContentVersion` + a `VariantKey` threaded through the materialization request; delete the `FormatEtag(materializedDocument)` call at line 217.
- The materialization request records that carry data into the materializer (add `VariantKey`) — exact type names read in Task 2.1.
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalCurrentEtagPreconditionChecker.cs` — replace `Materialize` + `FormatEtag` (lines 130-139) with a composed expected tag from `currentState.DocumentMetadata.ContentVersion` + the request's `VariantKey`; strict full-tag compare (line 149).
- `src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs` — build the success-result etag from the persisted `ContentVersion` + `VariantKey`; stop performing the readback solely for the etag (lines ~310-365; `RelationalCommittedRepresentationReader`).
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalCommittedRepresentationReader.cs` — either return only `ContentVersion`/`ContentLastModifiedAt` (light) or be removed from the etag path (Task 3.3 decides after reading consumers).
- Persist result record `RelationalWritePersistResult` — add `ContentVersion` + `ContentLastModifiedAt` from `INSERT/UPDATE … RETURNING` (Task 3.2).
- Frontend header handling (`AspNetCoreFrontend.cs`) + `WritePreconditionFactory.cs` — quote/unquote (Phase 5).
- DI registration for `IEtagComposer` — read the backend's registration module in Task 1.4.
- Tests + design docs — Phase 7.

## Test commands (from repo root)

- Core unit: `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit`
- Backend unit: `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit`
- Frontend unit: `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit`
- Postgres integration: `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration`
- SQL Server integration: `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration`
- E2E: `dotnet test src/dms/tests/EdFi.DataManagementService.Tests.E2E`

Add `--filter "FullyQualifiedName~<Name>"` to target one class/method. Integration/E2E require their containers (see `eng/docker-compose/`).

---

## Phase 0 — Preconditions & safety net

### Task 0.1: Confirm identity-cascade bumps `ContentVersion`

**Files:**
- Read: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlIfMatchCascadeReferentialIdentityTests.cs`
- Read: `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlIfMatchCascadeReferentialIdentityTests.cs`
- Read: `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs` (cascade/descriptor stamping trigger; general identity-cascade DML)

- [ ] **Step 1: Read the cascade tests and DDL** to determine whether an identity change on a referenced resource updates referrers' `ContentVersion` (not just their `_etag` hash). The existing tests assert `If-Match` behavior on cascade, which currently rests on the *hash*; the new design rests on `ContentVersion`.

- [ ] **Step 2: Write a failing (or characterization) integration test** asserting `ContentVersion` strictly increases on a referrer after a referenced identity update.

```csharp
// In PostgresqlIfMatchCascadeReferentialIdentityTests.cs (mirror in Mssql...)
[Test]
public async Task Referenced_identity_update_bumps_referrer_ContentVersion()
{
    // Arrange: create referenced resource R and referrer F embedding R's identity.
    var referrerV1 = await GetContentVersion(F.DocumentId);
    // Act: update R's identity (cascade path).
    await UpdateIdentity(R, newIdentity);
    // Assert: F's ContentVersion increased.
    var referrerV2 = await GetContentVersion(F.DocumentId);
    referrerV2.Should().BeGreaterThan(referrerV1);
}
```

- [ ] **Step 3: Run it.** `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration --filter "FullyQualifiedName~Cascade"`
  - **If PASS:** precondition holds; keep the test as a regression guard.
  - **If FAIL:** STOP. This is a defect that blocks the ADR (and would equally break a hash that depends on cascade). Report to the team; do not proceed to Phase 3+ until fixed.

- [ ] **Step 4: Commit** (test only).
```bash
git add src/dms/backend/EdFi.DataManagementService.Backend.*.Tests.Integration
git commit -m "test: assert referenced-identity cascade bumps referrer ContentVersion"
```

### Task 0.2: Characterize current etag tests (snapshot what will change)

**Files:**
- Read: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/**/DescriptorWriteHandlerResponseEtagTests.cs`
- Read: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/**/RelationalCurrentEtagPreconditionCheckerTests.cs`
- Read: `src/dms/backend/EdFi.DataManagementService.Backend.*.Tests.Integration/**/*ProfileIfMatchEtag*.cs`
- Read: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/**/etag.feature`

- [ ] **Step 1: Run the full existing etag suite green** and record which assertions bind to the *hash* (exact base64 value, "non-canonical order hashes identically") versus only behavior (presence, 412 on mismatch).
```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit --filter "FullyQualifiedName~Etag"
```
- [ ] **Step 2: Note in the plan checklist** which tests must be rewritten in Phase 7 (the hash-value ones) versus which should keep passing unchanged (behavioral ones — a good regression signal that Phase 2-5 preserved semantics). No commit.

---

## Phase 1 — Pure helpers (no wiring)

Each helper is independently unit-tested and merged before any behavior changes, so later phases only wire tested pieces together.

### Task 1.1: `EtagValue` wire-format helper

**Files:**
- Create: `src/dms/core/EdFi.DataManagementService.Core/Utilities/EtagValue.cs`
- Test: `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Utilities/EtagValueTests.cs`

- [ ] **Step 1: Write the failing test.**
```csharp
using EdFi.DataManagementService.Core.Utilities;
using FluentAssertions;
using NUnit.Framework; // match the project's framework; see sibling tests

[TestFixture]
public class EtagValueTests
{
    [Test]
    public void Compose_joins_contentVersion_and_variantKey_with_hyphen()
    {
        EtagValue.Compose("5", "a1b2c3d4.j._.l").Should().Be("5-a1b2c3d4.j._.l");
    }

    [Test]
    public void ToHeaderValue_wraps_in_double_quotes()
    {
        EtagValue.ToHeaderValue("5-a1b2c3d4.j._.l").Should().Be("\"5-a1b2c3d4.j._.l\"");
    }

    [Test]
    public void TryParseHeaderValue_strips_quotes()
    {
        EtagValue.TryParseHeaderValue("\"5-a1b2c3d4.j._.l\"", out var v).Should().BeTrue();
        v.Should().Be("5-a1b2c3d4.j._.l");
    }

    [Test]
    public void TryParseHeaderValue_accepts_unquoted_for_backward_tolerance()
    {
        EtagValue.TryParseHeaderValue("5-a1b2c3d4.j._.l", out var v).Should().BeTrue();
        v.Should().Be("5-a1b2c3d4.j._.l");
    }

    [Test]
    public void TryParseHeaderValue_rejects_weak_validator()
    {
        EtagValue.TryParseHeaderValue("W/\"5-x\"", out _).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (type not defined).
`dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit --filter "FullyQualifiedName~EtagValue"`

- [ ] **Step 3: Implement.**
```csharp
// SPDX-License-Identifier: Apache-2.0
// (include the standard 4-line Ed-Fi license header used by sibling files)

namespace EdFi.DataManagementService.Core.Utilities;

/// <summary>
/// The wire format of an API <c>_etag</c>: an opaque strong entity-tag of the form
/// "{ContentVersion}-{variantKey}". The ContentVersion component is treated as an
/// opaque string and is never parsed or compared numerically (RFC 7232 §2.3, §3.1).
/// </summary>
public static class EtagValue
{
    public static string Compose(string contentVersion, string variantKey) =>
        $"{contentVersion}-{variantKey}";

    /// <summary>Quotes an opaque etag value for the ETag / If-Match HTTP header (strong, no W/).</summary>
    public static string ToHeaderValue(string etagValue) => $"\"{etagValue}\"";

    /// <summary>
    /// Extracts the opaque value from a strong entity-tag header. Rejects weak (W/) tags.
    /// Tolerates a bare unquoted value for robustness against non-conforming clients.
    /// </summary>
    public static bool TryParseHeaderValue(string? headerValue, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(headerValue))
        {
            return false;
        }
        if (headerValue.StartsWith("W/", StringComparison.Ordinal))
        {
            return false; // weak validators are not accepted for strong If-Match comparison
        }
        if (headerValue.Length >= 2 && headerValue[0] == '"' && headerValue[^1] == '"')
        {
            value = headerValue[1..^1];
            return true;
        }
        value = headerValue;
        return true;
    }
}
```

- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit.**
```bash
git add src/dms/core/EdFi.DataManagementService.Core/Utilities/EtagValue.cs src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Utilities/EtagValueTests.cs
git commit -m "feat: add EtagValue helper for composed strong entity-tag wire format"
```

### Task 1.2: `VariantKey` + `VariantKeyFactory`

**Files:**
- Read first: `src/dms/backend/EdFi.DataManagementService.Backend.External/MappingSetContracts.cs` (`MappingSetKey.EffectiveSchemaHash`), `src/dms/backend/EdFi.DataManagementService.Backend.External/ResourceLinksOptions.cs`.
- Create: `src/dms/backend/EdFi.DataManagementService.Backend/Etag/VariantKey.cs`
- Test: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Etag/VariantKeyTests.cs`

- [ ] **Step 1: Write the failing test.**
```csharp
[Test]
public void Format_code_j_no_profile_links_on()
{
    var key = VariantKeyFactory.Create(
        effectiveSchemaHash: "a1b2c3d4e5f6",
        format: ResponseFormat.Json,
        profileCode: VariantKey.NoProfileCode, // "_"
        linksEnabled: true);
    key.Value.Should().Be("a1b2c3d4.j._.l");
}

[Test]
public void Profile_index_and_links_off()
{
    var key = VariantKeyFactory.Create("a1b2c3d4e5f6", ResponseFormat.Json, "3", linksEnabled: false);
    key.Value.Should().Be("a1b2c3d4.j.3.n");
}

[Test]
public void SchemaEpoch_is_first_8_lowercase_hex_of_schema_hash()
{
    var key = VariantKeyFactory.Create("A1B2C3D4FFFF", ResponseFormat.Json, "_", true);
    key.Value.Should().StartWith("a1b2c3d4."); // lowercased, truncated to 8
}
```

- [ ] **Step 2: Run — expect FAIL.**
`dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit --filter "FullyQualifiedName~VariantKey"`

- [ ] **Step 3: Implement.**
```csharp
// (license header)
namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>Stable server-side registry of response media types. MUST NOT be derived from a raw
/// media-type string at runtime. JSON is the only format today.</summary>
public enum ResponseFormat
{
    Json,
}

/// <summary>
/// The representation discriminator embedded in a served etag so the tag is a strong validator
/// (RFC 7232 §2.1): schemaEpoch "." format "." profileCode "." linkFlag. All chars are valid etagc.
/// </summary>
public readonly record struct VariantKey(string Value)
{
    public const string NoProfileCode = "_";
    public override string ToString() => Value;
}

public static class VariantKeyFactory
{
    public static VariantKey Create(
        string effectiveSchemaHash,
        ResponseFormat format,
        string profileCode,
        bool linksEnabled)
    {
        ArgumentException.ThrowIfNullOrEmpty(effectiveSchemaHash);
        ArgumentException.ThrowIfNullOrEmpty(profileCode);

        var schemaEpoch = SchemaEpoch(effectiveSchemaHash);
        var formatCode = FormatCode(format);
        var linkFlag = linksEnabled ? "l" : "n";
        return new VariantKey($"{schemaEpoch}.{formatCode}.{profileCode}.{linkFlag}");
    }

    // First 8 lowercase hex chars of the in-force EffectiveSchemaHash (which is already lowercase hex,
    // per EffectiveSchemaHashProvider). Lowercase defensively.
    private static string SchemaEpoch(string effectiveSchemaHash)
    {
        var lower = effectiveSchemaHash.ToLowerInvariant();
        return lower.Length <= 8 ? lower : lower[..8];
    }

    private static string FormatCode(ResponseFormat format) => format switch
    {
        ResponseFormat.Json => "j",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No etag format code registered."),
    };
}
```

- [ ] **Step 4: Run — expect PASS. Step 5: Commit.**
```bash
git add src/dms/backend/EdFi.DataManagementService.Backend/Etag/VariantKey.cs src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Etag/VariantKeyTests.cs
git commit -m "feat: add VariantKey and VariantKeyFactory for representation-sensitive etags"
```

### Task 1.3: `ProfileVariantCodeRegistry` (stable profile ordinal)

**Files:**
- Read first: `src/dms/backend/EdFi.DataManagementService.Backend.External/MappingSetContracts.cs` and wherever profiles/`MappingSet` enumerate readable profiles; the read/write profile context types (`BackendProfileReadContext` / `BackendProfileWriteContext`). Determine the canonical set of profile names available at `MappingSet` compile time.
- Create: `src/dms/backend/EdFi.DataManagementService.Backend/Etag/ProfileVariantCodeRegistry.cs`
- Test: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Etag/ProfileVariantCodeRegistryTests.cs`

> **Why this exists:** the ADR assumed a stable compile-time profile index; the code has none. The registry assigns one deterministically by ordinal-sorting profile names within a `MappingSet`. Stability is only required within a `schemaEpoch`, and any profile *redefinition* rotates `schemaEpoch` (Task 1.2), so a sort-position ordinal is sufficient and unambiguous.
>
> **Alternative if the team rejects new infra:** replace `CodeFor` with the ADR's opaque form — return the first N hex of `SHA-256(profileName)`. Same signature; keep tests.

- [ ] **Step 1: Write the failing test.**
```csharp
[Test]
public void No_profile_maps_to_underscore()
{
    var reg = new ProfileVariantCodeRegistry(new[] { "b-profile", "a-profile" });
    reg.CodeFor(null).Should().Be("_");
}

[Test]
public void Codes_are_stable_ordinal_indices_independent_of_input_order()
{
    var reg1 = new ProfileVariantCodeRegistry(new[] { "b-profile", "a-profile" });
    var reg2 = new ProfileVariantCodeRegistry(new[] { "a-profile", "b-profile" });
    reg1.CodeFor("a-profile").Should().Be("0");
    reg1.CodeFor("b-profile").Should().Be("1");
    reg2.CodeFor("a-profile").Should().Be("0"); // order-independent
}

[Test]
public void Unknown_profile_throws()
{
    var reg = new ProfileVariantCodeRegistry(new[] { "a-profile" });
    Action act = () => reg.CodeFor("missing");
    act.Should().Throw<KeyNotFoundException>();
}
```

- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 3: Implement.**
```csharp
// (license header)
using System.Collections.Frozen;

namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>
/// Assigns each readable profile a stable, compile-time ordinal ("profileCode") within a MappingSet.
/// Ordinals are the ordinal-sort position of the profile name; stability is only required within a
/// schemaEpoch, and any profile redefinition rotates schemaEpoch, so the ordinal is unambiguous.
/// </summary>
public sealed class ProfileVariantCodeRegistry
{
    private readonly FrozenDictionary<string, string> _codeByName;

    public ProfileVariantCodeRegistry(IEnumerable<string> profileNames)
    {
        var ordered = profileNames.Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        _codeByName = ordered
            .Select((name, index) => (name, code: index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToFrozenDictionary(t => t.name, t => t.code, StringComparer.Ordinal);
    }

    public string CodeFor(string? profileName)
    {
        if (profileName is null)
        {
            return VariantKey.NoProfileCode;
        }
        return _codeByName.TryGetValue(profileName, out var code)
            ? code
            : throw new KeyNotFoundException($"Profile '{profileName}' is not registered for this MappingSet.");
    }
}
```

- [ ] **Step 4: Run — expect PASS. Step 5: Commit.**
```bash
git add src/dms/backend/EdFi.DataManagementService.Backend/Etag/ProfileVariantCodeRegistry.cs src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Etag/ProfileVariantCodeRegistryTests.cs
git commit -m "feat: add ProfileVariantCodeRegistry for stable profileCode in variantKey"
```

### Task 1.4: `EtagComposer`

**Files:**
- Read first: the backend DI/registration module (search `AddScoped`/`AddSingleton` for `IRelationalReadMaterializer` to find where backend services register) to know where to register `IEtagComposer`.
- Create: `src/dms/backend/EdFi.DataManagementService.Backend/Etag/EtagComposer.cs`
- Test: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Etag/EtagComposerTests.cs`

- [ ] **Step 1: Write the failing test.**
```csharp
[Test]
public void Compose_produces_ContentVersion_dash_variantKey_as_opaque_string()
{
    var composer = new EtagComposer();
    var key = new VariantKey("a1b2c3d4.j._.l");
    composer.Compose(contentVersion: 5, key).Should().Be("5-a1b2c3d4.j._.l");
}

[Test]
public void ContentVersion_is_serialized_as_string_not_parsed()
{
    var composer = new EtagComposer();
    composer.Compose(9007199254740993, new VariantKey("a1b2c3d4.j._.l"))
        .Should().StartWith("9007199254740993-"); // full bigint preserved as text
}
```

- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 3: Implement.**
```csharp
// (license header)
using System.Globalization;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Backend.Etag;

public interface IEtagComposer
{
    /// <summary>Composes the opaque etag value (unquoted). Callers quote for HTTP headers via EtagValue.ToHeaderValue.</summary>
    string Compose(long contentVersion, VariantKey variantKey);
}

public sealed class EtagComposer : IEtagComposer
{
    public string Compose(long contentVersion, VariantKey variantKey) =>
        EtagValue.Compose(contentVersion.ToString(CultureInfo.InvariantCulture), variantKey.Value);
}
```

- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Register `IEtagComposer` → `EtagComposer`** in the backend DI module found above (singleton — it is stateless).
- [ ] **Step 6: Build to confirm registration compiles.** `dotnet build src/dms/backend/EdFi.DataManagementService.Backend`
- [ ] **Step 7: Commit.**
```bash
git add src/dms/backend/EdFi.DataManagementService.Backend/Etag/EtagComposer.cs src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Etag/EtagComposerTests.cs <di-module-file>
git commit -m "feat: add IEtagComposer and register it in backend DI"
```

---

## Phase 2 — Read path: compose etag instead of hashing

### Task 2.1: Thread `VariantKey` into the materialization request

**Files:**
- Read first: `src/dms/backend/EdFi.DataManagementService.Backend/RelationalReadMaterializer.cs` (full file, esp. the `RelationalReadMaterializationRequest` / `RelationalReadPageMaterializationRequest` record definitions and `MaterializePage`), and every construction site of those requests (read path handler, `RelationalCommittedRepresentationReader`, `RelationalCurrentEtagPreconditionChecker`).

- [ ] **Step 1: Add a `VariantKey VariantKey` member** to the materialization request record(s) that reach `InjectApiMetadata`. Because the materializer currently receives only `readPlan`, `documentMetadata`, rows, and `readMode`, the `VariantKey` must be supplied by callers (they hold `MappingSet` + profile + link context; the materializer does not).
  - **Note (both request records):** the single-doc `Materialize` internally builds a `RelationalReadPageMaterializationRequest` and delegates to `MaterializePage` (which also serves GET-collection). Add `VariantKey` to **both** `RelationalReadMaterializationRequest` and `RelationalReadPageMaterializationRequest` and ensure it flows single-doc → page → `InjectApiMetadata`.
  - **Note (static → instance):** `InjectApiMetadata` and its caller `ApplyReadMode` are currently `static`. To reach the injected `IEtagComposer`, remove their `static` modifiers (or thread `IEtagComposer` + `VariantKey` explicitly down `MaterializePage` → `ApplyReadMode` → `InjectApiMetadata`).
- [ ] **Step 2: Build — expect FAIL at all construction sites** (missing argument). This compiler error is the checklist of call sites to fix in Task 2.3 and Phases 3-4.
`dotnet build src/dms/backend/EdFi.DataManagementService.Backend`
- [ ] **Step 3:** Do not commit yet (broken build); proceed to Task 2.2 and 2.3 in the same commit.

### Task 2.2: Rewrite `InjectApiMetadata` to compose

**Files:**
- Modify: `src/dms/backend/EdFi.DataManagementService.Backend/RelationalReadMaterializer.cs` lines 199-225.

- [ ] **Step 1: Add a unit test** for the materializer's external-response metadata that asserts the composed form, not a hash.
```csharp
// In the existing RelationalReadMaterializer test class (Backend.Tests.Unit)
[Test]
public void ExternalResponse_injects_composed_etag_from_ContentVersion_and_variantKey()
{
    // Arrange a materialization request with a known ContentVersion (e.g. 7) and
    // VariantKey("a1b2c3d4.j._.l") in ExternalResponse mode.
    var result = materializer.Materialize(request);
    result["_etag"]!.GetValue<string>().Should().Be("7-a1b2c3d4.j._.l");
}
```

- [ ] **Step 2: Run — expect FAIL** (still hashing).
- [ ] **Step 3: Replace the etag line.** Change `InjectApiMetadata` to accept the `VariantKey` (from the request) and the injected `IEtagComposer`, and replace lines 213-217:
```csharp
// remove the DMS-1005 hashing comment and this line:
//   var etag = RelationalApiMetadataFormatter.FormatEtag(materializedDocument);
// with:
var etag = _etagComposer.Compose(documentMetadata.ContentVersion, request.VariantKey);
documentObject[IdPropertyName] = documentMetadata.DocumentUuid.ToString();
documentObject[EtagPropertyName] = etag; // opaque, unquoted in the body
```
Inject `IEtagComposer` into the materializer's constructor and store as `_etagComposer`. `documentMetadata.ContentVersion` already exists on `DocumentMetadataRow` (confirmed: it is read at `RelationalCurrentEtagPreconditionChecker.cs:142`).

- [ ] **Step 4: Run the materializer test — expect PASS** (after Task 2.3 fixes call sites enough to build).

### Task 2.3: Fix read-path call sites to build `VariantKey`

**Files:**
- Modify: the GET-by-id / GET-collection read handler(s) that build the materialization request (found in Task 2.1).

- [ ] **Step 1:** At each read call site, construct the `VariantKey` from context available there:
```csharp
var variantKey = VariantKeyFactory.Create(
    effectiveSchemaHash: mappingSet.Key.EffectiveSchemaHash,
    format: ResponseFormat.Json,
    profileCode: profileVariantCodeRegistry.CodeFor(readProfileNameOrNull),
    linksEnabled: resourceLinksOptions.Enabled);
```
Pass it into the (now-extended) materialization request. Obtain the `ProfileVariantCodeRegistry` for the current `MappingSet` (build once per `MappingSet` and cache; see Task 3.2 note on caching).

- [ ] **Step 2: Build — expect PASS.** `dotnet build src/dms/backend/EdFi.DataManagementService.Backend`
- [ ] **Step 3: Run backend unit + read integration.**
```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit --filter "FullyQualifiedName~Materializer"
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration --filter "FullyQualifiedName~Get"
```
- [ ] **Step 4: Commit Phase 2.**
```bash
git add src/dms/backend/EdFi.DataManagementService.Backend
git commit -m "feat: compose read-path _etag from ContentVersion + variantKey (no hashing)"
```

---

## Phase 3 — Write path: stop reading back for the etag

### Task 3.1: Confirm the readback is used only for `_etag`/`_lastModifiedDate`

**Files:**
- Read: `src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs` (lines ~310-365), `RelationalCommittedRepresentationReader.cs`, `RelationalWriteExecutorResults.BuildAppliedWriteSuccessResult`, and the Core handlers `UpsertHandler.cs` / `UpdateByIdHandler.cs` (how they consume the write result — does the POST/PUT response body use the committed representation, or only headers?).

- [ ] **Step 1:** Confirm the POST/PUT success response body is `null` (headers-only) and the committed representation feeds **only** `_etag` and `_lastModifiedDate`. Record findings inline in the commit message of Task 3.3.
  - **If the body is genuinely null:** the full hydrate-materialize readback can be removed from the write path (Task 3.3).
  - **If the body is used** (unexpected vs. ADR): keep the readback for the body, but still replace its etag with the composed value; skip Task 3.3's removal and only do Task 3.2's etag composition. Note the divergence for the team.

### Task 3.2: Return `ContentVersion` + `ContentLastModifiedAt` from persistence

**Files:**
- Read first: the persistence command builder(s) for INSERT/UPDATE on `dms.Document`, and `RelationalWritePersistResult`.
- Modify: `RelationalWritePersistResult` (add `long ContentVersion`, `DateTimeOffset ContentLastModifiedAt`), the INSERT/UPDATE SQL to `… RETURNING "ContentVersion", "ContentLastModifiedAt"` (PostgreSQL) / `OUTPUT INSERTED.…` (SQL Server), and the mapping that reads them.

- [ ] **Step 1: Write/extend an integration test** asserting a POST/PUT surfaces the persisted `ContentVersion` (monotonic) and `ContentLastModifiedAt` without a second materialization.
- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 3: Implement** the `RETURNING`/`OUTPUT` change on both dialects and populate `RelationalWritePersistResult`.
- [ ] **Step 4: Build both dialects; run** the new test on Postgres and MSSQL integration projects.
- [ ] **Step 5: Commit.**
```bash
git add src/dms/backend/EdFi.DataManagementService.Backend
git commit -m "feat: return ContentVersion and ContentLastModifiedAt from document persistence"
```

> **Caching note:** build the `ProfileVariantCodeRegistry` and the `schemaEpoch` once per `MappingSet` (they are constant for a loaded schema) and cache alongside the `MappingSet`, so no per-request hashing or sorting occurs. `linkFlag` is a config read; `format` is a constant. This preserves the ADR's zero-hash performance goal.

### Task 3.3: Build the write success etag by composition; remove the etag-only readback

**Files:**
- Modify: `DefaultRelationalWriteExecutor.cs` (success + no-op success paths, lines ~310-365), `RelationalCommittedRepresentationReader.cs`.

- [ ] **Step 1: Write/extend a test** (backend unit or seam) asserting a POST/PUT success result carries `_etag == "{ContentVersion}-{variantKey}"` and `_lastModifiedDate` from the persisted stamps, with **no** call into `RelationalCommittedRepresentationReader`/`Materialize` on the success path. Use a spy/mock on the reader to assert it is not invoked (when body is null per Task 3.1).
- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 3: Implement.** In the applied-write and guarded-no-op success paths, compute:
```csharp
var variantKey = VariantKeyFactory.Create(
    persistContext.MappingSet.Key.EffectiveSchemaHash,
    ResponseFormat.Json,
    profileVariantCodeRegistry.CodeFor(writeProfileNameOrNull),
    resourceLinksOptions.Enabled);
var etag = _etagComposer.Compose(persistedTarget.ContentVersion, variantKey);
// build success result with etag + persistedTarget.ContentLastModifiedAt; do not call the readback for the etag.
```
Remove (or bypass) the `_committedRepresentationReader.ReadAsync(...)` call on the etag path. Keep `CommitAsync` ordering unchanged. If the reader has no remaining callers, delete it and its DI registration in Task 6.1.

- [ ] **Step 4: Run backend unit + write integration (both dialects).**
```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit --filter "FullyQualifiedName~Write"
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration --filter "FullyQualifiedName~Upsert"
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration --filter "FullyQualifiedName~Upsert"
```
- [ ] **Step 5: Commit.**
```bash
git add src/dms/backend/EdFi.DataManagementService.Backend
git commit -m "perf: build write-path _etag from ContentVersion, drop hydrate-materialize readback"
```

---

## Phase 4 — `If-Match` precondition: compare composed tags

### Task 4.1: Replace materialize+hash with composed expected tag

**Files:**
- Modify: `src/dms/backend/EdFi.DataManagementService.Backend/RelationalCurrentEtagPreconditionChecker.cs`
- Modify: `RelationalCurrentEtagPreconditionCheckRequest` — add the request `VariantKey` (from the inbound request's representation context). Note this record uses a **hand-written constructor** (not a positional record), so update the constructor, the `VariantKey` property, and both `CheckAsync` construction sites; the compiler will surface each.
- Test: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/**/RelationalCurrentEtagPreconditionCheckerTests.cs`

- [ ] **Step 1: Update/replace the checker tests.** Assert that:
  - a matching `If-Match` (`"{currentContentVersion}-{requestVariantKey}"`) → `IsMatch == true`;
  - a stale `ContentVersion` → `IsMatch == false` → 412;
  - a **cross-variant** tag (right `ContentVersion`, different `variantKey`) → `IsMatch == false` (strict full-tag comparison — the chosen behavior);
  - the checker does **not** call `_readMaterializer.Materialize` (spy/mock) and does **not** hash.

- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 3: Implement.** Two options depending on Task 3.1/consumer analysis:
  - **Minimal (safe default):** keep the existing `_currentStateLoader.LoadAsync` (its `currentState` may be consumed downstream — verify), but replace lines 130-139:
```csharp
// remove: Materialize(...) + RelationalApiMetadataFormatter.FormatEtag(...)
var currentEtag = _etagComposer.Compose(
    currentState.DocumentMetadata.ContentVersion, request.VariantKey);
```
  - **Optimized (if `currentState` is unused downstream):** drop the load entirely and use the `ContentVersion` already returned by the row lock. `TryLockDocumentAsync` (lines 153-166) currently discards its scalar; return it instead:
```csharp
private static async Task<long?> TryLockDocumentAsync(...)
{
    await using var command = writeSession.CreateCommand(
        RelationalDocumentLockCommandBuilder.BuildContentVersionCommand(dialect, documentId));
    var scalarResult = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return scalarResult is null or DBNull ? null : Convert.ToInt64(scalarResult);
}
// caller: var lockedVersion = await TryLockDocumentAsync(...); if (lockedVersion is null) return null;
// var currentEtag = _etagComposer.Compose(lockedVersion.Value, request.VariantKey);
```
  Keep the strict comparison at line 149: `string.Equals(request.Precondition.Value, currentEtag, StringComparison.Ordinal)`. (For the lenient alternative, compare only the `ContentVersion` component instead.)
  Inject `IEtagComposer` into the checker's constructor (line 52-55).

- [ ] **Step 4: Run — expect PASS.**
```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit --filter "FullyQualifiedName~PreconditionChecker"
```

### Task 4.2: Descriptor + delete precondition and write-request wiring

**Files:**
- Modify: the write executor path that builds `RelationalCurrentEtagPreconditionCheckRequest` (supply the request `VariantKey`), the delete precondition path (`IRelationalDeleteEtagPreconditionChecker`), and any descriptor-specific etag path (`RelationalApiMetadataFormatter.FormatEtag(ExtractedDescriptorBody)` becomes unused).

- [ ] **Step 1:** Wire the inbound-request `VariantKey` into every `RelationalCurrentEtagPreconditionCheckRequest`/delete check construction, built from the write request's profile/format/link context.
- [ ] **Step 2: Build; run** precondition + delete + cascade integration tests (both dialects).
```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration --filter "FullyQualifiedName~IfMatch"
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration --filter "FullyQualifiedName~IfMatch"
```
- [ ] **Step 3: Commit Phase 4.**
```bash
git add src/dms/backend/EdFi.DataManagementService.Backend
git commit -m "feat: If-Match compares composed ContentVersion+variantKey tags (no materialize/hash)"
```

---

## Phase 5 — HTTP surface: quote the strong entity-tag

### Task 5.1: Quote `ETag`, parse `If-Match` quote-tolerantly

**Files:**
- Read first: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs` (where `Response.Headers.ETag` is set), `src/dms/core/EdFi.DataManagementService.Core/Backend/WritePreconditionFactory.cs` (line 18, reads `If-Match`), and the handlers that put etag into `FrontendResponse.Headers["etag"]` (`UpsertHandler.cs`, `UpdateByIdHandler.cs`).
- Test: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/**/AspNetCoreFrontendResponseHeaderTests.cs`

- [ ] **Step 1: Update the header test** to expect a **quoted** `ETag` value (`"5-a1b2c3d4.j._.l"`), and that the `_etag` **body** field remains **unquoted**.
- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 3: Implement.**
  - When writing the `ETag` response header, wrap with `EtagValue.ToHeaderValue(...)`. Keep the `_etag` JSON body field unquoted (already composed unquoted in Phase 2/3).
  - In `WritePreconditionFactory.Create`, normalize the inbound `If-Match` via `EtagValue.TryParseHeaderValue` so the stored `WritePrecondition.IfMatch.Value` is the unquoted opaque tag that the checker compares against (Phase 4 compares unquoted values). Reject weak (`W/`) tags per the helper.
- [ ] **Step 4: Run — expect PASS.**
```bash
dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit --filter "FullyQualifiedName~Header"
```
- [ ] **Step 5: Commit.**
```bash
git add src/dms/frontend src/dms/core/EdFi.DataManagementService.Core/Backend/WritePreconditionFactory.cs
git commit -m "feat: serve ETag as quoted strong validator; accept quoted If-Match"
```

---

## Phase 6 — Retire dead hash code

### Task 6.1: Remove unused hashing paths

**Files:**
- `src/dms/core/EdFi.DataManagementService.Core/Utilities/ResourceEtagFormatter.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalApiMetadataFormatter.cs`
- `RelationalCommittedRepresentationReader.cs` (if now unused per Task 3.3)
- `src/dms/core/EdFi.DataManagementService.Core/Utilities/CanonicalJsonSerializer.cs` (check other callers first)

- [ ] **Step 1: Grep for remaining references** to `ResourceEtagFormatter`, `RelationalApiMetadataFormatter.FormatEtag`, `CanonicalJsonSerializer`, `RelationalCommittedRepresentationReader`.
```bash
git grep -n "ResourceEtagFormatter\|RelationalApiMetadataFormatter\|CanonicalJsonSerializer\|CommittedRepresentationReader" -- src/dms
```
- [ ] **Step 2: Remove** each symbol that has no remaining callers, plus its DI registration and its now-obsolete unit tests. **Keep** `CanonicalJsonSerializer` if any non-etag caller remains (e.g. no-op storage comparison) — verify via the grep; do not remove shared utilities used elsewhere.
- [ ] **Step 3: Build the solution.** `dotnet build src/dms/EdFi.DataManagementService.sln`
- [ ] **Step 4: Commit.**
```bash
git add -A src/dms
git commit -m "refactor: remove content-hash etag formatter and unused readback"
```

---

## Phase 7 — Tests & documentation

### Task 7.1: Rewrite hash-bound unit tests

**Files:**
- `DescriptorWriteHandlerResponseEtagTests.cs`, `RelationalWriteSeamTests.cs`, `UpdateByIdHandlerTests.cs`, `UpsertHandlerTests.cs`, and any test flagged in Task 0.2.

- [ ] **Step 1:** Replace exact-hash assertions and "non-canonical order hashes identically" cases with composed-form assertions (`"{ContentVersion}-{variantKey}"`), monotonic-bump-on-update, and no-op-returns-same-etag semantics. Delete tests that only asserted hash canonicalization behavior.
- [ ] **Step 2: Run** the affected unit projects green.
- [ ] **Step 3: Commit.**

### Task 7.2: Update integration tests

**Files:**
- `*ProfileIfMatchEtagTests.cs` (pg + mssql), `*IfMatchCascadeReferentialIdentityTests.cs` (pg + mssql).

- [ ] **Step 1:** Update etag reads to the composed form; add a **cross-profile `If-Match` → 412** case (this is the deliberate, ADR-driven behavior change — different profiles now yield different `_etag`). Keep the cascade regression from Task 0.1.
- [ ] **Step 2: Run** both integration projects green (containers required).
- [ ] **Step 3: Commit.**

### Task 7.3: Update E2E `etag.feature`

**Files:**
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/**/etag.feature`

- [ ] **Step 1:** Review the 6 scenarios. Presence/412/If-Match-ignores-body scenarios should still pass unchanged. If any scenario asserts a hash-shaped value, update to the composed shape. Optionally add a scenario asserting the quoted `ETag` header.
- [ ] **Step 2: Run** E2E green (containers required). **Step 3: Commit.**

### Task 7.4: Apply the staged design-doc edits

**Files:**
- `reference/design/backend-redesign/design-docs/update-tracking.md`
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- Source of wording: `reference/adr-etag-from-content-version-designdoc-edits.md`

- [ ] **Step 1:** Apply the paste-ready replacements from the companion file (sections 1-3), **re-verifying** each "current" quote still matches (the companion's line numbers are as of 2026-06-30). This includes the deliberate reversal: `_etag` becomes profile/link/format-**sensitive**. Do not apply until the ADR is accepted (per the companion's own gate).
- [ ] **Step 2:** Update the ADR `Status` to `Accepted` (with date/deciders) once the team signs off.
- [ ] **Step 3: Commit.**
```bash
git add reference/design/backend-redesign/design-docs reference/adr-etag-from-content-version.md
git commit -m "docs: update etag derivation to ContentVersion + variantKey per accepted ADR"
```

---

## Phase 8 — Verification, review, PR

- [ ] **Step 1: Full solution build + all unit tests.**
```bash
dotnet build src/dms/EdFi.DataManagementService.sln
dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit
dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit
```
- [ ] **Step 2: Both-dialect integration + E2E** (containers).
- [ ] **Step 3: Manual smoke** — verify a POST returns a quoted `ETag`, a follow-up conditional GET with `If-None-Match` behaves, and a stale `If-Match` PUT returns 412.
- [ ] **Step 4: superpowers:code-reviewer** against this plan + the ADR.
- [ ] **Step 5:** Open PR from `etag-content-version`. In the description, disclose AI assistance, link the ADR, and call out the two client-visible changes (composed opaque etag; quoted header; cross-variant 412) and the profile-index infrastructure added beyond the ADR's assumption. Request human review before merge.

## Risks & watch-items

- **Profile-index gap (Task 1.3):** the ADR assumed it existed; this plan builds it. If the team prefers the opaque-hash `profileCode`, swap one method.
- **`currentState` coupling in the precondition checker (Task 4.1):** verify downstream consumers before dropping the load; default to the minimal change if in doubt.
- **`RETURNING`/`OUTPUT` dialect parity (Task 3.2):** confirm both PostgreSQL and SQL Server return the stamps identically-typed.
- **Cross-variant 412 (Task 7.2):** a genuine behavior change from strict comparison; ensure it is intended before shipping (ADR §"If-Match comparison").
- **Cache freshness (companion §2d):** `dms.DocumentCache`, if enabled, must key freshness on `ContentVersion` alone and compose `_etag` per request — do not cache a materialized `_etag`.
