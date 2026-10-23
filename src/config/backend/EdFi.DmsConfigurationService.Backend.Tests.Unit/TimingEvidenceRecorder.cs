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

/// <summary>The two files one run writes.</summary>
internal sealed record TimingRunFiles(string SamplesPath, string HeaderPath, int Samples);

/// <summary>
/// The settings of one run, read from <c>TIMING_EVIDENCE_*</c> environment variables and recorded
/// in the run's header, so every run can be repeated exactly.
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
    public static TimingRunSettings FromEnvironment() =>
        new(
            Required("TIMING_EVIDENCE_OUTPUT_DIR"),
            Optional("TIMING_EVIDENCE_RUN_LABEL", "pilot"),
            PositiveInteger("TIMING_EVIDENCE_WARMUP_ROUNDS", 50),
            PositiveInteger("TIMING_EVIDENCE_ROUNDS", 200),
            Integer("TIMING_EVIDENCE_SEED", 1),
            Optional("TIMING_EVIDENCE_COMMIT", "unrecorded")
        );

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
/// fall on every arm alike. Writes one CSV row per measured request (<c>round,arm,position,elapsed_us</c>)
/// and a JSON header describing the run. It computes no statistics: the offline analysis does.
/// Neither file holds a secret, a token, a connection string or a request body.
/// </summary>
internal static class TimingEvidenceRecorder
{
    private static readonly JsonSerializerOptions _headerOptions = new() { WriteIndented = true };

    // Without a byte order mark, so the first CSV column is read as "round" by any reader.
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<TimingRunFiles> RunAsync<TResponse>(
        TimingRunSettings settings,
        string boundary,
        IReadOnlyDictionary<string, string> context,
        IReadOnlyList<TimingArm<TResponse>> arms
    )
    {
        Random order = new(settings.Seed);
        int[] positions = [.. Enumerable.Range(0, arms.Count)];
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;

        // Warm-up rounds are discarded. They absorb first-use costs: JIT, connection set-up, and the
        // token manager's placeholder generation on its first unknown-client request.
        for (int round = 0; round < settings.WarmupRounds; round++)
        {
            order.Shuffle(positions);
            foreach (int position in positions)
            {
                _ = await MeasureAsync(arms[position]);
            }
        }

        int[] collectionsBefore = Collections();
        List<string> rows = new(settings.Rounds * arms.Count) { "round,arm,position,elapsed_us" };
        for (int round = 0; round < settings.Rounds; round++)
        {
            order.Shuffle(positions);
            for (int slot = 0; slot < positions.Length; slot++)
            {
                TimingArm<TResponse> arm = arms[positions[slot]];
                double elapsedMicroseconds = await MeasureAsync(arm);
                rows.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{round},{arm.Name},{slot},{elapsedMicroseconds:F3}"
                    )
                );
            }
        }

        int[] collectionsAfter = Collections();
        DateTimeOffset finishedUtc = DateTimeOffset.UtcNow;

        Directory.CreateDirectory(settings.OutputDirectory);
        string stem =
            $"{settings.RunLabel}-{boundary}-{startedUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
        string samplesPath = Path.Combine(settings.OutputDirectory, $"{stem}.csv");
        string headerPath = Path.Combine(settings.OutputDirectory, $"{stem}.json");
        await File.WriteAllLinesAsync(samplesPath, rows, _utf8);

        Dictionary<string, object> header = new()
        {
            ["schema"] = "timing-evidence/1",
            ["boundary"] = boundary,
            ["run_label"] = settings.RunLabel,
            ["commit"] = settings.Commit,
            ["seed"] = settings.Seed,
            ["warmup_rounds"] = settings.WarmupRounds,
            ["rounds"] = settings.Rounds,
            ["arms"] = arms.Select(arm => arm.Name).ToArray(),
            ["order"] = "each round: every arm once, order shuffled by System.Random(seed)",
            ["samples_file"] = Path.GetFileName(samplesPath),
            ["started_utc"] = startedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["finished_utc"] = finishedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["processor_count"] = Environment.ProcessorCount,
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["stopwatch_frequency"] = Stopwatch.Frequency,
            ["gc_collections_before"] = collectionsBefore,
            ["gc_collections_after"] = collectionsAfter,
            ["context"] = context,
        };
        await File.WriteAllTextAsync(headerPath, JsonSerializer.Serialize(header, _headerOptions), _utf8);

        return new TimingRunFiles(samplesPath, headerPath, rows.Count - 1);
    }

    private static async Task<double> MeasureAsync<TResponse>(TimingArm<TResponse> arm)
    {
        long started = Stopwatch.GetTimestamp();
        TResponse response = await arm.Send();
        long elapsed = Stopwatch.GetTimestamp() - started;

        if (!arm.IsExpected(response))
        {
            throw new InvalidOperationException(
                $"Arm '{arm.Name}' received a response it does not stand for; the run is aborted."
            );
        }

        return elapsed * 1_000_000.0 / Stopwatch.Frequency;
    }

    private static int[] Collections() =>
        [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
}
