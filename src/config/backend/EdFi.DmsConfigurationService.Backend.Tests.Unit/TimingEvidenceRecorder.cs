// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace EdFi.DmsConfigurationService.Tests.TimingEvidence;

/// <summary>
/// One kind of request a timing run measures. <see cref="Send"/> is timed in full.
/// <see cref="IsExpected"/> runs after the clock stops and aborts the run when a response is not the
/// one the arm stands for, so a misconfigured arm can never contribute samples.
/// </summary>
internal sealed record TimingArm<TResponse>(
    string Name,
    Func<Task<TResponse>> Send,
    Func<TResponse, bool> IsExpected
);

/// <summary>
/// The three files one run writes: the measured samples, the warm-up samples and the header.
/// <see cref="Samples"/> and <see cref="WarmupSamples"/> count rows, without the column row.
/// </summary>
internal sealed record TimingRunFiles(
    string SamplesPath,
    string WarmupPath,
    string HeaderPath,
    int Samples,
    int WarmupSamples
);

/// <summary>
/// The clocks a run reads: the high-resolution timestamp that times each request and stamps each
/// round (on Windows, the system-wide QueryPerformanceCounter, which the power sampler also reads),
/// its frequency, and the wall clock. Tests replace them to check what is read, and when.
/// </summary>
internal sealed record TimingClock(Func<long> Timestamp, long Frequency, Func<DateTime> UtcNow)
{
    public static TimingClock System { get; } =
        new(Stopwatch.GetTimestamp, Stopwatch.Frequency, () => DateTime.UtcNow);
}

