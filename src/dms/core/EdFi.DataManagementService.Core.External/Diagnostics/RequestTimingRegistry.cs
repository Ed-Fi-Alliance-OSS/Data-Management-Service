// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

namespace EdFi.DataManagementService.Core.External.Diagnostics;

/// <summary>Aggregated statistics for one phase, produced by <see cref="RequestTimingRegistry.Snapshot"/>.</summary>
public sealed record PhaseStats(
    string Phase,
    long Count,
    double TotalMs,
    double MeanMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs
);

/// <summary>Point-in-time export of every phase accumulator plus request-level counters.</summary>
public sealed record RegistrySnapshot(
    DateTimeOffset CapturedAtUtc,
    double WindowSeconds,
    long Requests,
    int InFlightRequests,
    IReadOnlyList<PhaseStats> Phases
);

/// <summary>
/// Process-wide, lock-free aggregation of phase timings for the DMS-1236 performance
/// investigation. Each phase keeps count/total/max plus a power-of-two microsecond
/// histogram for percentile estimation. Snapshots are served by the /instrumentation
/// endpoint and the periodic summary logger.
/// </summary>
public static class RequestTimingRegistry
{
    private const int MaxPhaseKeys = 3000;
    private const string OverflowKey = "~overflow";

    private static readonly ConcurrentDictionary<string, PhaseAccumulator> _phases = new();
    private static long _requests;
    private static int _inFlight;
    private static long _windowStartTimestamp = Stopwatch.GetTimestamp();
    private static long _detailSampleCounter;

    /// <summary>Adds one observation of <paramref name="milliseconds"/> to the named phase.</summary>
    public static void Observe(string phase, double milliseconds)
    {
        GetAccumulator(phase).AddObservation(milliseconds);
    }

    public static void IncrementInFlight()
    {
        Interlocked.Increment(ref _inFlight);
    }

    public static void DecrementInFlight()
    {
        Interlocked.Decrement(ref _inFlight);
    }

    /// <summary>
    /// Increments the shared detail-sampling counter and returns true when this request
    /// should emit a detailed phase breakdown (every Nth request).
    /// </summary>
    public static bool ShouldSampleDetail(int everyN)
    {
        return everyN > 0 && Interlocked.Increment(ref _detailSampleCounter) % everyN == 0;
    }

    /// <summary>
    /// Folds a completed request into the registry: the request total (globally and per
    /// endpoint), every recorded phase (pipeline steps folded as SELF time, with the
    /// nested next-step time subtracted), and the derived in-transaction idle gap.
    /// </summary>
    public static void FoldRequest(RequestTiming timing)
    {
        Interlocked.Increment(ref _requests);

        IReadOnlyList<PhaseSample> samples = timing.SnapshotSamples();
        Dictionary<int, double> stepDurations = CollectStepDurations(timing.Pipeline, samples);

        double sessionMs = 0;
        double inTxnComponentsMs = 0;

        foreach (PhaseSample sample in samples)
        {
            double duration = sample.DurationMs;

            int stepIndex = ParseStepIndex(timing.Pipeline, sample.Phase);
            if (stepIndex >= 0)
            {
                // Pipeline steps nest strictly (each step's time includes all later steps),
                // so self time is the inclusive time minus the next step's inclusive time.
                double childMs = stepDurations.GetValueOrDefault(stepIndex + 1);
                Observe(sample.Phase, Math.Max(0, duration - childMs));
                continue;
            }

            Observe(sample.Phase, duration);

            switch (sample.Phase)
            {
                case DbPhases.Session:
                    sessionMs += duration;
                    break;
                case DbPhases.BeginTransaction:
                case DbPhases.Commit:
                case DbPhases.Rollback:
                case DbPhases.CommandInTxn:
                    inTxnComponentsMs += duration;
                    break;
                default:
                    break;
            }
        }

        Observe("Http.Total", timing.TotalMs);
        Observe($"Total.{timing.Method}.{timing.Resource}", timing.TotalMs);

        if (sessionMs > 0)
        {
            // Time the DB transaction sat open while no statement/commit was executing:
            // the app-side round-trip overhead the DB sees as idle-in-transaction.
            Observe(DbPhases.InTxnGap, Math.Max(0, sessionMs - inTxnComponentsMs));
        }
    }

