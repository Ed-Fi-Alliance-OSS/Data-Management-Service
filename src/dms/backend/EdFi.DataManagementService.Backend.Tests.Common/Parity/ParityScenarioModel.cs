// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.Tests.Common.Parity;

/// <summary>
/// Which of the three cross-engine parity coverage layers a scenario belongs to.
/// The two original no-profile matrix scenarios (NoProfileWriteBehavior,
/// FullSurfaceCollectionReorder) belong to <see cref="NoProfile"/>, not <see cref="Profile"/>.
/// </summary>
public enum ParityLayer
{
    /// <summary>DMS-1022 API-level HTTP CRUD/query/profile scenarios.</summary>
    Api,

    /// <summary>DMS-1124 profile-aware relational-write scenarios.</summary>
    Profile,

    /// <summary>DMS-984 no-profile relational-write scenarios.</summary>
    NoProfile,
}

/// <summary>Per-engine coverage state for a parity scenario.</summary>
public enum EngineCoverage
{
    /// <summary>An executing test exercises this engine for this scenario.</summary>
    Covered,

    /// <summary>No test exercises this engine and one is owed; the scenario's per-engine owner names the ticket.</summary>
    Gap,

    /// <summary>
    /// This engine has no test for this exact row, but the mechanic is contractually covered by the
    /// canonical same-boundary scenario the row defers to (used by supporting-smoke breadth rows). Not
    /// an owned gap.
    /// </summary>
    Mapped,

    /// <summary>The behavior is provider-independent, so cross-engine execution does not apply.</summary>
    NotApplicable,
}

/// <summary>
/// Coverage classification. Reuses the DMS-1124 Slice-7 vocabulary (<see cref="Both"/>,
/// <see cref="FixInSlice"/>, <see cref="Na"/>) and adds the DMS-1023 classifications.
/// Classification is a separate field from the scenario's per-engine gap owners; a known gap is
/// never encoded as a combined free-form string.
/// </summary>
public enum ParityClassification
{
    /// <summary>Parity demonstrated on both PostgreSQL and SQL Server.</summary>
    Both,

    /// <summary>A gap the owning DMS-1124 slice itself closed (historical; none remain).</summary>
    FixInSlice,

    /// <summary>Provider-independent behavior validated at the unit level; no cross-engine parity applies.</summary>
    Na,

    /// <summary>PostgreSQL-only today; a SQL Server twin is owed by the scenario's per-engine owner.</summary>
    KnownGap,

    /// <summary>
    /// Real-world breadth smoke whose mechanic is contractually covered by a canonical scenario at
    /// the same production boundary (see <c>CoveredByScenarioId</c>); the other engine is Mapped.
    /// </summary>
    SupportingSmoke,

    /// <summary>Behavior that is intentionally provider-specific and not a cross-engine parity obligation.</summary>
    ProviderSpecific,
}

/// <summary>
/// How a scenario's effective reusable assertion/helper entry point is recorded. Every catalog row must
/// resolve to exactly one of these modes.
/// </summary>
public enum EntryPointKind
{
    /// <summary>The row names its own provider-neutral shared contract in <see cref="ParityScenario.SharedEntryPoint"/>.</summary>
    Direct,

    /// <summary>
    /// The row reuses the shared contract of the scenario it explicitly defers to through
    /// <see cref="ParityScenario.CoveredByScenarioId"/> (a supporting-smoke deferral), and only when that
    /// scenario pins the same production <see cref="ParityScenario.Boundary"/>. Belonging to a canonical family
    /// (a shared id prefix, via <c>ParityScenarioCatalog.CanonicalIdOf</c>) never inherits a contract by itself:
    /// sharing a boundary with the family does not imply running the family's assertion helpers, so an ordinary
    /// variant names its own <see cref="ParityScenario.SharedEntryPoint"/> instead.
    /// </summary>
    Inherited,

    /// <summary>
    /// No provider-neutral shared contract applies; the effective fixture/assertion entry points are the row's
    /// existing per-engine (<see cref="ParityScenario.PgsqlLocations"/>/<see cref="ParityScenario.MssqlLocations"/>)
    /// or <see cref="ParityScenario.UnitLocations"/> test locations, justified by
    /// <see cref="ParityScenario.ProviderSpecificEntryPointRationale"/>.
    /// </summary>
    ProviderSpecific,
}

