// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// The outcome of one gate check. Inconclusive is a first-class status: a gate whose evidence
/// is missing, stale, or measured on a non-comparable environment must say so rather than
/// pass or fail on numbers it cannot trust.
/// </summary>
public enum PerfGateStatus
{
    Pass,
    Fail,
    Inconclusive,
}

/// <summary>
/// One evaluated gate: which rule, over which provider's evidence, what happened, the ratio
/// or reason detail, and the exact rows the decision used.
/// </summary>
public sealed record PerfGateOutcome(
    string GateId,
    string Provider,
    string Description,
    PerfGateStatus Status,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> EvidenceRows
);

/// <summary>
/// Identity of one run the evaluation consumed, for the report's provenance section.
/// </summary>
public sealed record PerfEvaluatedRun(
    string Provider,
    string Kind,
    string RunId,
    string RunDirectory,
    string FixtureId,
    string RunnerCommit,
    string SubjectCommit,
    string MachineFingerprint,
    string ImageDigest
);

/// <summary>
/// The complete evaluation: every gate outcome plus the runs it consumed. The overall status
/// is Fail when any gate failed, otherwise Inconclusive when any gate could not decide,
/// otherwise Pass.
/// </summary>
public sealed record PerfFinalGateEvaluation(
    string SchemaVersion,
    IReadOnlyList<PerfEvaluatedRun> Runs,
    IReadOnlyList<PerfGateOutcome> Gates
)
{
    public PerfGateStatus OverallStatus
    {
        get
        {
            if (Gates.Any(gate => gate.Status == PerfGateStatus.Fail))
            {
                return PerfGateStatus.Fail;
            }

            return Gates.Any(gate => gate.Status == PerfGateStatus.Inconclusive)
                ? PerfGateStatus.Inconclusive
                : PerfGateStatus.Pass;
        }
    }

    public static PerfFinalGateEvaluation Create(
        IReadOnlyList<PerfEvaluatedRun> runs,
        IReadOnlyList<PerfGateOutcome> gates
    ) => new(PerfFinalGateArtifactSchema.Version, runs, gates);
}
