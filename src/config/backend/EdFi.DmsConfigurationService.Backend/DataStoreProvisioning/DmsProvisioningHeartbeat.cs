// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The holder session's heartbeat (D-12): a timer on the holder's <see cref="TimeProvider"/> that fires
/// every <c>LockHeartbeatInterval</c> and <b>tries</b> the execution gate without waiting. When a command
/// holds the gate the probe is skipped and nothing else happens: the active command's own completion or
/// connection-class failure is the liveness evidence, and a skipped probe is never loss. When the gate is
/// free the probe runs through <see cref="DmsTargetLockHolder.ProbeUnderGateAsync"/>, which alone decides
/// whether what it saw is loss. Ticks stop after loss and after the holder begins disposal.
/// </summary>
internal sealed class DmsProvisioningHeartbeat
{
    private readonly DmsTargetLockHolder _holder;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _probeDeadline;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lifecycle = new();
    private ITimer? _timer;
    private bool _started;
    private volatile bool _stopped;
    private int _skippedProbes;
    private int _completedProbes;

    public DmsProvisioningHeartbeat(
        DmsTargetLockHolder holder,
        TimeSpan interval,
        TimeSpan probeDeadline,
        TimeProvider timeProvider
    )
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(probeDeadline, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _holder = holder;
        _interval = interval;
        _probeDeadline = probeDeadline;
        _timeProvider = timeProvider;
    }

    /// <summary>Ticks that found a command holding the gate and did nothing (test evidence).</summary>
    public int SkippedProbes => Volatile.Read(ref _skippedProbes);

    /// <summary>Ticks whose probe ran to its own verdict, loss or not (test evidence).</summary>
    public int CompletedProbes => Volatile.Read(ref _completedProbes);

    /// <summary>Starts the timer; the first tick is due one interval from now. Allowed once.</summary>
    public void Start()
    {
        lock (_lifecycle)
        {
            if (_started || _stopped)
            {
                throw new InvalidOperationException("The holder heartbeat was already started or stopped.");
            }

            _started = true;
            _timer = _timeProvider.CreateTimer(OnTick, null, _interval, _interval);
        }
    }

    /// <summary>Stops the timer; safe when never started and when called more than once.</summary>
    public async ValueTask StopAsync()
    {
        ITimer? timer;
        lock (_lifecycle)
        {
            _stopped = true;
            timer = _timer;
            _timer = null;
        }

        if (timer is not null)
        {
            await timer.DisposeAsync();
        }
    }

    private void OnTick(object? state)
    {
        // TickAsync observes every exception itself, so the timer thread never faults and nothing awaits it.
        _ = TickAsync();
    }

    internal async Task TickAsync()
    {
        if (_stopped || _holder.IsLost)
        {
            return;
        }

        if (!_holder.TryEnterGate())
        {
            Interlocked.Increment(ref _skippedProbes);
            return;
        }

        try
        {
            await _holder.ProbeUnderGateAsync(_probeDeadline);
            Interlocked.Increment(ref _completedProbes);
        }
        finally
        {
            _holder.ExitGate();
        }
    }
}