/// <summary>
/// The behavioral mechanic (production seam) whose externally visible and authoritative-storage behavior a
/// scenario's assertions pin. This is deliberately <b>not</b> the invocation entry point: every no-profile and
/// profile integration row is invoked through <c>RelationalDocumentStoreRepository</c>, every API row through the
/// HTTP pipeline, and every Na row through the merge synthesizer, so the invocation boundary carries no
/// discriminating signal. Recording the mechanic — not the entry point — is what makes same-mechanic
/// supporting-smoke deferral meaningful. Each value belongs to exactly one <see cref="ParityLayer"/>: the
/// layer↔mechanic partition below is enforced by a structural invariant, so a no-profile scenario can never
/// declare a profile mechanic (or vice versa).
/// </summary>
public enum ProductionBoundary
{
    /// <summary>End-to-end behavior of the DMS ASP.NET Core HTTP request pipeline (API layer).</summary>
    HttpPipeline,

    /// <summary>
    /// No-profile persister control path (<c>RelationalWriteNoProfilePersister</c>): full-surface create,
    /// POST-as-update in place, and transactional rollback outcomes.
    /// </summary>
    NoProfilePersister,

    /// <summary>
    /// No-profile merge row synthesis (<c>RelationalWriteNoProfileMerge</c>): omitted-scope clearing/deletion,
    /// collection reorder, and stable-CollectionItemId reuse.
    /// </summary>
    NoProfileMerge,

    /// <summary>Guarded no-op compare (<c>RelationalWriteGuardedNoOp</c>) with the freshness/current-state seams.</summary>
    GuardedNoOp,

    /// <summary>Immutable-identity rejection (<c>RelationalWriteIdentityStability</c>).</summary>
    IdentityStability,

    /// <summary>Collection batch partitioning (<c>WritePlanBatchSqlEmitter</c> / <c>PlanWriteBatchingConventions</c>).</summary>
    BatchSqlEmitter,

    /// <summary>Runtime reference-identity column population/repopulation/cascade.</summary>
    ReferenceIdentityRuntime,

    /// <summary>Key-unification conflict validation with atomic rollback.</summary>
    KeyUnificationValidation,

    /// <summary>Relational GET-by-id read-back / ETag / If-Match / readable-profile projection.</summary>
    RelationalReadback,

    /// <summary>Profile-aware persist-executor merge (profile layer).</summary>
    ProfilePersistExecutor,

    /// <summary>
    /// Provider-independent profile merge synthesizer/planner, exercised by unit tests with no provider persist
    /// executor. Used by rows whose only coverage is a synthesizer unit test.
    /// </summary>
    ProfileMergeSynthesizer,
}

/// <summary>
/// A test entry point: the source file, the NUnit fixture class, and the test method(s) that
/// exercise the scenario. Names are recorded independently per engine because the provider suites
/// do not mechanically mirror each other's names.
/// </summary>
public sealed record ScenarioLocation(string File, string Fixture, ImmutableArray<string> Methods);

/// <summary>
/// The resolved effective reusable assertion/helper entry point for a parity row. For
/// <see cref="EntryPointKind.Direct"/> and <see cref="EntryPointKind.Inherited"/>, <see cref="SharedValue"/>
/// names the shared contract (and <see cref="InheritedFromScenarioId"/> its source scenario id when inherited).
/// For <see cref="EntryPointKind.ProviderSpecific"/>, the applicable per-engine and/or unit location collections
/// carry the effective entry points (a Both row exposes both engines; an Na row exposes its unit locations).
/// </summary>
public sealed record EffectiveEntryPoint(
    EntryPointKind Kind,
    string? SharedValue,
    string? InheritedFromScenarioId,
    ImmutableArray<ScenarioLocation> PgsqlLocations,
    ImmutableArray<ScenarioLocation> MssqlLocations,
    ImmutableArray<ScenarioLocation> UnitLocations
);

/// <summary>An intentional dialect difference and the rationale that justifies it.</summary>
public sealed record DialectDifference(string Description, string Rationale);

