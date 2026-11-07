// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The dedicated holder session that owns the target's session-level exclusive lock (D-12). Every
/// selected-target mutation is issued on this session, so a worker whose holder session dies cannot
/// mutate the target, and a contending worker cannot acquire the lock until the holder session is gone.
/// The physical connection is private to the holder and is never replaced or reopened: a broken holder
/// means the lock is gone, <see cref="LostToken"/> is cancelled, and a new lock requires a fresh
/// <see cref="IDmsTargetEngine.AcquireTargetLockAsync"/>. Disposal releases the lock, never throws, and
/// is safe after loss and when called more than once.
/// </summary>
public interface IDmsTargetLockHolder : IAsyncDisposable
{
    /// <summary>The holder session's server id, for the operator procedure and for proving it gone.</summary>
    DmsServerSessionId Session { get; }

    /// <summary>
    /// Cancelled when the holder session is proven gone: a connection-class failure of any gated command
    /// or heartbeat, or the connection reporting broken state. A heartbeat skipped because a command is
    /// active is never loss.
    /// </summary>
    CancellationToken LostToken { get; }

    /// <summary>
    /// The single execution gate for the holder session. Every command the engine issues on the holder
    /// (fence, unfence, termination, rename, drop, probes), the heartbeat, and disposal run through it,
    /// one at a time: the gate provides mutual exclusion only and no waiter order is guaranteed. The
    /// engine that created the holder runs <paramref name="command"/> against its private connection while
    /// the gate is held, under a token linked to <paramref name="deadline"/>, <paramref name="cancellationToken"/>,
    /// and <see cref="LostToken"/>. A command whose deadline expires is cancelled; the holder stays usable
    /// and no loss is declared unless the connection itself failed.
    /// </summary>
    Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> command,
        TimeSpan deadline,
        CancellationToken cancellationToken
    );
}
