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
                item.Cell.DriverExecuteMs,
                item.Metrics,
                item.PlanFile,
                item.Cell.PageSelection.Sha256,
                commits.RunnerCommit,
                commits.SubjectCommit
            )),
        ];

        // The full captured dictionary is retained, not just the paging values: the planner
        // can bind filter, change-version, and authorization values, and the replay is only
        // reproducible with all of them. Keys are sorted for deterministic artifacts.
        JsonArray boundParameters = [];
        foreach (PerfCellEvidence item in evidence)
        {
            JsonObject parameters = new();
            foreach (
                (string name, object? value) in item.Cell.PageSelection.ParameterValues.OrderBy(
                    pair => pair.Key,
                    StringComparer.Ordinal
                )
            )
            {
                parameters[name] = value is null
                    ? null
                    : System.Text.Json.JsonSerializer.SerializeToNode(value);
            }

            boundParameters.Add(
                new JsonObject
                {
                    ["scenarioId"] = item.Cell.ScenarioId,
                    ["pageSize"] = item.Cell.PageSize,
                    ["offset"] = item.Cell.Offset,
                    ["parameters"] = parameters,
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
                    // LF-only like every other artifact, so runs diff identically across
                    // platforms.
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }
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
