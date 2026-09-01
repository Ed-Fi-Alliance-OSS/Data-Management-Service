// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// The fixture a final-gate run measured against. DeepOffset applies only to the primary run,
/// whose traditional rerun cells resolve it; the descriptor run carries null.
/// </summary>
public sealed record PerfFinalGateManifestFixture(string FixtureId, long RowCount, long? DeepOffset);

/// <summary>
/// One entry of the primary run's phase log: which mutation was applied to the shared load,
/// after which measurement phase, with the analytic facts the verification held it to. The
/// pristine phase has no entry — its whole point is that nothing was applied.
/// </summary>
public sealed record PerfFinalGatePhaseLogEntry(
    string Phase,
    string Description,
    IReadOnlyList<PerfSetting> Facts
);

/// <summary>
/// One cell as it actually executed, in order. The full identity is recorded because plan
/// caching means whichever cell's parameter values run first can shape later cells.
/// </summary>
public sealed record PerfFinalGateExecutedCell(
    string ScenarioId,
    string Family,
    string Variant,
    string? Phase,
    int? PageSize,
    long? Offset,
    string? CursorRange,
    long? StartAnchorDocumentId,
    int? RequestedPartitionNumber
);

/// <summary>
/// Iteration counts and the exact cell execution order of a final-gate run.
/// </summary>
public sealed record PerfFinalGateIterationPlan(
    int WarmupIterations,
    int MeasuredIterations,
    IReadOnlyList<PerfFinalGateExecutedCell> CellExecutionOrder
);

/// <summary>
/// The versioned root of a final-gate run-manifest.json.
/// </summary>
public sealed record PerfFinalGateRunManifest(
    string SchemaVersion,
    string RunKind,
    PerfRunIdentity Run,
    PerfCommitIdentity Commits,
    PerfFinalGateManifestFixture Fixture,
    IReadOnlyList<PerfFinalGatePhaseLogEntry> PhaseLog,
    PerfFinalGateIterationPlan Iterations,
    PerfEnvironmentIdentity Environment
)
{
    public static PerfFinalGateRunManifest Create(
        string runKind,
        PerfRunIdentity run,
        PerfCommitIdentity commits,
        PerfFinalGateManifestFixture fixture,
        IReadOnlyList<PerfFinalGatePhaseLogEntry> phaseLog,
        PerfFinalGateIterationPlan iterations,
        PerfEnvironmentIdentity environment
    ) =>
        new(
            PerfFinalGateArtifactSchema.Version,
            runKind,
            run,
            commits,
            fixture,
            phaseLog,
            iterations,
            environment
        );
}
