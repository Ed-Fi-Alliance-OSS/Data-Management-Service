// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// A point on the host clock with the harness process's CPU time, taken at a measured
/// window's edges.
/// </summary>
public sealed record PerfWindowStamp(DateTimeOffset Utc, TimeSpan ProcessCpu)
{
    public static PerfWindowStamp Now()
    {
        using Process process = Process.GetCurrentProcess();
        return new PerfWindowStamp(DateTimeOffset.UtcNow, process.TotalProcessorTime);
    }
}

/// <summary>
/// A scenario's measured window: from just before the first measured request is dispatched
/// to just after the last one completes, with the harness process CPU spent over it
/// (descriptive only).
/// </summary>
public sealed record PerfMeasuredWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc, double ProcessCpuMs)
{
    public static PerfMeasuredWindow Between(PerfWindowStamp start, PerfWindowStamp end) =>
        new(start.Utc, end.Utc, (end.ProcessCpu - start.ProcessCpu).TotalMilliseconds);
}

public sealed record PerfReadCellCounts(
    string CellId,
    int WarmupAttempted,
    int WarmupCompleted,
    int WarmupFailed,
    int MeasuredAttempted,
    int MeasuredCompleted,
    int MeasuredFailed,
    PerfMeasuredWindow? MeasuredWindow = null
);

/// <summary>
/// <see cref="Phase" /> and <see cref="Reason" /> are set only when the side failed.
/// </summary>
public sealed record PerfBenchmarkSideStatus(string State, string? Phase = null, string? Reason = null)
{
    public const string Completed = "completed";
    public const string Failed = "failed";

    public const string ReadsPhase = "reads";
    public const string PostReadPhase = "post-read";
    public const string WritesPhase = "writes";

    public static readonly IReadOnlyList<string> Phases = [ReadsPhase, PostReadPhase, WritesPhase];
}

/// <summary>
/// The workload the side actually ran.
/// </summary>
public sealed record PerfBenchmarkSideWorkload(
    string Provider,
    string Fixture,
    long DeepOffset,
    PerfBenchmarkIterations Reads,
    PerfBenchmarkIterations Writes
);

/// <summary>
/// The measurement instrumentation every side shares. The injection is deliberately not
/// part of it: baseline and candidate sides must agree on instrumentation but differ on
/// injection.
/// </summary>
public sealed record PerfBenchmarkInstrumentation(bool QueryRecorder, bool DriverCommandObserver);

/// <summary>
/// The test-only slowdown the side ran with (empty factors when none) and the requests it
/// affected for every scenario.
/// </summary>
public sealed record PerfBenchmarkInjection(
    IReadOnlyDictionary<string, decimal> Factors,
    IReadOnlyDictionary<string, int> AffectedRequests
);

/// <summary>
/// <c>benchmark-side.json</c>: one side of one pair, written whether the side completed or
/// failed.
/// </summary>
public sealed record PerfBenchmarkSideResult(
    string SchemaVersion,
    string RunId,
    PerfBenchmarkSideStatus Status,
    string Role,
    int Pair,
    int Position,
    string ProfileVersion,
    string ProfileSha256,
    PerfBenchmarkSideWorkload Workload,
    IReadOnlyList<PerfReadCellCounts> ReadCells,
    PerfWriteScenarioResult Write,
    PerfBenchmarkConnectionSettings EffectiveConnection,
    string EffectiveSchemaHash,
    string EffectiveLogLevel,
    bool OwnershipPublished,
    PerfBenchmarkInstrumentation Instrumentation,
    PerfBenchmarkInjection Injection
)
{
    public const string CurrentSchemaVersion = "benchmark-side 1.0.0";
    public const string FileName = "benchmark-side.json";
    public const string BaselineRole = "baseline";
    public const string CandidateRole = "candidate";
}

