// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// One loaded DMS-1391 baseline run: the frozen schema 1.3.0 artifacts the cross-run gates
/// compare against.
/// </summary>
public sealed record PerfBaselineRunArtifacts(
    PerfRunManifest Manifest,
    PerfResultsDocument Results,
    string RunDirectory
);

/// <summary>
/// One loaded final-gate run: schema 2.0.0 artifacts.
/// </summary>
public sealed record PerfFinalGateRunArtifacts(
    PerfFinalGateRunManifest Manifest,
    PerfFinalGateResultsDocument Results,
    string RunDirectory
);

/// <summary>
/// Everything the evaluator needs for one provider: the pre-change traditional baseline, the
/// shared-primary-load final-gate run, and the descriptor final-gate run.
/// </summary>
public sealed record PerfFinalGateProviderEvidence(
    PerfBaselineRunArtifacts Baseline,
    PerfFinalGateRunArtifacts Primary,
    PerfFinalGateRunArtifacts Descriptors
);

/// <summary>
/// Loads and structurally validates run artifacts from disk, including that every results
/// row's plan evidence file actually exists in the run directory — a results.json referencing
/// missing plan evidence is not loadable evidence.
/// </summary>
public static class PerfFinalGateEvidenceLoader
{
    public static PerfBaselineRunArtifacts LoadBaseline(string runDirectory)
    {
        PerfRunManifest manifest = PerfArtifactJson.Deserialize<PerfRunManifest>(
            File.ReadAllText(Path.Combine(runDirectory, "run-manifest.json"))
        );
        PerfResultsDocument results = PerfArtifactJson.Deserialize<PerfResultsDocument>(
            File.ReadAllText(Path.Combine(runDirectory, "results.json"))
        );
        PerfArtifactValidator.EnsureValid(manifest, results);
        EnsurePlanFilesExist(runDirectory, results.Results.Select(row => row.PlanFile));
        return new PerfBaselineRunArtifacts(manifest, results, runDirectory);
    }

    public static PerfFinalGateRunArtifacts LoadFinalGate(string runDirectory)
    {
        PerfFinalGateRunManifest manifest = PerfArtifactJson.Deserialize<PerfFinalGateRunManifest>(
            File.ReadAllText(Path.Combine(runDirectory, "run-manifest.json"))
        );
        PerfFinalGateResultsDocument results = PerfArtifactJson.Deserialize<PerfFinalGateResultsDocument>(
            File.ReadAllText(Path.Combine(runDirectory, "results.json"))
        );
        PerfFinalGateArtifactValidator.EnsureValid(manifest, results);
        EnsurePlanFilesExist(runDirectory, results.Results.Select(row => row.PlanFile));
        return new PerfFinalGateRunArtifacts(manifest, results, runDirectory);
    }

    private static void EnsurePlanFilesExist(string runDirectory, IEnumerable<string> planFiles)
    {
        List<string> missing =
        [
            .. planFiles.Where(planFile =>
                !File.Exists(Path.Combine(runDirectory, planFile.Replace('/', Path.DirectorySeparatorChar)))
            ),
        ];
        if (missing.Count > 0)
        {
            throw new PerfArtifactValidationException([
                .. missing.Select(planFile => $"plan evidence '{planFile}' is missing from {runDirectory}."),
            ]);
        }
    }
}
