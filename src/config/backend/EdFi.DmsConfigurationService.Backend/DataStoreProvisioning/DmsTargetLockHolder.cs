// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The provider-neutral part of a holder session (D-12), which an engine's lock session derives from:
/// the single execution gate, the heartbeat schedule, loss declaration, and disposal. The engine supplies
/// what only its connection knows: the probe statement, the connection-failure classification, the
/// broken-state read, and the release. The gate is a <see cref="SemaphoreSlim"/> that provides mutual
/// exclusion only; no waiter order is assumed anywhere. Loss is declared only by a connection-class
/// failure of a gated command or probe, by the connection reporting broken state after one, by a probe
/// finding the lock no longer held by this session, or by the engine's own broken-state notification
/// through <see cref="DeclareLost"/>; a command whose deadline expires, and a heartbeat skipped because a
/// command is active, are never loss. The physical connection is never replaced: after loss every command
/// is refused with <see cref="OperationCanceledException"/> on <see cref="LostToken"/>, and a new lock
/// requires a fresh <see cref="IDmsTargetEngine.AcquireTargetLockAsync"/>. Disposal stops the heartbeat,
/// waits for the active command, releases through the gate exactly once, never throws, and is safe after
/// loss and when called more than once.
/// </summary>
public abstract class DmsTargetLockHolder : IDmsTargetLockHolder
{
    // Beside the orchestrator's LockLost (1438_11); 1438_20 to 1438_22 belong to the artifact catalog.
    internal static readonly EventId ReleaseFailed = new(1438_15, nameof(ReleaseFailed));
    internal static readonly EventId ProbeFailed = new(1438_16, nameof(ProbeFailed));

    // The gate is never disposed: a late waiter woken by the final release must find it usable, and it
    // holds no OS handle. The loss source is disposed last, after the lock is released; its token is cached
    // so LostToken stays readable afterwards, and a late loss declaration is ignored.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationToken _lostToken;
    private readonly TaskCompletionSource _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DmsProvisioningHeartbeat _heartbeat;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private int _disposeState;
    private volatile bool _released;

    /// <param name="session">The holder session's server id, captured at open.</param>
    /// <param name="heartbeatInterval"><c>LockHeartbeatInterval</c>.</param>
    /// <param name="probeDeadline">The bound for one probe, normally <c>CommandTimeout</c>.</param>
    /// <param name="timeProvider">Drives the heartbeat timer and every deadline.</param>
    /// <param name="logger">Receives the type chain of ignored release and probe errors; never a value.</param>
    protected DmsTargetLockHolder(
        DmsServerSessionId session,
        TimeSpan heartbeatInterval,
        TimeSpan probeDeadline,
        TimeProvider timeProvider,
        ILogger logger
    )
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        Session = session;
        _lostToken = _lost.Token;
        _timeProvider = timeProvider;
        _logger = logger;
        _heartbeat = new DmsProvisioningHeartbeat(this, heartbeatInterval, probeDeadline, timeProvider);
    }

    public DmsServerSessionId Session { get; }

    public CancellationToken LostToken => _lostToken;

    /// <summary>Whether loss has been declared.</summary>
    public bool IsLost => _lostToken.IsCancellationRequested;

    internal DmsProvisioningHeartbeat Heartbeat => _heartbeat;

    /// <summary>Starts the heartbeat timer. The engine calls it once, immediately after the lock is held.</summary>
    public void StartHeartbeat() => _heartbeat.Start();

    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> command,
        TimeSpan deadline,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
        ThrowIfUnusable();

        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            LostToken
        );
        await _gate.WaitAsync(waiting.Token);
        try
        {
            ThrowIfUnusable();
            using CancellationTokenSource deadlineSource = new(deadline, _timeProvider);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                LostToken,
                deadlineSource.Token
            );
            try
            {
                return await command(linked.Token);
            }
            catch (Exception exception) when (ClassifiesAsConnectionFailure(exception))
            {
                DeclareLost();
                throw;
            }
            finally
            {
                ObserveConnectionState();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            await _disposal.Task;
            return;
        }

        try
        {
            await _heartbeat.StopAsync();
            await _gate.WaitAsync();
            string? releaseFailure = null;
            try
            {
                _released = true;
                await ReleaseAsync();
            }
            catch (Exception exception)
            {
                // Only the type chain leaves the catch: a release error may quote the connection.
                releaseFailure = DmsProvisioningDiagnostic.TypeChain(exception);
            }
            finally
            {
                _gate.Release();
            }

            if (releaseFailure is not null)
            {
                _logger.LogWarning(
                    ReleaseFailed,
                    "The target lock holder session could not be released cleanly and was abandoned: {ExceptionTypeChain}",
                    releaseFailure
                );
            }

            _lost.Dispose();
        }
        finally
        {
            _disposal.TrySetResult();
        }
    }

    /// <summary>
    /// On the private connection, while the gate is held: the liveness statement and, where the engine
    /// can check it, whether this session still holds the lock. Returns false when the lock is no longer
    /// held; throws when the statement fails.
    /// </summary>
    protected abstract Task<bool> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="exception"/> proves the private connection failed (a connection-class error).</summary>
    protected abstract bool IsConnectionFailure(Exception exception);

    /// <summary>Whether the private connection reports broken state. Must not throw.</summary>
    protected abstract bool IsConnectionBroken { get; }

    /// <summary>
    /// Releases the lock and closes the private connection. Called exactly once, through the gate, at
    /// disposal, including after loss; may throw, and the base logs the type chain and continues.
    /// </summary>
    protected abstract ValueTask ReleaseAsync();

    /// <summary>
    /// Declares the holder session gone (D-12). Idempotent. The base calls it on the evidence listed on the
    /// class; an engine calls it when its connection reports broken state outside a gated command.
    /// </summary>
    protected void DeclareLost()
    {
        if (_lostToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _lost.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposal already released the lock; a loss declared after that changes nothing.
        }
    }

    internal bool TryEnterGate() => _gate.Wait(0);

    internal void ExitGate() => _gate.Release();

    /// <summary>The heartbeat's probe; the caller holds the gate. Decides alone what counts as loss.</summary>
    internal async Task ProbeUnderGateAsync(TimeSpan deadline)
    {
        if (IsLost || _released)
        {
            return;
        }

        using CancellationTokenSource deadlineSource = new(deadline, _timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            LostToken,
            deadlineSource.Token
        );
        string? probeFailure = null;
        try
        {
            if (!await ProbeAsync(linked.Token))
            {
                DeclareLost();
            }
        }
        catch (OperationCanceledException)
        {
            // The probe's deadline expired (or loss was declared meanwhile): a slow probe is not loss.
        }
        catch (Exception exception) when (ClassifiesAsConnectionFailure(exception))
        {
            DeclareLost();
        }
        catch (Exception exception)
        {
            // Only the type chain leaves the catch: a probe error may quote the connection.
            probeFailure = DmsProvisioningDiagnostic.TypeChain(exception);
        }
        finally
        {
            ObserveConnectionState();
        }

        if (probeFailure is not null)
        {
            _logger.LogWarning(
                ProbeFailed,
                "The target lock holder heartbeat probe failed with a non-connection error and was ignored: {ExceptionTypeChain}",
                probeFailure
            );
        }
    }

    private void ThrowIfUnusable()
    {
        LostToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_released, this);
    }

    private bool ClassifiesAsConnectionFailure(Exception exception)
    {
        try
        {
            return IsConnectionFailure(exception);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ObserveConnectionState()
    {
        if (IsConnectionBroken)
        {
            DeclareLost();
        }
    }
}
