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

    /// <summary>No test exercises this engine yet; a twin is owed (see the scenario's gap owner).</summary>
    Gap,

    /// <summary>The behavior is provider-independent, so cross-engine execution does not apply.</summary>
    NotApplicable,
}

/// <summary>
/// Coverage classification. Reuses the DMS-1124 Slice-7 vocabulary (<see cref="Both"/>,
/// <see cref="FixInSlice"/>, <see cref="Na"/>) and adds the DMS-1023 classifications.
/// Classification is a separate field from the scenario's gap owner; a known gap is never
/// encoded as a combined free-form string.
/// </summary>
public enum ParityClassification
{
    /// <summary>Parity demonstrated on both PostgreSQL and SQL Server.</summary>
    Both,

    /// <summary>A gap the owning DMS-1124 slice itself closed (historical; none remain).</summary>
    FixInSlice,

    /// <summary>Provider-independent behavior validated at the unit level; no cross-engine parity applies.</summary>
    Na,

    /// <summary>PostgreSQL-only today; a SQL Server twin is owed by the scenario's <c>GapOwner</c>.</summary>
    KnownGap,

    /// <summary>
    /// Real-world breadth smoke whose mechanic is contractually covered by another canonical
    /// scenario at the same production boundary (see <c>CoveredByScenarioId</c>).
    /// </summary>
    SupportingSmoke,

    /// <summary>Behavior that is intentionally provider-specific and not a cross-engine parity obligation.</summary>
    ProviderSpecific,
}

/// <summary>
/// The production boundary a parity scenario exercises. A no-profile scenario must never
/// declare <see cref="ProfilePersistExecutor"/>; a profile scenario declares it. This makes
/// same-boundary supporting-smoke deferral mechanically checkable.
/// </summary>
public enum ProductionBoundary
{
    /// <summary>The DMS ASP.NET Core HTTP request pipeline (API layer).</summary>
    HttpPipeline,

    /// <summary>RelationalDocumentStoreRepository → RelationalWriteNoProfilePersister.</summary>
    NoProfilePersister,

    /// <summary>RelationalWriteNoProfileMerge row synthesis.</summary>
    NoProfileMerge,

    /// <summary>RelationalWriteGuardedNoOp plus freshness/current-state seams.</summary>
    GuardedNoOp,

    /// <summary>RelationalWriteIdentityStability immutable-identity rejection.</summary>
    IdentityStability,

    /// <summary>WritePlanBatchSqlEmitter / PlanWriteBatchingConventions batching.</summary>
    BatchSqlEmitter,

    /// <summary>Runtime reference-identity column population/cascade.</summary>
    ReferenceIdentityRuntime,

    /// <summary>Key-unification conflict validation with atomic rollback.</summary>
    KeyUnificationValidation,

    /// <summary>The profile-aware persist executor (profile layer only).</summary>
    ProfilePersistExecutor,

    /// <summary>
    /// The provider-independent profile merge synthesizer/planner, exercised by unit tests with
    /// no provider persist executor. Used by rows whose only coverage is a synthesizer unit test.
    /// </summary>
    ProfileMergeSynthesizer,
}

/// <summary>
/// A test entry point recorded per engine: the source file, the NUnit fixture class, and the
/// test method(s) that exercise the scenario on that engine. Names are recorded independently
/// per engine because the provider suites do not mechanically mirror each other's names.
/// </summary>
public sealed record ScenarioLocation(string File, string Fixture, ImmutableArray<string> Methods);

/// <summary>An intentional dialect difference and the rationale that justifies it.</summary>
public sealed record DialectDifference(string Description, string Rationale);

/// <summary>
/// One row of the canonical cross-engine parity catalog. The C# catalog is the authoritative
/// source of truth; the design document is the narrative/index.
/// </summary>
public sealed record ParityScenario
{
    /// <summary>Stable identifier: a canonical id, or <c>&lt;CanonicalId&gt;/&lt;PascalCaseVariant&gt;</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Coverage layer this scenario belongs to.</summary>
    public required ParityLayer Layer { get; init; }

    /// <summary>Short statement of the externally visible + authoritative storage behavior asserted.</summary>
    public required string BehavioralContract { get; init; }

    /// <summary>The provider-neutral shared contract entry point in Backend.Tests.Common, or empty when mapped-only.</summary>
    public string SharedEntryPoint { get; init; } = "";

    /// <summary>The production boundary exercised.</summary>
    public required ProductionBoundary Boundary { get; init; }

    /// <summary>Optional specific class/method on the boundary (e.g. RelationalWriteIdentityStability.TryBuildFailureResult).</summary>
    public string? BoundaryDetail { get; init; }

    /// <summary>PostgreSQL test location, or null when none applies.</summary>
    public ScenarioLocation? Pgsql { get; init; }

    /// <summary>SQL Server test location, or null when none applies (e.g. a known gap).</summary>
    public ScenarioLocation? Mssql { get; init; }

    /// <summary>Provider-independent unit test location for an Na row; null otherwise.</summary>
    public ScenarioLocation? Unit { get; init; }

    /// <summary>PostgreSQL coverage state.</summary>
    public required EngineCoverage PgsqlCoverage { get; init; }

    /// <summary>SQL Server coverage state.</summary>
    public required EngineCoverage MssqlCoverage { get; init; }

    /// <summary>An intentional dialect difference, or null when the engines are behaviorally identical.</summary>
    public DialectDifference? DialectDifference { get; init; }

    /// <summary>Coverage classification.</summary>
    public required ParityClassification Classification { get; init; }

    /// <summary>
    /// Owning ticket for the PostgreSQL gap on a KnownGap row; required when PostgreSQL is a gap and
    /// forbidden otherwise.
    /// </summary>
    public string? PgsqlGapOwner { get; init; }

    /// <summary>
    /// Owning ticket for the SQL Server gap on a KnownGap row; required when SQL Server is a gap and
    /// forbidden otherwise.
    /// </summary>
    public string? MssqlGapOwner { get; init; }

    /// <summary>
    /// For a SupportingSmoke row, the canonical same-boundary scenario id whose mechanic
    /// contractually covers this breadth smoke. The reverse mapping is derived from these forward
    /// links, never stored separately.
    /// </summary>
    public string? CoveredByScenarioId { get; init; }

    /// <summary>Free-form traceability notes (trace labels, partial-coverage / additional-mechanic notes).</summary>
    public string? Notes { get; init; }
}
