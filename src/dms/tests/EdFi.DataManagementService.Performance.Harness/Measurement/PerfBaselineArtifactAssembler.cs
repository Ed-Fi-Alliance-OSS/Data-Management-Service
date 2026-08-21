// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// One measured cell together with its plan-replay evidence: the parsed database metrics, the
/// results row's plan file reference, and the raw evidence files to write beside it.
/// </summary>
public sealed record PerfCellEvidence(
    PerfMeasuredCell Cell,
    PerfDatabaseMetrics Metrics,
    string PlanFile,
    IReadOnlyList<PerfArtifactFile> PlanFiles
);

/// <summary>
/// The complete assembled artifact set for one provider run, ready for
/// <see cref="PerfRunArtifactWriter" />.
/// </summary>
public sealed record PerfAssembledRun(
    PerfRunManifest Manifest,
    PerfResultsDocument Results,
    PerfFixtureManifest FixtureManifest,
    IReadOnlyList<PerfArtifactFile> AuxiliaryFiles
);

/// <summary>
/// Assembles measured cells and plan evidence into the final artifact set. The page-selection
/// SQL and the hydration batch SQL must each be one distinct text across all six cells —
/// that textual stability is itself baseline evidence — so each is written once per provider
/// under sql/, alongside a small per-cell bound-parameters file.
/// </summary>
public static class PerfBaselineArtifactAssembler
{
    public static PerfAssembledRun Assemble(
        PerfProvider provider,
        PerfFixtureDefinition definition,
        long deepOffset,
        int warmupIterations,
        int measuredIterations,
        IReadOnlyList<PerfCellEvidence> evidence,
        PerfRunIdentity runIdentity,
        PerfCommitIdentity commits,
        PerfEnvironmentIdentity environment
    )
    {
        string providerName = PerfProviders.ArtifactName(provider);

        string pageSelectionSql = SingleDistinct(
            evidence.Select(item => item.Cell.PageSelection.PageDocumentIdSql),
            "page-selection SQL"
        );
        string hydrationBatchSql = SingleDistinct(
            evidence.Select(item => item.Cell.HydrationBatchSql),
            "hydration batch SQL"
        );

        List<PerfScenarioResult> rows =
        [
            .. evidence.Select(item => new PerfScenarioResult(
                providerName,
                item.Cell.ScenarioId,
                item.Cell.PageSize,
                item.Cell.Offset,
                item.Cell.ReturnedRows,
                item.Cell.CommandCountPerRequest,
                warmupIterations,
                measuredIterations,
                item.Cell.LatencyMs,
                item.Cell.DbCommandMs,
                item.Metrics,
                item.PlanFile,
                item.Cell.PageSelection.Sha256,
                commits.RunnerCommit,
                commits.SubjectCommit
            )),
        ];

        JsonArray boundParameters = [];
        foreach (PerfCellEvidence item in evidence)
        {
            boundParameters.Add(
                new JsonObject
                {
                    ["scenarioId"] = item.Cell.ScenarioId,
                    ["pageSize"] = item.Cell.PageSize,
                    ["offset"] = item.Cell.Offset,
                    ["limit"] = item.Cell.PageSize,
                }
            );
        }

        List<PerfArtifactFile> auxiliaryFiles =
        [
            new($"sql/{providerName}.page-selection.sql", pageSelectionSql),
            new($"sql/{providerName}.hydration-batch.sql", hydrationBatchSql),
            new(
                $"sql/{providerName}.bound-parameters.json",
                boundParameters.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
                )
            ),
            .. evidence.SelectMany(item => item.PlanFiles),
        ];

        PerfRunManifest manifest = PerfRunManifest.Create(
            runIdentity,
            commits,
            new PerfManifestFixture(definition.Kind.Id, definition.RowCount, deepOffset),
            new PerfIterationPlan(
                warmupIterations,
                measuredIterations,
                PerfScenarioExecutor.CellsInExecutionOrder(deepOffset)
            ),
            environment
        );

        return new PerfAssembledRun(
            manifest,
            PerfResultsDocument.Create(rows),
            PerfFixtureManifest.Create(definition),
            auxiliaryFiles
        );
    }

    private static string SingleDistinct(IEnumerable<string> values, string label)
    {
        List<string> distinct = [.. values.Distinct()];
        return distinct.Count == 1
            ? distinct[0]
            : throw new PerfObservationException(
                $"Expected one distinct {label} text across cells; observed {distinct.Count}."
            );
    }
}