    /// <summary>Produces an immutable snapshot of all phases, ordered by total time descending.</summary>
    public static RegistrySnapshot Snapshot()
    {
        List<PhaseStats> stats = new(_phases.Count);
        foreach (KeyValuePair<string, PhaseAccumulator> entry in _phases)
        {
            stats.Add(entry.Value.ToStats(entry.Key));
        }

        stats.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));

        double windowSeconds =
            (Stopwatch.GetTimestamp() - Volatile.Read(ref _windowStartTimestamp))
            / (double)Stopwatch.Frequency;

        return new RegistrySnapshot(
            DateTimeOffset.UtcNow,
            Math.Round(windowSeconds, 1),
            Interlocked.Read(ref _requests),
            Volatile.Read(ref _inFlight),
            stats
        );
    }

    /// <summary>Clears all accumulators and returns the final snapshot of the closed window.</summary>
    public static RegistrySnapshot Reset()
    {
        RegistrySnapshot snapshot = Snapshot();
        _phases.Clear();
        Interlocked.Exchange(ref _requests, 0);
        Volatile.Write(ref _windowStartTimestamp, Stopwatch.GetTimestamp());
        return snapshot;
    }

    private static PhaseAccumulator GetAccumulator(string phase)
    {
        if (_phases.TryGetValue(phase, out PhaseAccumulator? existing))
        {
            return existing;
        }

        // Bound the key space so unexpected high-cardinality phase names (e.g. unusual
        // SQL texts) cannot grow memory without limit.
        string key = _phases.Count >= MaxPhaseKeys ? OverflowKey : phase;
        return _phases.GetOrAdd(key, static _ => new PhaseAccumulator());
    }

    private static Dictionary<int, double> CollectStepDurations(
        string pipeline,
        IReadOnlyList<PhaseSample> samples
    )
    {
        Dictionary<int, double> durations = [];
        for (int i = 0; i < samples.Count; i++)
        {
            int stepIndex = ParseStepIndex(pipeline, samples[i].Phase);
            if (stepIndex >= 0)
            {
                durations[stepIndex] = samples[i].DurationMs;
            }
        }

        return durations;
    }

    /// <summary>
    /// Pipeline step phases are named "{pipeline}.{NN}.{StepName}". Returns NN, or -1
    /// when the phase is not a step of the given pipeline.
    /// </summary>
    private static int ParseStepIndex(string pipeline, string phase)
    {
        if (pipeline.Length == 0 || !phase.StartsWith(pipeline, StringComparison.Ordinal))
        {
            return -1;
        }

        int digitsStart = pipeline.Length + 1;
        if (
            phase.Length <= digitsStart + 2
            || phase[pipeline.Length] != '.'
            || phase[digitsStart + 2] != '.'
        )
        {
            return -1;
        }

        char tens = phase[digitsStart];
        char ones = phase[digitsStart + 1];
        if (!char.IsAsciiDigit(tens) || !char.IsAsciiDigit(ones))
        {
            return -1;
        }

        return ((tens - '0') * 10) + (ones - '0');
    }

    /// <summary>Phase names shared between the backend instrumentation and the fold logic.</summary>
    public static class DbPhases
    {
        public const string Session = "Db.Session";
        public const string OpenConnection = "Db.OpenConnection";
        public const string BeginTransaction = "Db.BeginTransaction";
        public const string Commit = "Db.Commit";
        public const string Rollback = "Db.Rollback";
        public const string Command = "Db.Command";
        public const string CommandInTxn = "Db.Command.InTxn";
        public const string InTxnGap = "Db.InTxnGap";
    }

    /// <summary>
    /// Lock-free accumulator: count, sum, max (all in whole microseconds) plus a
    /// power-of-two histogram for percentile estimation.
    /// </summary>
    private sealed class PhaseAccumulator
    {
        private const int BucketCount = 28;

        private readonly long[] _buckets = new long[BucketCount];
        private long _count;
        private long _sumMicros;
        private long _maxMicros;

        public void AddObservation(double milliseconds)
        {
            long micros = (long)(milliseconds * 1000.0);
            if (micros < 0)
            {
                micros = 0;
            }

            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _sumMicros, micros);
            UpdateMax(micros);

            int bucket = micros <= 0 ? 0 : Math.Min(BucketCount - 1, BitOperations.Log2((ulong)micros));
            Interlocked.Increment(ref _buckets[bucket]);
        }

        public PhaseStats ToStats(string phase)
        {
            long count = Interlocked.Read(ref _count);
            double totalMs = Interlocked.Read(ref _sumMicros) / 1000.0;
            double maxMs = Interlocked.Read(ref _maxMicros) / 1000.0;

            return new PhaseStats(
                phase,
                count,
                Math.Round(totalMs, 3),
                count == 0 ? 0 : Math.Round(totalMs / count, 4),
                Percentile(count, 0.50),
                Percentile(count, 0.95),
                Percentile(count, 0.99),
                Math.Round(maxMs, 3)
            );
        }

        private void UpdateMax(long micros)
        {
            long observedMax = Interlocked.Read(ref _maxMicros);
            while (micros > observedMax)
            {
                long previous = Interlocked.CompareExchange(ref _maxMicros, micros, observedMax);
                if (previous == observedMax)
                {
                    return;
                }

                observedMax = previous;
            }
        }

        /// <summary>
        /// Estimates a percentile from the histogram; the returned value is the geometric
        /// midpoint of the bucket containing the requested rank (~±20% within a bucket).
        /// </summary>
        private double Percentile(long count, double quantile)
        {
            if (count == 0)
            {
                return 0;
            }

            long rank = (long)Math.Ceiling(count * quantile);
            long cumulative = 0;
            for (int i = 0; i < BucketCount; i++)
            {
                cumulative += Interlocked.Read(ref _buckets[i]);
                if (cumulative >= rank)
                {
                    double bucketMidMicros = Math.Pow(2, i) * 1.5;
                    return Math.Round(bucketMidMicros / 1000.0, 4);
                }
            }

            return Math.Round(Interlocked.Read(ref _maxMicros) / 1000.0, 3);
        }
    }
}
