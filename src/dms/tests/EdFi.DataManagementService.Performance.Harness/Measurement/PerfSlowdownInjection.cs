// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// A parsed <c>PERF_INJECT_SLOWDOWN</c> value: <c>&lt;scenarioId&gt;=&lt;factor&gt;;…</c>, where a
/// read scenario id carries its page size (<c>traditional-offset-zero/25</c>). Unset means
/// no injection.
/// </summary>
public sealed record PerfSlowdownSpec(IReadOnlyDictionary<string, decimal> Factors)
{
    public static readonly PerfSlowdownSpec None = new(
        new SortedDictionary<string, decimal>(StringComparer.Ordinal)
    );

    public bool IsNone => Factors.Count == 0;

    public static PerfSlowdownSpec FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(PerfEnvironmentVariables.InjectSlowdown));

    public static PerfSlowdownSpec Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return None;
        }

        List<string> errors = [];
        SortedDictionary<string, decimal> factors = new(StringComparer.Ordinal);
        foreach (
            string entry in value.Split(
                ';',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            string[] parts = entry.Split('=', StringSplitOptions.TrimEntries);
            string at = $"{PerfEnvironmentVariables.InjectSlowdown} entry '{entry}'";
            if (parts.Length != 2)
            {
                errors.Add($"{at} must be <scenarioId>=<factor>.");
                continue;
            }

            if (!PerfBenchmarkScenarios.AllIds.Contains(parts[0]))
            {
                errors.Add($"{at}: unknown scenario '{parts[0]}'.");
            }
            else if (factors.ContainsKey(parts[0]))
            {
                errors.Add($"{at}: scenario '{parts[0]}' is listed twice.");
            }

            if (
                !decimal.TryParse(
                    parts[1],
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out decimal factor
                )
                || factor <= 0
                || factor > PerfBenchmarkProfileLoader.MaximumInjectionFactor
            )
            {
                errors.Add(
                    $"{at}: the factor must be a number in (0, {PerfBenchmarkProfileLoader.MaximumInjectionFactor}]."
                );
                continue;
            }

            factors.TryAdd(parts[0], factor);
        }

        return errors.Count > 0
            ? throw new PerfConfigurationException(errors)
            : new PerfSlowdownSpec(factors);
    }
}

/// <summary>
/// Test-only slowdown, registered as the outermost middleware on candidate sides only. For a
/// request matching an injected scenario (by method and URL shape) it buffers the response,
/// measures the rest of the pipeline, then busy-waits <c>factor × that pipeline time</c>
/// before releasing the response, so the client observes the slowdown end to end.
/// <see cref="Stopwatch" /> timestamps are used because <c>Task.Delay</c> is too coarse on
/// Windows. A busy-wait burns harness CPU; that is the point of a proportional slowdown.
/// </summary>
public sealed class PerfSlowdownInjection : IStartupFilter
{
    private readonly PerfSlowdownSpec _spec;
    private readonly IReadOnlyDictionary<(int PageSize, long Offset), string> _readCells;
    private readonly Func<long> _timestamp;
    private readonly ConcurrentDictionary<string, int> _affected = new(StringComparer.Ordinal);

    public PerfSlowdownInjection(PerfSlowdownSpec spec, long deepOffset, Func<long>? timestamp = null)
    {
        _spec = spec;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _readCells = PerfScenarioExecutor
            .CellsInExecutionOrder(deepOffset)
            .ToDictionary(
                cell => (cell.PageSize, cell.Offset),
                cell => PerfBenchmarkScenarios.ReadCellId(cell.ScenarioId, cell.PageSize)
            );
    }

    /// <summary>
    /// Requests the injection slowed down, for every scenario (zero when none).
    /// </summary>
    public IReadOnlyDictionary<string, int> AffectedRequests =>
        PerfBenchmarkScenarios.AllIds.ToDictionary(id => id, id => _affected.GetValueOrDefault(id));

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        string? scenario = ScenarioOf(context.Request);
        if (scenario is null || !_spec.Factors.TryGetValue(scenario, out decimal factor))
        {
            await next(context);
            return;
        }

        IHttpResponseBodyFeature original =
            context.Features.Get<IHttpResponseBodyFeature>()
            ?? throw new InvalidOperationException("The response has no body feature.");
        using MemoryStream buffer = new();
        StreamResponseBodyFeature buffering = new(buffer);
        context.Features.Set<IHttpResponseBodyFeature>(buffering);

        long start = _timestamp();
        try
        {
            await next(context);
            await buffering.CompleteAsync();
        }
        finally
        {
            context.Features.Set(original);
        }

        long end = _timestamp();
        long deadline = end + (long)Math.Ceiling((end - start) * (double)factor);
        while (_timestamp() < deadline)
        {
            Thread.SpinWait(20);
        }

        _affected.AddOrUpdate(scenario, 1, (_, count) => count + 1);

        buffer.Position = 0;
        await buffer.CopyToAsync(original.Stream, context.RequestAborted);
    }

    /// <summary>
    /// <c>PUT /data/ed-fi/students/{id}</c> is the write; <c>GET /data/ed-fi/students</c>
    /// with a cell's <c>limit</c> and <c>offset</c> is that read cell. Anything else is null.
    /// </summary>
    public string? ScenarioOf(HttpRequest request)
    {
        string path = (request.PathBase + request.Path).Value?.TrimEnd('/') ?? string.Empty;

        if (
            HttpMethods.IsPut(request.Method)
            && path.StartsWith(
                PerfFixtureDefinition.ResourceEndpoint + "/",
                StringComparison.OrdinalIgnoreCase
            )
            && Guid.TryParse(path[(PerfFixtureDefinition.ResourceEndpoint.Length + 1)..], out _)
        )
        {
            return PerfBenchmarkScenarios.StudentPutCollectionUpdate;
        }

        bool isCollectionGet =
            HttpMethods.IsGet(request.Method)
            && string.Equals(
                path,
                PerfFixtureDefinition.ResourceEndpoint,
                StringComparison.OrdinalIgnoreCase
            );
        return
            isCollectionGet
            && request.Query.Count == 2
            && int.TryParse(
                request.Query["limit"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int limit
            )
            && long.TryParse(
                request.Query["offset"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long offset
            )
            && _readCells.TryGetValue((limit, offset), out string? cellId)
            ? cellId
            : null;
    }
}
