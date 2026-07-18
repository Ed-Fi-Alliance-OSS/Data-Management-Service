// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;

namespace EdFi.DataManagementService.Core.External.Diagnostics;

/// <summary>
/// A single timed phase observed during a request: where it started relative to the
/// request start, how long it took, and an optional detail (e.g. a SQL statement tag).
/// </summary>
public readonly record struct PhaseSample(string Phase, double OffsetMs, double DurationMs, string? Detail);

/// <summary>
/// Per-request wall-clock phase recorder for the DMS-1236 performance investigation.
/// Created by the frontend logging middleware and flowed ambiently (via
/// <see cref="RequestTimingContext"/>) through core pipeline steps and backend database
/// calls, so no method signatures need to change. Thread-safe; the cost per recorded
/// phase is one Stopwatch timestamp read and one small list append under a lock.
/// </summary>
public sealed class RequestTiming(string method, string resource)
{
    private static readonly double _ticksToMs = 1000.0 / Stopwatch.Frequency;

    private readonly object _gate = new();
    private readonly List<PhaseSample> _samples = new(64);
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private int _dbSessionDepth;
    private double _totalMs;

    /// <summary>The HTTP method of the request being timed.</summary>
    public string Method { get; } = method;

    /// <summary>The normalized resource path (document ids replaced with a placeholder).</summary>
    public string Resource { get; } = resource;

    /// <summary>The core pipeline that handled the request (e.g. "GET.Query"), if any.</summary>
    public string Pipeline { get; private set; } = "";

    /// <summary>Total in-app duration, valid after <see cref="Stop"/> is called.</summary>
    public double TotalMs => _totalMs;

    /// <summary>True while a relational write session (DB transaction) is open on this request.</summary>
    public bool InDbSession => Volatile.Read(ref _dbSessionDepth) > 0;

    /// <summary>Returns the current Stopwatch timestamp; pass it back to <see cref="Record"/>.</summary>
    public static long Now()
    {
        return Stopwatch.GetTimestamp();
    }

    public void EnterDbSession()
    {
        Interlocked.Increment(ref _dbSessionDepth);
    }

    public void ExitDbSession()
    {
        Interlocked.Decrement(ref _dbSessionDepth);
    }

    public void SetPipeline(string pipelineName)
    {
        Pipeline = pipelineName;
    }

    /// <summary>
    /// Records a phase that started at <paramref name="startTimestamp"/> (a value from
    /// <see cref="Now"/>) and ends now.
    /// </summary>
    public void Record(string phase, long startTimestamp, string? detail = null)
    {
        long now = Stopwatch.GetTimestamp();
        Add(
            phase,
            (startTimestamp - _startTimestamp) * _ticksToMs,
            (now - startTimestamp) * _ticksToMs,
            detail
        );
    }

    /// <summary>
    /// Records a phase that just completed with a known duration (used for externally
    /// measured durations such as Npgsql activity spans).
    /// </summary>
    public void RecordCompleted(string phase, TimeSpan duration, string? detail = null)
    {
        long now = Stopwatch.GetTimestamp();
        double durationMs = duration.TotalMilliseconds;
        double offsetMs = ((now - _startTimestamp) * _ticksToMs) - durationMs;
        Add(phase, offsetMs, durationMs, detail);
    }

    /// <summary>Finalizes the total request duration.</summary>
    public void Stop()
    {
        _totalMs = (Stopwatch.GetTimestamp() - _startTimestamp) * _ticksToMs;
    }

    /// <summary>Returns a point-in-time copy of the recorded samples.</summary>
    public IReadOnlyList<PhaseSample> SnapshotSamples()
    {
        lock (_gate)
        {
            return [.. _samples];
        }
    }

    private void Add(string phase, double offsetMs, double durationMs, string? detail)
    {
        var sample = new PhaseSample(phase, offsetMs, durationMs, detail);
        lock (_gate)
        {
            _samples.Add(sample);
        }
    }
}

/// <summary>
/// Ambient holder for the current request's <see cref="RequestTiming"/>. Uses AsyncLocal
/// so the context flows through async/await from the frontend middleware into core
/// pipeline steps and backend database code without any signature changes.
/// </summary>
public static class RequestTimingContext
{
    private static readonly AsyncLocal<RequestTiming?> _current = new();

    /// <summary>
    /// Global master switch, set once at startup from <see cref="RequestTimingOptions"/>.
    /// When false, <see cref="Begin"/> is never called and all instrumentation call sites
    /// see a null <see cref="Current"/> (near-zero overhead).
    /// </summary>
    public static bool Enabled { get; private set; }

    public static RequestTiming? Current => _current.Value;

    public static void Enable()
    {
        Enabled = true;
    }

    /// <summary>Starts a new timing context for the current async flow.</summary>
    public static RequestTiming Begin(string method, string resource)
    {
        RequestTiming timing = new(method, resource);
        _current.Value = timing;
        return timing;
    }

    /// <summary>Clears the timing context for the current async flow.</summary>
    public static void End()
    {
        _current.Value = null;
    }
}
