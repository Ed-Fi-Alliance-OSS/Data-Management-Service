// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Appends one CSV row per timed reference-resolution iteration, so the Phase 3 cutover gate (old
/// <c>dms.ReferentialIdentity</c> hash resolver vs the natural-key resolver) is decided from recorded
/// numbers rather than impressions.
/// </summary>
/// <remarks>
/// Modelled directly on <c>MssqlProvisioningTimingRecorder</c>
/// (<c>Backend.Tests.Integration.Common/MssqlGeneratedDdlTestDatabase.cs</c>): an environment-variable-gated
/// output path, <c>Stopwatch</c>-sourced durations, invariant-culture formatting, and a lock around the
/// append so parallel fixtures cannot interleave partial lines. Both gates are opt-in — the fixtures do not
/// run at all unless <see cref="EnabledVariable" /> is <c>1</c>, and the recorder is a no-op unless
/// <see cref="OutputPathVariable" /> names a file — so default CI and local test runs are unaffected.
/// </remarks>
public static class ReferenceResolverBenchmarkRecorder
{
    /// <summary>Set to <c>1</c> to let the benchmark fixtures run instead of ignoring themselves.</summary>
    public const string EnabledVariable = "DMS_RESOLVER_BENCHMARK";

    /// <summary>Names the CSV file this recorder appends to; unset means "record nothing".</summary>
    public const string OutputPathVariable = "DMS_RESOLVER_BENCHMARK_PATH";

    /// <summary>The old resolver: <c>dms.ReferentialIdentity</c> → <c>dms.Document</c> by UUIDv5 hash.</summary>
    public const string HashArm = "referential-identity-hash";

    /// <summary>The new resolver: a seek on the target's own <c>UX_&lt;T&gt;_RefKey</c>.</summary>
    public const string NaturalKeyArm = "natural-key";

    public const string PostgresqlEngine = "postgresql";

    public const string MssqlEngine = "mssql";

    private const string CsvHeader =
        "TimestampUtc,Engine,Arm,Case,ReferenceCount,Iteration,ElapsedMilliseconds,MachineName,ProcessorCount,RuntimeVersion";

    private static readonly object _lock = new();

    /// <summary>
    /// Whether the benchmark fixtures should execute. Deliberately separate from
    /// <see cref="OutputPathVariable" />: a run can be exercised for correctness without producing a CSV.
    /// </summary>
    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal);

    /// <summary>
    /// Appends one timed iteration. No-ops when <see cref="OutputPathVariable" /> is unset or blank.
    /// </summary>
    /// <param name="engine">The database engine the iteration ran against.</param>
    /// <param name="arm">Which resolver produced the timing (<see cref="HashArm" /> or <see cref="NaturalKeyArm" />).</param>
    /// <param name="benchmarkCase">The workload name, stable across arms and engines.</param>
    /// <param name="referenceCount">References in the resolved batch — recorded because it differs per engine.</param>
    /// <param name="iteration">One-based timed iteration ordinal; warm-ups are not recorded.</param>
    /// <param name="elapsed">Wall time of the single <c>ResolveAsync</c> call.</param>
    public static void Record(
        string engine,
        string arm,
        string benchmarkCase,
        int referenceCount,
        int iteration,
        TimeSpan elapsed
    )
    {
        var outputPath = Environment.GetEnvironmentVariable(OutputPathVariable);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string[] fields =
        [
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            engine,
            arm,
            benchmarkCase,
            referenceCount.ToString(CultureInfo.InvariantCulture),
            iteration.ToString(CultureInfo.InvariantCulture),
            elapsed.TotalMilliseconds.ToString("0.000", CultureInfo.InvariantCulture),
            Environment.MachineName,
            Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            Environment.Version.ToString(),
        ];

        lock (_lock)
        {
            var writeHeader = !File.Exists(fullPath);
            using var writer = new StreamWriter(fullPath, append: true, Encoding.UTF8);
            if (writeHeader)
            {
                writer.WriteLine(CsvHeader);
            }

            writer.WriteLine(string.Join(",", fields.Select(EscapeCsv)));
        }
    }

    /// <summary>
    /// The middle timing of the sample (mean of the two middles when the count is even).
    /// </summary>
    public static TimeSpan Median(IReadOnlyList<TimeSpan> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var ordered = samples.OrderBy(sample => sample.Ticks).ToArray();
        var middle = ordered.Length / 2;

        return ordered.Length % 2 == 1
            ? ordered[middle]
            : TimeSpan.FromTicks((ordered[middle - 1].Ticks + ordered[middle].Ticks) / 2);
    }

    /// <summary>
    /// The nearest-rank percentile — no interpolation, so a reported value is always an observed one.
    /// </summary>
    public static TimeSpan Percentile(IReadOnlyList<TimeSpan> samples, double percentile)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(percentile);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 1d);

        if (samples.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var ordered = samples.OrderBy(sample => sample.Ticks).ToArray();
        var rank = (int)Math.Ceiling(percentile * ordered.Length);

        return ordered[Math.Clamp(rank - 1, 0, ordered.Length - 1)];
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
