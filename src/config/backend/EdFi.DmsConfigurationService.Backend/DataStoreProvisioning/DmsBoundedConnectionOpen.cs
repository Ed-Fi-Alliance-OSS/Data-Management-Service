// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// An administrative connection open bounded on the engine's clock whatever connect timeout the connection string
/// carries: a value of <c>0</c> (Npgsql <c>Timeout</c>, SqlClient <c>Connect Timeout</c>) lets the driver wait for
/// a stalled login forever. It bounds the startup readiness probes' opens (7.3) and every SQL Server
/// administrative open (<c>MssqlAdminConnection</c>), because SqlClient does not honour cancellation while a login
/// stalls. Only the open is abandoned, never the work that follows it: the wait ends at the bound or at the
/// caller's cancellation even when the driver ignores both, and a connection the abandoned open still produces is
/// disposed when it arrives, before anything is sent on it. An expired bound is a <see cref="TimeoutException"/>;
/// the caller's cancellation propagates as <see cref="OperationCanceledException"/> on its own token.
/// </summary>
public static class DmsBoundedConnectionOpen
{
    public static async Task<TConnection> OpenAsync<TConnection>(
        Func<CancellationToken, Task<TConnection>> open,
        TimeSpan bound,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
        where TConnection : IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bound, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        using CancellationTokenSource deadline = new(bound, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token
        );
        Task<TConnection> opening = open(linked.Token);
        try
        {
            return await opening.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException exception) when (linked.IsCancellationRequested)
        {
            DisposeWhenOpened(opening);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                "The administrative connection was not opened within its bound.",
                exception
            );
        }
    }

    /// <summary>Disposes a connection the abandoned open still produces, and observes its failure otherwise.</summary>
    private static void DisposeWhenOpened<TConnection>(Task<TConnection> opening)
        where TConnection : IAsyncDisposable =>
        _ = opening.ContinueWith(
            static async completed =>
            {
                if (!completed.IsCompletedSuccessfully)
                {
                    // An abandoned open's failure is expected, and its message may quote the connection.
                    _ = completed.Exception;
                    return;
                }

                try
                {
                    await completed.Result.DisposeAsync();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Nothing is waiting on this connection; closing it is best effort.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
}
