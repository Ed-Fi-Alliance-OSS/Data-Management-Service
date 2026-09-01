// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// One partition cell to measure: a boundary-selection request for the given count over the
/// variant's candidate set.
/// </summary>
public sealed record PerfPartitionCellRequest(
    string ScenarioId,
    string ResourceEndpoint,
    int RequestedNumber,
    string? FilterQueryString = null
);

/// <summary>
/// One fully measured partition cell before plan capture. The boundary SQL comes from the
/// driver observer's single command window; its SHA-256 is carried the same way the
/// page-selection hash is, so later comparison work can mechanically confirm the boundary
/// text never varied within or across runs.
/// </summary>
public sealed record PerfPartitionMeasuredCell(
    string ScenarioId,
    int RequestedNumber,
    int ReturnedTokenCount,
    int CommandCountPerRequest,
    PerfLatencySummary LatencyMs,
    PerfLatencySummary DriverExecuteMs,
    string BoundarySql,
    string BoundarySqlSha256
);

/// <summary>
/// Executes partition cells against the in-process pipeline. Every iteration must observe
/// exactly one database command and no hydration keyset — boundary selection hydrates
/// nothing — and must return a body holding nothing but page tokens: at least one, at most
/// the requested count, every one a DocumentId-anchored range, and the whole sequence stable
/// across iterations because the fixture is static. Any deviation throws. The timed window
/// covers only the HTTP request and response content read; all verification runs outside it.
/// </summary>
public static class PerfPartitionScenarioExecutor
{
    private const string PageTokensMember = "pageTokens";

    public static string RequestUrl(PerfPartitionCellRequest cell)
    {
        string url = $"{cell.ResourceEndpoint}/partitions?number={cell.RequestedNumber}";
        return cell.FilterQueryString is null ? url : $"{url}&{cell.FilterQueryString}";
    }

    /// <summary>
    /// Parses and validates a partition response body: one <c>pageTokens</c> member and
    /// nothing else, holding between one and <paramref name="requestedNumber" /> decodable
    /// DocumentId-anchored tokens.
    /// </summary>
    public static IReadOnlyList<string> ParsePageTokens(string responseBody, int requestedNumber)
    {
        if (JsonNode.Parse(responseBody) is not JsonObject body)
        {
            throw new PerfObservationException("The partition response body is not a JSON object.");
        }

        if (body.Count != 1 || body[PageTokensMember] is not JsonArray tokensNode)
        {
            throw new PerfObservationException(
                $"The partition response must hold exactly one '{PageTokensMember}' array and nothing else."
            );
        }

        List<string> tokens = [];
        foreach (JsonNode? tokenNode in tokensNode)
        {
            string? token = tokenNode?.GetValue<string>();
            if (
                string.IsNullOrEmpty(token) || !PerfCursorTokens.TryDecodeDocumentIdRange(token, out _, out _)
            )
            {
                throw new PerfObservationException(
                    "Every partition page token must be a DocumentId-anchored range."
                );
            }

            tokens.Add(token);
        }

        if (tokens.Count < 1 || tokens.Count > requestedNumber)
        {
            throw new PerfObservationException(
                $"Returned token count {tokens.Count} must be between 1 and the requested "
                    + $"number {requestedNumber}."
            );
        }

        return tokens;
    }

    public static async Task<PerfPartitionMeasuredCell> RunCellAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        PerfPartitionCellRequest cell,
        int warmupIterations,
        int measuredIterations
    )
    {
        ApiIntegrationQueryRecorder recorder =
            harness.QueryRecorder
            ?? throw new PerfObservationException(
                "Query recording must be enabled (CaptureQueryPlans) for measured runs."
            );

        string at = $"{cell.ScenarioId}/number={cell.RequestedNumber}";
        string url = RequestUrl(cell);

        using DriverCommandObserver observer = DriverCommandObserver.Start(provider);
        int recorderBaseline = recorder.HydrationKeysets.Count;
        int relationalBaseline = recorder.RelationalCommandExecutions;
        List<double> driverExecuteSamplesMs = [];
        string? boundarySql = null;
        IReadOnlyList<string>? firstTokens = null;

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

                IReadOnlyList<string> tokens = ParsePageTokens(responseBody, cell.RequestedNumber);
                firstTokens ??= tokens;
                if (!tokens.SequenceEqual(firstTokens))
                {
                    throw new PerfObservationException(
                        $"{at} iteration {iteration}: partition tokens changed within the cell over "
                            + "static data."
                    );
                }

                IReadOnlyList<ObservedDbCommand> window = [.. observer.Commands.Skip(commandBaseline)];
                if (window.Count != 1)
                {
                    throw new PerfObservationException(
                        $"{at} iteration {iteration}: expected exactly one database command per request; "
                            + $"observed {window.Count}."
                    );
                }

                boundarySql ??= window[0].CommandText;
                if (window[0].CommandText != boundarySql)
                {
                    throw new PerfObservationException(
                        $"{at} iteration {iteration}: boundary SQL text changed within the cell."
                    );
                }

                if (iteration >= warmupIterations)
                {
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

        if (recorder.HydrationKeysets.Count != recorderBaseline)
        {
            throw new PerfObservationException(
                $"{at}: boundary selection must hydrate nothing, but "
                    + $"{recorder.HydrationKeysets.Count - recorderBaseline} hydration keysets were recorded."
            );
        }

        // Boundary selection is the relational command channel's one command per request.
        int relationalExecutions = recorder.RelationalCommandExecutions - relationalBaseline;
        int expectedRequests = warmupIterations + measuredIterations;
        if (relationalExecutions != expectedRequests)
        {
            throw new PerfObservationException(
                $"{at}: expected {expectedRequests} relational-channel boundary commands; observed "
                    + $"{relationalExecutions}."
            );
        }

        string boundaryText =
            boundarySql ?? throw new PerfObservationException($"{at}: no boundary SQL was observed.");

        return new PerfPartitionMeasuredCell(
            cell.ScenarioId,
            cell.RequestedNumber,
            ReturnedTokenCount: (
                firstTokens ?? throw new PerfObservationException($"{at}: no tokens were observed.")
            ).Count,
            CommandCountPerRequest: 1,
            latency,
            PerfLatencyMeasurement.Summarize(driverExecuteSamplesMs),
            boundaryText,
            PageSelectionCapture.Sha256Lowercase(boundaryText)
        );
    }
}
