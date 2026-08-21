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
    PerfLatencySummary DriverExecuteMs,
    PageSelectionQueryCapture PageSelection,
    string HydrationBatchSql
);

/// <summary>
/// Executes the fixed six-cell traditional matrix against the in-process pipeline. The
/// command-observer window is per request: every iteration, warmup or measured, must observe
/// exactly one database command and exactly the requested row count, and every request in a
/// cell must compile the same page-selection SQL text with the cell's bound offset and page
/// size. Any deviation throws — a cell that did not do the expected work is not evidence.
/// The timed window covers only the HTTP request and response content read; all of that
/// verification runs outside it, after each sample is taken.
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
        List<double> driverExecuteSamplesMs = [];
        string? hydrationBatchSql = null;
        int? observedReturnedRows = null;

        int commandBaseline = 0;
        HttpStatusCode responseStatus = default;
        string responseBody = string.Empty;

        PerfLatencySummary latency = await PerfLatencyMeasurement.MeasureAsync(
            async _ =>
            {
                using HttpResponseMessage response = await harness.HttpClient.GetAsync(url);
                responseStatus = response.StatusCode;
                responseBody = await response.Content.ReadAsStringAsync();
            },
            warmupIterations,
            measuredIterations,
            beforeIterationAsync: _ =>
            {
                commandBaseline = observer.Commands.Count;
                return Task.CompletedTask;
            },
            afterIterationAsync: iteration =>
            {
                if (responseStatus != HttpStatusCode.OK)
                {
                    throw new PerfObservationException(
                        $"{at} iteration {iteration}: HTTP {(int)responseStatus}: "
                            + responseBody[..Math.Min(responseBody.Length, 500)]
                    );
                }

                int returnedRows = CountReturnedRows(responseBody);
                if (returnedRows != cell.PageSize)
                {
                    throw new PerfObservationException(
                        $"{at} iteration {iteration}: returned rows {returnedRows} must equal "
                            + $"page size {cell.PageSize}."
                    );
                }

                observedReturnedRows ??= returnedRows;

                IReadOnlyList<ObservedDbCommand> window = [.. observer.Commands.Skip(commandBaseline)];
                if (window.Count != 1)
                {
                    throw new PerfObservationException(
                        $"{at} iteration {iteration}: expected exactly one database command per request; "
                            + $"observed {window.Count}."
                    );
                }

                hydrationBatchSql ??= window[0].CommandText;
                if (window[0].CommandText != hydrationBatchSql)
                {
                    throw new PerfObservationException(
                        $"{at} iteration {iteration}: hydration batch SQL text changed within the cell."
                    );
                }

                if (iteration >= warmupIterations)
                {
                    // The observer's interval ends at the provider's diagnostic "after"
                    // event, which SqlClient raises when ExecuteReader returns — before the
                    // rows are consumed. It is a driver execute/dispatch sample, never full
                    // database command time.
                    driverExecuteSamplesMs.Add(window[0].ElapsedMs);
                }

                return Task.CompletedTask;
            }
        );

        if (driverExecuteSamplesMs.Count != measuredIterations)
        {
            throw new PerfObservationException(
                $"{at}: driver execute timing sample count {driverExecuteSamplesMs.Count} must equal "
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
            observedReturnedRows
                ?? throw new PerfObservationException($"{at}: no returned row count was observed."),
            CommandCountPerRequest: 1,
            latency,
            PerfLatencyMeasurement.Summarize(driverExecuteSamplesMs),
            capture,
            hydrationBatchSql
                ?? throw new PerfObservationException($"{at}: no hydration batch SQL was observed.")
        );
    }

    /// <summary>
    /// The number of items in a GET-many response body, read from the observed response
    /// rather than assumed from the requested page size.
    /// </summary>
    public static int CountReturnedRows(string responseBody) =>
        JsonNode.Parse(responseBody) is JsonArray items
            ? items.Count
            : throw new PerfObservationException("The GET-many response body is not a JSON array.");

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