/// <summary>
/// JSON for the benchmark artifacts: camelCase, indented with LF-only newlines, nulls
/// omitted on write, and strict on read (unknown, missing, duplicate or null-for-non-nullable
/// members are rejected).
/// </summary>
public static class PerfBenchmarkJson
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, _options);

    /// <summary>
    /// Reads strictly, reporting a malformed document as an artifact validation error that
    /// names <paramref name="fileName" />.
    /// </summary>
    public static T Read<T>(string json, string fileName)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, _options)
                ?? throw new JsonException($"The document is null.");
        }
        catch (JsonException exception)
        {
            throw new PerfArtifactValidationException([$"{fileName}: {exception.Message}"]);
        }
    }
}

/// <summary>
/// Serialization and validation of <see cref="PerfBenchmarkSideResult" />.
/// </summary>
public static partial class PerfBenchmarkSideResults
{
    public static string Serialize(PerfBenchmarkSideResult result) => PerfBenchmarkJson.Serialize(result);

    /// <summary>
    /// Reads strictly and validates a side result, throwing with every error at once.
    /// </summary>
    public static PerfBenchmarkSideResult Read(string json)
    {
        PerfBenchmarkSideResult result = PerfBenchmarkJson.Read<PerfBenchmarkSideResult>(
            json,
            PerfBenchmarkSideResult.FileName
        );

        IReadOnlyList<string> errors = Validate(result);
        return errors.Count > 0 ? throw new PerfArtifactValidationException(errors) : result;
    }

    public static IReadOnlyList<string> Validate(PerfBenchmarkSideResult result)
    {
        List<string> errors = [];

        if (result.SchemaVersion != PerfBenchmarkSideResult.CurrentSchemaVersion)
        {
            errors.Add(
                $"schemaVersion must be '{PerfBenchmarkSideResult.CurrentSchemaVersion}'; got '{result.SchemaVersion}'."
            );
        }

        if (string.IsNullOrWhiteSpace(result.RunId))
        {
            errors.Add("runId must not be blank.");
        }

        if (
            result.Role is not (PerfBenchmarkSideResult.BaselineRole or PerfBenchmarkSideResult.CandidateRole)
        )
        {
            errors.Add($"role must be 'baseline' or 'candidate'; got '{result.Role}'.");
        }

        if (result.Pair < 1 || result.Position is not (1 or 2))
        {
            errors.Add(
                $"pair must be at least 1 and position 1 or 2; got pair {result.Pair}, position {result.Position}."
            );
        }

        if (!Sha256().IsMatch(result.ProfileSha256))
        {
            errors.Add($"profileSha256 must be 64 lowercase hex characters; got '{result.ProfileSha256}'.");
        }

        ValidateStatus(result.Status, errors);
        ValidateReadCells(result, errors);
        ValidateWrite(result, errors);
        ValidateInjection(result.Injection, errors);

        return errors;
    }

    private static void ValidateStatus(PerfBenchmarkSideStatus status, List<string> errors)
    {
        switch (status.State)
        {
            case PerfBenchmarkSideStatus.Completed:
                if (status.Phase is not null || status.Reason is not null)
                {
                    errors.Add("status: a completed side carries no phase or reason.");
                }

                break;
            case PerfBenchmarkSideStatus.Failed:
                if (status.Phase is null || !PerfBenchmarkSideStatus.Phases.Contains(status.Phase))
                {
                    errors.Add(
                        $"status.phase must be one of {string.Join(", ", PerfBenchmarkSideStatus.Phases)} "
                            + $"for a failed side; got '{status.Phase}'."
                    );
                }

                if (string.IsNullOrWhiteSpace(status.Reason))
                {
                    errors.Add("status.reason is required for a failed side.");
                }

                break;
            default:
                errors.Add($"status.state must be 'completed' or 'failed'; got '{status.State}'.");
                break;
        }
    }

