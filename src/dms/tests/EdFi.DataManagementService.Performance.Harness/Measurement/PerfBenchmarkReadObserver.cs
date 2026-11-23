// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// Hooks <see cref="PerfScenarioExecutor" /> calls around every paging request, all outside
/// the timed window. <c>iteration</c> is the absolute iteration (warmups first).
/// </summary>
public interface IPerfReadObserver
{
    /// <summary>
    /// Before dispatch.
    /// </summary>
    void OnAttempt(PerfExecutedCell cell, int iteration, bool isWarmup);

    /// <summary>
    /// First after the response; throws to fail the request.
    /// </summary>
    Task VerifyAsync(PerfExecutedCell cell, int iteration, HttpStatusCode status, string body);

    /// <summary>
    /// Last after the response, only once every check passed.
    /// </summary>
    void OnCompleted(PerfExecutedCell cell, int iteration, bool isWarmup);
}

/// <summary>
/// The benchmark side's read observer. It keeps attempted and completed counts per cell,
/// warmup and measured apart, so they survive any exception; it checks that every page
/// holds exactly the expected students in order and that the first and last items carry the
/// fixture's hydrated collections; and it records each cell's measured window.
/// </summary>
public sealed class PerfBenchmarkReadObserver : IPerfReadObserver
{
    private readonly int _warmupIterations;
    private readonly int _measuredIterations;
    private readonly Func<PerfWindowStamp> _clock;
    private readonly List<CellState> _cells;

    public PerfBenchmarkReadObserver(
        long deepOffset,
        int warmupIterations,
        int measuredIterations,
        Func<PerfWindowStamp>? clock = null
    )
    {
        _warmupIterations = warmupIterations;
        _measuredIterations = measuredIterations;
        _clock = clock ?? PerfWindowStamp.Now;
        _cells =
        [
            .. PerfScenarioExecutor.CellsInExecutionOrder(deepOffset).Select(cell => new CellState(cell)),
        ];
    }

    public bool AllReadsCompleted =>
        _cells.TrueForAll(state =>
            state.WarmupCompleted == _warmupIterations && state.MeasuredCompleted == _measuredIterations
        );

    /// <summary>
    /// Every planned cell in execution order, including cells never attempted.
    /// </summary>
    public IReadOnlyList<PerfReadCellCounts> Counts =>
        [
            .. _cells.Select(state => new PerfReadCellCounts(
                CellIdOf(state.Cell),
                state.WarmupAttempted,
                state.WarmupCompleted,
                state.WarmupAttempted - state.WarmupCompleted,
                state.MeasuredAttempted,
                state.MeasuredCompleted,
                state.MeasuredAttempted - state.MeasuredCompleted,
                state.MeasuredWindow
            )),
        ];

    public void OnAttempt(PerfExecutedCell cell, int iteration, bool isWarmup)
    {
        CellState state = StateOf(cell);
        if (isWarmup)
        {
            state.WarmupAttempted++;
            return;
        }

        state.MeasuredAttempted++;
        if (iteration == _warmupIterations)
        {
            state.WindowStart = _clock();
        }
    }

    public Task VerifyAsync(PerfExecutedCell cell, int iteration, HttpStatusCode status, string body)
    {
        VerifyPage(cell, iteration, status, body);
        return Task.CompletedTask;
    }

    public void OnCompleted(PerfExecutedCell cell, int iteration, bool isWarmup)
    {
        CellState state = StateOf(cell);
        if (isWarmup)
        {
            state.WarmupCompleted++;
            return;
        }

        state.MeasuredCompleted++;
        if (iteration == _warmupIterations + _measuredIterations - 1)
        {
            state.MeasuredWindow = PerfMeasuredWindow.Between(
                state.WindowStart ?? throw new PerfObservationException("The measured window never opened."),
                _clock()
            );
        }
    }

