// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Tests.Integration;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// One cursor cell to measure: the page begins exactly at the given anchor DocumentId, must
/// return exactly the expected documents in order, and must advance to the expected next
/// token. The expected values are analytic — the deterministic fixtures make every page's
/// membership predictable — so a cell that returns plausible-but-wrong rows (an authorization
/// or filter predicate silently lost) fails observation rather than producing evidence.
/// </summary>
public sealed record PerfCursorCellRequest(
    string ScenarioId,
    string ResourceEndpoint,
    int PageSize,
    long StartAnchorDocumentId,
    long ExpectedNextTokenInclusiveMinimum,
    IReadOnlyList<Guid> ExpectedDocumentUuids,
    string? FilterQueryString = null,
    PerfCursorCaptureChannel CaptureChannel = PerfCursorCaptureChannel.HydrationKeyset
);

/// <summary>
/// Which recorder seam a cursor cell's page selection is captured on. Regular resources
/// hydrate through the document hydrator, whose recorded keyset carries the compiled SQL and
/// bound parameter values; descriptor reads execute through the relational command channel,
/// where only the observed command text exists and the shape gate is textual.
/// </summary>
public enum PerfCursorCaptureChannel
{
    HydrationKeyset,
    RelationalCommand,
}

/// <summary>
/// One fully measured cursor cell before plan capture.
/// </summary>
public sealed record PerfCursorMeasuredCell(
    string ScenarioId,
    int PageSize,
    long StartAnchorDocumentId,
    int ReturnedRows,
    int CommandCountPerRequest,
    PerfLatencySummary LatencyMs,
    PerfLatencySummary DriverExecuteMs,
    PageSelectionQueryCapture PageSelection,
    string HydrationBatchSql
);

/// <summary>
/// Executes cursor cells against the in-process pipeline. The command-observer window is per
/// request: every iteration must observe exactly one database command, return exactly the
/// expected page membership in order, carry a decodable DocumentId-anchored Next-Page-Token
/// advancing to the expected bound, and compile a cursor-shaped page selection with the
/// cell's bound range and page size. Any deviation throws — a cell that did not do the
/// expected work is not evidence. The timed window covers only the HTTP request and response
/// content read; all verification runs outside it, after each sample is taken.
/// </summary>
public static class PerfCursorScenarioExecutor
{
    public const string NextPageTokenHeaderName = "Next-Page-Token";

    public static string RequestUrl(PerfCursorCellRequest cell)
    {
        string url =
            $"{cell.ResourceEndpoint}?pageToken={Uri.EscapeDataString(PerfCursorTokens.DocumentIdRangeFrom(cell.StartAnchorDocumentId))}"
            + $"&pageSize={cell.PageSize}";
        return cell.FilterQueryString is null ? url : $"{url}&{cell.FilterQueryString}";
    }

