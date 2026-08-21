// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// One fully measured scenario cell before plan capture: everything a
/// <see cref="PerfScenarioResult" /> needs except the database plan metrics and plan file,
/// which the plan-replay stage adds.
/// </summary>
public sealed record PerfMeasuredCell(
    string ScenarioId,
    int PageSize,
    long Offset,
    int ReturnedRows,
    int CommandCountPerRequest,
    PerfLatencySummary LatencyMs,
    PerfLatencySummary DbCommandMs,
    PageSelectionQueryCapture PageSelection
);

/// <summary>
/// Executes the fixed six-cell traditional matrix against the in-process pipeline. The
/// command-observer window is per request: every iteration, warmup or measured, must observe
/// exactly one database command and exactly the requested row count, and every request in a
/// cell must compile the same page-selection SQL text with the cell's bound offset and page
/// size. Any deviation throws — a cell that did not do the expected work is not evidence.
/// </summary>
public static class PerfScenarioExecutor
{
    public static long OffsetFor(string scenarioId, int pageSize, long deepOffset) =>
        scenarioId switch
        {
            PerfScenarios.TraditionalOffsetZero => 0,
            PerfScenarios.TraditionalOffsetShallow => pageSize,
            PerfScenarios.TraditionalOffsetDeep => deepOffset,
            _ => throw new ArgumentException($"Unknown scenario id '{scenarioId}'.", nameof(scenarioId)),
        };

    public static IReadOnlyList<PerfExecutedCell> CellsInExecutionOrder(long deepOffset) =>
        [
            .. PerfScenarios.AllIds.SelectMany(scenarioId =>
                PerfScenarios.PageSizes.Select(pageSize => new PerfExecutedCell(
                    scenarioId,
                    pageSize,
                    OffsetFor(scenarioId, pageSize, deepOffset)
                ))
            ),
        ];

    public static async Task<IReadOnlyList<PerfMeasuredCell>> RunAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        long deepOffset,
        int warmupIterations,
        int measuredIterations
    )
    {
        List<PerfMeasuredCell> cells = [];
        foreach (PerfExecutedCell cell in CellsInExecutionOrder(deepOffset))
        {
            cells.Add(await RunCellAsync(harness, provider, cell, warmupIterations, measuredIterations));
        }

        return cells;
    }

    private static async Task<PerfMeasuredCell> RunCellAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        PerfExecutedCell cell,
        int warmupIterations,
        int measuredIterations
    )
    {
        ApiIntegrationQueryRecorder recorder =
            harness.QueryRecorder
            ?? throw new PerfObservationException(
                "Query recording must be enabled (CaptureQueryPlans) for measured runs."
            );

        string at = $"{cell.ScenarioId}/{cell.PageSize}";
        string url = $"{PerfFixtureDefinition.ResourceEndpoint}?limit={cell.PageSize}&offset={cell.Offset}";

        using DriverCommandObserver observer = DriverCommandObserver.Start(provider);
        int recorderBaseline = recorder.HydrationKeysets.Count;
        List<double> dbCommandSamplesMs = [];

        PerfLatencySummary latency = await PerfLatencyMeasurement.MeasureAsync(
            async iteration =>
            {
                int commandBaseline = observer.Commands.Count;
                await ExecuteRequestAsync(harness, url, cell, at, iteration);

                IReadOnlyList<ObservedDbCommand> window = [.. observer.Commands.Skip(commandBaseline)];
                if (window.Count != 1)
                {
                    throw new PerfObservationException(
                        $"{at} iteration {iteration}: expected exactly one database command per request; "
                            + $"observed {window.Count}."
                    );
                }

                if (iteration >= warmupIterations)
                {
                    dbCommandSamplesMs.Add(window[0].ElapsedMs);
                }
            },
            warmupIterations,
            measuredIterations
        );

        if (dbCommandSamplesMs.Count != measuredIterations)
        {
            throw new PerfObservationException(
                $"{at}: command timing sample count {dbCommandSamplesMs.Count} must equal "
                    + $"measured iterations {measuredIterations}."
            );
        }

        List<PageKeysetSpec> keysets = [.. recorder.HydrationKeysets.Skip(recorderBaseline)];
        int expectedRequests = warmupIterations + measuredIterations;
        if (keysets.Count != expectedRequests)
        {
            throw new PerfObservationException(
                $"{at}: expected {expectedRequests} recorded hydration keysets; observed {keysets.Count}."
            );
        }

        PageSelectionQueryCapture capture = PageSelectionCapture.ExtractSingleQuery([keysets[0]]);
        foreach (PageKeysetSpec keyset in keysets)
        {
            PageSelectionQueryCapture iterationCapture = PageSelectionCapture.ExtractSingleQuery([keyset]);
            if (iterationCapture.PageDocumentIdSql != capture.PageDocumentIdSql)
            {
                throw new PerfObservationException($"{at}: page-selection SQL text changed within the cell.");
            }

            VerifyBoundValue(iterationCapture, "offset", cell.Offset, at);
            VerifyBoundValue(iterationCapture, "limit", cell.PageSize, at);
        }

        return new PerfMeasuredCell(
            cell.ScenarioId,
            cell.PageSize,
            cell.Offset,
            cell.PageSize,
            CommandCountPerRequest: 1,
            latency,
            PerfLatencyMeasurement.Summarize(dbCommandSamplesMs),
            capture
        );
    }

    private static async Task ExecuteRequestAsync(
        ApiIntegrationHarness harness,
        string url,
        PerfExecutedCell cell,
        string at,
        int iteration
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(url);
        string body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new PerfObservationException(
                $"{at} iteration {iteration}: HTTP {(int)response.StatusCode}: "
                    + body[..Math.Min(body.Length, 500)]
            );
        }

        int returnedRows = JsonNode.Parse(body)!.AsArray().Count;
        if (returnedRows != cell.PageSize)
        {
            throw new PerfObservationException(
                $"{at} iteration {iteration}: returned rows {returnedRows} must equal page size {cell.PageSize}."
            );
        }
    }

    private static void VerifyBoundValue(
        PageSelectionQueryCapture capture,
        string parameterName,
        long expected,
        string at
    )
    {
        if (!capture.ParameterValues.TryGetValue(parameterName, out object? value) || value is null)
        {
            throw new PerfObservationException($"{at}: bound parameter '{parameterName}' was not captured.");
        }

        long actual = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (actual != expected)
        {
            throw new PerfObservationException(
                $"{at}: bound parameter '{parameterName}' was {actual}; expected {expected}."
            );
        }
    }
}