    /// <summary>
    /// Item <c>i</c> must be the student at ordinal <c>offset + i + 1</c>, by document id
    /// and by studentUniqueId, and the first and last items must carry the fixture's
    /// hydrated collections. A page of the right size holding the wrong students fails.
    /// </summary>
    public static void VerifyPage(PerfExecutedCell cell, int iteration, HttpStatusCode status, string body)
    {
        string at = $"{CellIdOf(cell)} iteration {iteration}";
        if (status != HttpStatusCode.OK)
        {
            throw new PerfObservationException($"{at}: HTTP {(int)status}.");
        }

        JsonArray items;
        try
        {
            items =
                JsonNode.Parse(body) as JsonArray
                ?? throw new PerfObservationException($"{at}: the response body is not a JSON array.");
        }
        catch (JsonException exception)
        {
            throw new PerfObservationException($"{at}: the response body is not JSON: {exception.Message}");
        }

        if (items.Count != cell.PageSize)
        {
            throw new PerfObservationException($"{at}: {items.Count} items; expected {cell.PageSize}.");
        }

        for (int index = 0; index < items.Count; index++)
        {
            long ordinal = cell.Offset + index + 1;
            JsonNode? item = items[index];
            bool idMatches =
                Guid.TryParse(Text(item?["id"]), out Guid id)
                && id == PerfFixtureDefinition.DocumentUuidFor(ordinal);
            if (
                !idMatches
                || Text(item?["studentUniqueId"]) != PerfFixtureDefinition.StudentUniqueIdFor(ordinal)
            )
            {
                throw new PerfObservationException(
                    $"{at}: item {index} must be student {PerfFixtureDefinition.StudentUniqueIdFor(ordinal)} "
                        + $"({PerfFixtureDefinition.DocumentUuidFor(ordinal)}); got "
                        + $"{Text(item?["studentUniqueId"])} ({Text(item?["id"])})."
                );
            }
        }

        VerifyHydrated(at, 0, items[0]!, cell.Offset + 1);
        VerifyHydrated(at, items.Count - 1, items[^1]!, cell.Offset + items.Count);
    }

    private static void VerifyHydrated(string at, int index, JsonNode item, long ordinal)
    {
        JsonObject expected = PerfWriteScenarioExecutor.FixtureDocument(ordinal);
        if (Text(item["birthSexDescriptor"]) != Text(expected["birthSexDescriptor"]))
        {
            throw new PerfObservationException(
                $"{at}: item {index} birthSexDescriptor is not the fixture's."
            );
        }

        foreach (
            string collection in new[]
            {
                "otherNames",
                "identificationDocuments",
                "personalIdentificationDocuments",
                "visas",
            }
        )
        {
            JsonArray expectedItems = expected[collection]!.AsArray();
            bool matches =
                item[collection] is JsonArray actualItems
                && actualItems.Count == expectedItems.Count
                && expectedItems
                    .Select((expectedItem, position) => (expectedItem!.AsObject(), actualItems[position]))
                    .All(pair =>
                        pair.Item1.All(property => Text(pair.Item2?[property.Key]) == Text(property.Value))
                    );
            if (!matches)
            {
                throw new PerfObservationException(
                    $"{at}: item {index} {collection} must be the fixture's hydrated collection; got "
                        + $"{item[collection]?.ToJsonString() ?? "nothing"}."
                );
            }
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static string CellIdOf(PerfExecutedCell cell) =>
        PerfBenchmarkScenarios.ReadCellId(cell.ScenarioId, cell.PageSize);

    private CellState StateOf(PerfExecutedCell cell) =>
        _cells.SingleOrDefault(state => state.Cell == cell)
        ?? throw new PerfObservationException(
            $"{CellIdOf(cell)} at offset {cell.Offset} is not a planned cell."
        );

    private sealed class CellState(PerfExecutedCell cell)
    {
        public PerfExecutedCell Cell { get; } = cell;

        public int WarmupAttempted { get; set; }

        public int WarmupCompleted { get; set; }

        public int MeasuredAttempted { get; set; }

        public int MeasuredCompleted { get; set; }

        public PerfWindowStamp? WindowStart { get; set; }

        public PerfMeasuredWindow? MeasuredWindow { get; set; }
    }
}