/// <summary>
/// One row of the canonical cross-engine parity catalog. The C# catalog is the authoritative
/// source of truth; the design document is the narrative/index. A single scenario may name more
/// than one fixture per engine, so the locations are collections.
/// </summary>
public sealed record ParityScenario
{
    /// <summary>Stable identifier: a canonical id, or <c>&lt;CanonicalId&gt;/&lt;PascalCaseVariant&gt;</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Coverage layer this scenario belongs to.</summary>
    public required ParityLayer Layer { get; init; }

    /// <summary>Short statement of the externally visible + authoritative storage behavior asserted.</summary>
    public required string BehavioralContract { get; init; }

    /// <summary>
    /// The row's <b>direct</b> provider-neutral shared contract entry point: a Backend.Tests.Common class
    /// (or an <c>A + B</c> composite of them), or for an API row a <c>Type.Method</c> resolvable in the API
    /// test assembly. Empty when the effective entry point is <b>inherited</b> (from the covered-by scenario) or
    /// <b>provider-specific</b> (the per-engine/unit locations plus
    /// <see cref="ProviderSpecificEntryPointRationale"/>). Resolve the effective entry point and its
    /// <see cref="EntryPointKind"/> through <c>ParityEntryPointResolution.ResolveEffectiveEntryPoint</c>.
    /// </summary>
    public string SharedEntryPoint { get; init; } = "";

    /// <summary>
    /// Rationale required when a row's effective entry point is <see cref="EntryPointKind.ProviderSpecific"/> —
    /// i.e., it has no direct or inherited shared contract and resolves through its existing per-engine
    /// (<see cref="PgsqlLocations"/>/<see cref="MssqlLocations"/>) or <see cref="UnitLocations"/> entry points.
    /// Must be empty for Direct/Inherited rows so the resolution mode stays unambiguous.
    /// </summary>
    public string? ProviderSpecificEntryPointRationale { get; init; }

    /// <summary>
    /// The behavioral mechanic (production seam) whose behavior this scenario's assertions pin — not the
    /// invocation entry point. Must be a mechanic valid for this row's <see cref="Layer"/>.
    /// </summary>
    public required ProductionBoundary Boundary { get; init; }

    /// <summary>Optional specific class/method on the mechanic (e.g. RelationalWriteIdentityStability.TryBuildFailureResult).</summary>
    public string? BoundaryDetail { get; init; }

    /// <summary>PostgreSQL test locations (one per fixture); empty when PostgreSQL has none.</summary>
    public ImmutableArray<ScenarioLocation> PgsqlLocations { get; init; } = [];

    /// <summary>SQL Server test locations (one per fixture); empty when SQL Server has none.</summary>
    public ImmutableArray<ScenarioLocation> MssqlLocations { get; init; } = [];

    /// <summary>Provider-independent unit test locations for an Na row; empty otherwise.</summary>
    public ImmutableArray<ScenarioLocation> UnitLocations { get; init; } = [];

    /// <summary>PostgreSQL coverage state.</summary>
    public required EngineCoverage PgsqlCoverage { get; init; }

    /// <summary>SQL Server coverage state.</summary>
    public required EngineCoverage MssqlCoverage { get; init; }

    /// <summary>An intentional dialect difference, or null when the engines are behaviorally identical.</summary>
    public DialectDifference? DialectDifference { get; init; }

    /// <summary>Coverage classification.</summary>
    public required ParityClassification Classification { get; init; }

    /// <summary>Owning ticket for the PostgreSQL gap; required iff PostgreSQL coverage is Gap.</summary>
    public string? PgsqlGapOwner { get; init; }

    /// <summary>Owning ticket for the SQL Server gap; required iff SQL Server coverage is Gap.</summary>
    public string? MssqlGapOwner { get; init; }

    /// <summary>
    /// For a SupportingSmoke row, the canonical (base) same-boundary scenario id whose mechanic
    /// contractually covers this breadth smoke. Must equal an exact canonical no-profile id.
    /// </summary>
    public string? CoveredByScenarioId { get; init; }

    /// <summary>Free-form traceability notes (trace labels, partial-coverage / additional-mechanic notes).</summary>
    public string? Notes { get; init; }
}