/// <summary>
/// The settings of one run, read from <c>TIMING_EVIDENCE_*</c> environment variables and recorded
/// in the run's header, so every run can be repeated exactly. <see cref="Commit"/> is the build of
/// the collector itself; a run labelled <c>smoke</c> is a tooling check, never evidence, and is the
/// only kind of run that may leave declarations unset.
/// </summary>
internal sealed record TimingRunSettings(
    string OutputDirectory,
    string RunLabel,
    int WarmupRounds,
    int Rounds,
    int Seed,
    string Commit
)
{
    private const string SmokeLabel = "smoke";

    public bool IsSmoke => string.Equals(RunLabel, SmokeLabel, StringComparison.Ordinal);

    public static TimingRunSettings FromEnvironment()
    {
        string runLabel = Optional("TIMING_EVIDENCE_RUN_LABEL", "pilot");
        string commitName = "TIMING_EVIDENCE_COMMIT";
        return new(
            Required("TIMING_EVIDENCE_OUTPUT_DIR"),
            runLabel,
            PositiveInteger("TIMING_EVIDENCE_WARMUP_ROUNDS", 50),
            PositiveInteger("TIMING_EVIDENCE_ROUNDS", 200),
            Integer("TIMING_EVIDENCE_SEED", 1),
            string.Equals(runLabel, SmokeLabel, StringComparison.Ordinal)
                ? Optional(commitName, "smoke-undeclared")
                : Required(commitName)
        );
    }

    /// <summary>A setting every evidence run must declare; a smoke run may leave it unset.</summary>
    public string Declared(string name) => IsSmoke ? Optional(name, "smoke-undeclared") : Required(name);

    public static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Set {name} before running a timing evidence collector.");

    private static string Optional(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    private static int Integer(string name, int fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? int.Parse(value, CultureInfo.InvariantCulture)
            : fallback;

    private static int PositiveInteger(string name, int fallback)
    {
        int value = Integer(name, fallback);
        return value > 0 ? value : throw new InvalidOperationException($"{name} must be greater than zero.");
    }
}

/// <summary>
/// Runs warm-up rounds and then measured rounds. In every round each arm sends exactly one request,
/// in an order drawn from a generator seeded by the run's seed, so slow drift and ordering effects
/// fall on every arm alike. Each round reads one timestamp pair, <c>round_started_qpc</c> and
/// <c>round_started_utc</c>, after its shuffle and before its first arm's timed span, so the stamp
/// lies outside every timed request and draws nothing from the generator.
///
/// Every row (<c>round,arm,position,elapsed_us,round_started_qpc,round_started_utc</c>) is kept in
/// memory until the last measured round has finished, and only then are the measured samples, the
/// warm-up samples (<c>{label}-warmup-{boundary}-{stamp}.csv</c>, a name the measured file's
/// <c>{label}-{boundary}-*</c> pattern cannot match) and the JSON header written; a rejected response
/// in either phase aborts the run before any file exists. It computes no statistics: the offline
/// analysis does. No file holds a secret, a token, a connection string or a request body.
/// </summary>
internal static class TimingEvidenceRecorder
{
    public const string Schema = "timing-evidence/2";
    public const string Columns = "round,arm,position,elapsed_us,round_started_qpc,round_started_utc";

    private static readonly JsonSerializerOptions _headerOptions = new() { WriteIndented = true };

    // Without a byte order mark, so the first CSV column is read as "round" by any reader.
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static Task<TimingRunFiles> RunAsync<TResponse>(
        TimingRunSettings settings,
        string boundary,
        IReadOnlyDictionary<string, string> context,
        IReadOnlyList<TimingArm<TResponse>> arms
    ) => RunAsync(settings, boundary, context, arms, TimingClock.System);

    public static async Task<TimingRunFiles> RunAsync<TResponse>(
        TimingRunSettings settings,
        string boundary,
        IReadOnlyDictionary<string, string> context,
        IReadOnlyList<TimingArm<TResponse>> arms,
        TimingClock clock
    )
    {
        Random order = new(settings.Seed);
        int[] positions = [.. Enumerable.Range(0, arms.Count)];
        DateTime startedUtc = clock.UtcNow();

        // Warm-up rounds are discarded. They absorb first-use costs: JIT, connection set-up, and the
        // token manager's placeholder generation on its first unknown-client request.
        // Their samples are kept apart, in the warm-up file, and never enter the analysis.
        List<string> warmupRows = new((settings.WarmupRounds * arms.Count) + 1) { Columns };
        await RunRoundsAsync(settings.WarmupRounds, order, positions, arms, clock, warmupRows);

        int[] collectionsBefore = Collections();
        List<string> rows = new((settings.Rounds * arms.Count) + 1) { Columns };
        await RunRoundsAsync(settings.Rounds, order, positions, arms, clock, rows);

        int[] collectionsAfter = Collections();
        DateTime finishedUtc = clock.UtcNow();

        Directory.CreateDirectory(settings.OutputDirectory);
        string stamp = startedUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string stem = $"{settings.RunLabel}-{boundary}-{stamp}";
        string samplesPath = Path.Combine(settings.OutputDirectory, $"{stem}.csv");
        string warmupPath = Path.Combine(
            settings.OutputDirectory,
            $"{settings.RunLabel}-warmup-{boundary}-{stamp}.csv"
        );
        string headerPath = Path.Combine(settings.OutputDirectory, $"{stem}.json");
        await File.WriteAllLinesAsync(warmupPath, warmupRows, _utf8);
        await File.WriteAllLinesAsync(samplesPath, rows, _utf8);

        Dictionary<string, object> header = new()
        {
            ["schema"] = Schema,
            ["boundary"] = boundary,
            ["run_label"] = settings.RunLabel,
            ["collector_commit"] = settings.Commit,
            ["seed"] = settings.Seed,
            ["warmup_rounds"] = settings.WarmupRounds,
            ["rounds"] = settings.Rounds,
            ["arms"] = arms.Select(arm => arm.Name).ToArray(),
            ["order"] = "each round: every arm once, order shuffled by System.Random(seed)",
            ["columns"] = Columns.Split(','),
            ["round_timestamp"] =
                "round_started_qpc (the timestamp clock, at stopwatch_frequency) and round_started_utc, read once per round after its shuffle and before its first arm's timed span",
            ["samples_file"] = Path.GetFileName(samplesPath),
            ["measured_rows"] = rows.Count - 1,
            ["warmup_file"] = Path.GetFileName(warmupPath),
            ["warmup_rows"] = warmupRows.Count - 1,
            ["started_utc"] = startedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["finished_utc"] = finishedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["processor_count"] = Environment.ProcessorCount,
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["stopwatch_frequency"] = clock.Frequency,
            ["gc_collections_before"] = collectionsBefore,
            ["gc_collections_after"] = collectionsAfter,
            ["context"] = context,
        };
        await File.WriteAllTextAsync(headerPath, JsonSerializer.Serialize(header, _headerOptions), _utf8);

        return new TimingRunFiles(samplesPath, warmupPath, headerPath, rows.Count - 1, warmupRows.Count - 1);
    }

    // One phase: per round, the shuffle, then the round's one timestamp pair, then every arm in turn.
    private static async Task RunRoundsAsync<TResponse>(
        int rounds,
        Random order,
        int[] positions,
        IReadOnlyList<TimingArm<TResponse>> arms,
        TimingClock clock,
        List<string> rows
    )
    {
        for (int round = 0; round < rounds; round++)
        {
            order.Shuffle(positions);
            long roundStartedQpc = clock.Timestamp();
            string roundStartedUtc = clock.UtcNow().ToString("O", CultureInfo.InvariantCulture);
            for (int slot = 0; slot < positions.Length; slot++)
            {
                TimingArm<TResponse> arm = arms[positions[slot]];
                double elapsedMicroseconds = await MeasureAsync(arm, clock);
                rows.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{round},{arm.Name},{slot},{elapsedMicroseconds:F3},{roundStartedQpc},{roundStartedUtc}"
                    )
                );
            }
        }
    }

    private static async Task<double> MeasureAsync<TResponse>(TimingArm<TResponse> arm, TimingClock clock)
    {
        long started = clock.Timestamp();
        TResponse response = await arm.Send();
        long elapsed = clock.Timestamp() - started;

        if (!arm.IsExpected(response))
        {
            throw new InvalidOperationException(
                $"Arm '{arm.Name}' received a response it does not stand for; the run is aborted."
            );
        }

        return elapsed * 1_000_000.0 / clock.Frequency;
    }

    private static int[] Collections() =>
        [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
}
