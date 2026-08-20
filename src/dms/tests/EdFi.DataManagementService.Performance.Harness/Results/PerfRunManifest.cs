// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Identity of one harness run: which run, when (ISO-8601 UTC), and against which provider.
/// </summary>
public sealed record PerfRunIdentity(string RunId, string CapturedAtUtc, string Provider);

/// <summary>
/// The two distinct commit roles: the commit whose harness sources ran, and the commit whose
/// DMS behavior was measured. On a baseline capture these differ by design. The dirty-path
/// list records exactly what the overlay added to the subject worktree.
/// </summary>
public sealed record PerfCommitIdentity(
    string RunnerCommit,
    string SubjectCommit,
    IReadOnlyList<string> WorktreeDirtyPaths
);

/// <summary>
/// The fixture the run measured against, echoed from configuration for self-contained
/// artifacts.
/// </summary>
public sealed record PerfManifestFixture(string FixtureId, long RowCount, long DeepOffset);

/// <summary>
/// Iteration counts and the order scenario cells actually executed in, which matters for
/// provider plan-cache effects.
/// </summary>
public sealed record PerfIterationPlan(
    int WarmupIterations,
    int MeasuredIterations,
    IReadOnlyList<string> ScenarioExecutionOrder
);

/// <summary>
/// The versioned root of run-manifest.json.
/// </summary>
public sealed record PerfRunManifest(
    string SchemaVersion,
    PerfRunIdentity Run,
    PerfCommitIdentity Commits,
    PerfManifestFixture Fixture,
    PerfIterationPlan Iterations,
    PerfEnvironmentIdentity Environment
)
{
    public static PerfRunManifest Create(
        PerfRunIdentity run,
        PerfCommitIdentity commits,
        PerfManifestFixture fixture,
        PerfIterationPlan iterations,
        PerfEnvironmentIdentity environment
    ) => new(PerfArtifactSchema.Version, run, commits, fixture, iterations, environment);
}