    private static void ValidateReadCells(PerfBenchmarkSideResult result, List<string> errors)
    {
        IReadOnlyList<string> expectedCells = [.. PerfBenchmarkScenarios.AllIds.SkipLast(1)];
        if (!result.ReadCells.Select(cell => cell.CellId).SequenceEqual(expectedCells))
        {
            errors.Add($"readCells must list exactly {string.Join(", ", expectedCells)} in that order.");
        }

        bool completed = result.Status.State == PerfBenchmarkSideStatus.Completed;
        PerfBenchmarkIterations planned = result.Workload.Reads;
        foreach (PerfReadCellCounts cell in result.ReadCells)
        {
            string at = $"readCells.{cell.CellId}";
            if (
                cell.WarmupFailed != cell.WarmupAttempted - cell.WarmupCompleted
                || cell.MeasuredFailed != cell.MeasuredAttempted - cell.MeasuredCompleted
            )
            {
                errors.Add($"{at}: failed must equal attempted - completed in each phase.");
            }

            if (
                completed
                && (
                    cell.WarmupCompleted != planned.WarmupIterations
                    || cell.MeasuredCompleted != planned.MeasuredIterations
                    || cell.WarmupFailed + cell.MeasuredFailed != 0
                    || cell.MeasuredWindow is null
                )
            )
            {
                errors.Add(
                    $"{at}: a completed side must complete every planned read with a measured window."
                );
            }
        }
    }

    private static void ValidateWrite(PerfBenchmarkSideResult result, List<string> errors)
    {
        PerfWriteScenarioResult write = result.Write;
        if (write.ScenarioId != PerfBenchmarkScenarios.StudentPutCollectionUpdate)
        {
            errors.Add($"write.scenarioId must be '{PerfBenchmarkScenarios.StudentPutCollectionUpdate}'.");
        }

        if (
            write.Failed
            != write.WarmupAttempted
                + write.MeasuredAttempted
                - write.WarmupCompleted
                - write.MeasuredCompleted
        )
        {
            errors.Add("write: failed must equal attempted - completed.");
        }

        if (result.Status.State != PerfBenchmarkSideStatus.Completed)
        {
            return;
        }

        PerfBenchmarkIterations planned = result.Workload.Writes;
        if (
            write.WarmupCompleted != planned.WarmupIterations
            || write.MeasuredCompleted != planned.MeasuredIterations
            || write.Failed != 0
            || write.MeasuredWindow is null
        )
        {
            errors.Add("write: a completed side must complete every planned write with a measured window.");
        }

        if (write.LatencyMs?.SamplesMs.Count != planned.MeasuredIterations)
        {
            errors.Add(
                $"write.latencyMs must hold exactly {planned.MeasuredIterations} samples; "
                    + $"got {write.LatencyMs?.SamplesMs.Count ?? 0}."
            );
        }
    }

    private static void ValidateInjection(PerfBenchmarkInjection injection, List<string> errors)
    {
        if (
            !injection
                .AffectedRequests.Keys.Order(StringComparer.Ordinal)
                .SequenceEqual(PerfBenchmarkScenarios.AllIds.Order(StringComparer.Ordinal))
        )
        {
            errors.Add("injection.affectedRequests must have one count for every scenario.");
        }

        foreach ((string scenario, decimal factor) in injection.Factors)
        {
            if (!PerfBenchmarkScenarios.AllIds.Contains(scenario))
            {
                errors.Add($"injection.factors: unknown scenario '{scenario}'.");
            }

            if (factor <= 0 || factor > PerfBenchmarkProfileLoader.MaximumInjectionFactor)
            {
                errors.Add(
                    $"injection.factors.{scenario} must be in (0, {PerfBenchmarkProfileLoader.MaximumInjectionFactor}]."
                );
            }
        }

        foreach ((string scenario, int affected) in injection.AffectedRequests)
        {
            if (affected < 0 || (affected > 0 && !injection.Factors.ContainsKey(scenario)))
            {
                errors.Add(
                    $"injection.affectedRequests.{scenario}: only an injected scenario has affected requests."
                );
            }
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();
}