    public static async Task<PerfCursorMeasuredCell> RunCellAsync(
        ApiIntegrationHarness harness,
        PerfProvider provider,
        PerfCursorCellRequest cell,
        int warmupIterations,
        int measuredIterations
    )
    {
        if (cell.ExpectedDocumentUuids.Count != cell.PageSize)
        {
            throw new PerfObservationException(
                $"{cell.ScenarioId}/{cell.PageSize}: expected-document count "
                    + $"{cell.ExpectedDocumentUuids.Count} must equal the page size."
            );
        }

        ApiIntegrationQueryRecorder recorder =
            harness.QueryRecorder
            ?? throw new PerfObservationException(
                "Query recording must be enabled (CaptureQueryPlans) for measured runs."
            );

        string at = $"{cell.ScenarioId}/{cell.PageSize}";
        string url = RequestUrl(cell);

        using DriverCommandObserver observer = DriverCommandObserver.Start(provider);
        int recorderBaseline = recorder.HydrationKeysets.Count;
        int relationalBaseline = recorder.RelationalCommandExecutions;
        List<double> driverExecuteSamplesMs = [];
        string? hydrationBatchSql = null;

        int commandBaseline = 0;
        HttpStatusCode responseStatus = default;
        string responseBody = string.Empty;
        string? nextPageToken = null;

        PerfLatencySummary latency = await PerfLatencyMeasurement.MeasureAsync(
            async _ =>
            {
                using HttpResponseMessage response = await harness.HttpClient.GetAsync(url);
                responseStatus = response.StatusCode;
                nextPageToken = response.Headers.TryGetValues(
                    NextPageTokenHeaderName,
                    out IEnumerable<string>? values
                )
                    ? values.FirstOrDefault()
                    : null;
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

                VerifyPageMembership(cell, responseBody, at, iteration);
                VerifyNextPageToken(cell, nextPageToken, at, iteration);

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

        int expectedRequests = warmupIterations + measuredIterations;
        string observedCommandSql =
            hydrationBatchSql ?? throw new PerfObservationException($"{at}: no command SQL was observed.");
        PageSelectionQueryCapture capture = ExtractPageSelection(
            cell,
            recorder,
            recorderBaseline,
            relationalBaseline,
            expectedRequests,
            observedCommandSql,
            at
        );

        return new PerfCursorMeasuredCell(
            cell.ScenarioId,
            cell.PageSize,
            cell.StartAnchorDocumentId,
            ReturnedRows: cell.PageSize,
            CommandCountPerRequest: 1,
            latency,
            PerfLatencyMeasurement.Summarize(driverExecuteSamplesMs),
            capture,
            observedCommandSql
        );
    }

    /// <summary>
    /// Captures and shape-checks the page selection on the cell's recorder channel. The
    /// hydration-keyset channel carries the compiled SQL and bound parameter values per
    /// request; the relational-command channel (descriptor reads) records execution counts
    /// only, so its capture is the observed command text and the shape gate is textual.
    /// </summary>
    private static PageSelectionQueryCapture ExtractPageSelection(
        PerfCursorCellRequest cell,
        ApiIntegrationQueryRecorder recorder,
        int recorderBaseline,
        int relationalBaseline,
        int expectedRequests,
        string observedCommandSql,
        string at
    )
    {
        List<PageKeysetSpec> keysets = [.. recorder.HydrationKeysets.Skip(recorderBaseline)];
        int relationalExecutions = recorder.RelationalCommandExecutions - relationalBaseline;

        if (cell.CaptureChannel == PerfCursorCaptureChannel.HydrationKeyset)
        {
            if (keysets.Count != expectedRequests)
            {
                throw new PerfObservationException(
                    $"{at}: expected {expectedRequests} recorded hydration keysets; observed {keysets.Count}."
                );
            }

            if (relationalExecutions != 0)
            {
                throw new PerfObservationException(
                    $"{at}: a hydrated read must issue no relational-channel commands; observed "
                        + $"{relationalExecutions}."
                );
            }

            PageSelectionQueryCapture capture = PageSelectionCapture.ExtractSingleQuery([keysets[0]]);
            foreach (PageKeysetSpec keyset in keysets)
            {
                PageSelectionQueryCapture iterationCapture = PageSelectionCapture.ExtractSingleQuery([
                    keyset,
                ]);
                if (iterationCapture.PageDocumentIdSql != capture.PageDocumentIdSql)
                {
                    throw new PerfObservationException(
                        $"{at}: page-selection SQL text changed within the cell."
                    );
                }

                PerfCursorSqlShape.EnsureCursorShaped(
                    iterationCapture,
                    cell.StartAnchorDocumentId,
                    cell.PageSize,
                    at
                );
            }

            return capture;
        }

        if (keysets.Count != 0)
        {
            throw new PerfObservationException(
                $"{at}: a relational-channel read must record no hydration keysets; observed "
                    + $"{keysets.Count}."
            );
        }

        if (relationalExecutions != expectedRequests)
        {
            throw new PerfObservationException(
                $"{at}: expected {expectedRequests} relational-channel commands; observed "
                    + $"{relationalExecutions}."
            );
        }

        PerfCursorSqlShape.EnsureCursorShapedText(observedCommandSql, at);
        return new PageSelectionQueryCapture(
            observedCommandSql,
            new Dictionary<string, object?>(),
            PageSelectionCapture.Sha256Lowercase(observedCommandSql)
        );
    }

    /// <summary>
    /// The page must hold exactly the expected documents in order. Identity, not just count:
    /// a candidate set that lost its authorization or filter predicate can still fill a page.
    /// </summary>
    private static void VerifyPageMembership(
        PerfCursorCellRequest cell,
        string responseBody,
        string at,
        int iteration
    )
    {
        if (JsonNode.Parse(responseBody) is not JsonArray items)
        {
            throw new PerfObservationException(
                $"{at} iteration {iteration}: the GET-many response body is not a JSON array."
            );
        }

        if (items.Count != cell.PageSize)
        {
            throw new PerfObservationException(
                $"{at} iteration {iteration}: returned rows {items.Count} must equal page size "
                    + $"{cell.PageSize}."
            );
        }

        for (int index = 0; index < items.Count; index++)
        {
            string? idText = items[index]?["id"]?.GetValue<string>();
            if (idText is null || !Guid.TryParse(idText, out Guid id))
            {
                throw new PerfObservationException(
                    $"{at} iteration {iteration}: item {index} carries no parseable id."
                );
            }

            if (id != cell.ExpectedDocumentUuids[index])
            {
                throw new PerfObservationException(
                    $"{at} iteration {iteration}: item {index} was {id}; expected "
                        + $"{cell.ExpectedDocumentUuids[index]} — the page membership does not match the "
                        + "variant's candidate selection."
                );
            }
        }
    }

    private static void VerifyNextPageToken(
        PerfCursorCellRequest cell,
        string? nextPageToken,
        string at,
        int iteration
    )
    {
        if (string.IsNullOrEmpty(nextPageToken))
        {
            throw new PerfObservationException(
                $"{at} iteration {iteration}: the {NextPageTokenHeaderName} header is missing."
            );
        }

        if (
            !PerfCursorTokens.TryDecodeDocumentIdRange(
                nextPageToken,
                out long inclusiveMinimum,
                out long inclusiveMaximum
            )
        )
        {
            throw new PerfObservationException(
                $"{at} iteration {iteration}: the next page token is not a DocumentId-anchored range."
            );
        }

        if (inclusiveMinimum != cell.ExpectedNextTokenInclusiveMinimum || inclusiveMaximum != long.MaxValue)
        {
            throw new PerfObservationException(
                $"{at} iteration {iteration}: next token range [{inclusiveMinimum}, {inclusiveMaximum}] "
                    + $"must be [{cell.ExpectedNextTokenInclusiveMinimum}, {long.MaxValue}]."
            );
        }
    }
}
