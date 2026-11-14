// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;

/// <summary>
/// The system clock with the restore bound in the test's hands (Step 5.1 review). Both engines arm
/// <c>RestoreTimeout</c> as a cancellation source on their <see cref="TimeProvider"/>, so the one-shot timer created
/// for exactly <see cref="RestoreTimeout"/> is held and fires only when the test calls <see cref="Expire"/>; every
/// other timer, the clock and the timestamps are the system's. A real restore held at a controlled point therefore
/// ends by the bound when the test says so, whatever the restore's speed. No other bound of either engine equals
/// <see cref="RestoreTimeout"/>.
/// </summary>
public sealed class ControlledRestoreDeadline : TimeProvider
{
    /// <summary>The restore bound of a held lane, deployment and engine alike.</summary>
    public static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(17);

    private readonly Lock _gate = new();
    private readonly List<HeldTimer> _armed = [];

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        if (dueTime != RestoreTimeout || period != Timeout.InfiniteTimeSpan)
        {
            return base.CreateTimer(callback, state, dueTime, period);
        }

        HeldTimer timer = new(callback, state);
        lock (_gate)
        {
            _armed.Add(timer);
        }

        return timer;
    }

    /// <summary>Fires every restore bound armed and not yet disposed, on the caller's thread; returns how many fired.</summary>
    public int Expire()
    {
        HeldTimer[] armed;
        lock (_gate)
        {
            armed = [.. _armed];
            _armed.Clear();
        }

        return armed.Count(timer => timer.Fire());
    }

    private sealed class HeldTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _done;

        public bool Fire()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0)
            {
                return false;
            }

            callback(state);
            return true;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            throw new NotSupportedException("A held restore bound is fired by the test, never rescheduled.");

        public void Dispose() => Interlocked.Exchange(ref _done, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
