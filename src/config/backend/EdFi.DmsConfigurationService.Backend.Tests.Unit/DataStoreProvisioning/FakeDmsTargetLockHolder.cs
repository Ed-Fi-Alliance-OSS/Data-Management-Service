// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>The one exception type the fake holder classifies as a connection-class failure.</summary>
public sealed class TestConnectionException : Exception;

/// <summary>
/// A holder over no connection: the base class supplies the gate, heartbeat, loss, and disposal under
/// test, and the fake supplies a scripted probe, a scripted broken flag, a counted release, and a way to
/// declare loss directly, as an engine's broken-state notification would.
/// </summary>
internal sealed class FakeDmsTargetLockHolder(
    DmsServerSessionId session,
    TimeSpan heartbeatInterval,
    TimeSpan probeDeadline,
    TimeProvider timeProvider
) : DmsTargetLockHolder(session, heartbeatInterval, probeDeadline, timeProvider, NullLogger.Instance)
{
    private int _releaseCount;

    public Func<CancellationToken, Task<bool>> Probe { get; set; } = _ => Task.FromResult(true);

    public bool Broken { get; set; }

    public bool ReleaseThrows { get; set; }

    public int ReleaseCount => Volatile.Read(ref _releaseCount);

    public void Lose() => DeclareLost();

    protected override Task<bool> ProbeAsync(CancellationToken cancellationToken) => Probe(cancellationToken);

    protected override bool IsConnectionFailure(Exception exception) => exception is TestConnectionException;

    protected override bool IsConnectionBroken => Broken;

    protected override ValueTask ReleaseAsync()
    {
        Interlocked.Increment(ref _releaseCount);
        if (ReleaseThrows)
        {
            throw new InvalidOperationException("The fake release failed.");
        }

        return ValueTask.CompletedTask;
    }
}
