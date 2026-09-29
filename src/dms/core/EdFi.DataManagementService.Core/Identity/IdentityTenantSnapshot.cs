// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Identity;

/// <summary>
/// Process-wide, singleton coordinator for <see cref="Middleware.ValidateTenantExistsMiddleware" />.
/// Holds one immutable, case-insensitive tenant-name snapshot fed
/// by <see cref="IDataStoreProvider.LoadTenants" />, fresh for 60 seconds after a successful refresh
/// completes. A cold or expired caller, or one whose tenant is missing from a fresh snapshot, joins one
/// shared refresh regardless of the tenant name it asked about, so a tenant created in the
/// Configuration Service is recognized within seconds of being requested. A miss refreshes a fresh
/// snapshot at most once per 5 seconds, so repeated unknown names cannot drive back-to-back full-list
/// fetches; the refresh runs on its own <see cref="CancellationTokenSource" /> linked to
/// <see cref="IHostApplicationLifetime.ApplicationStopping" /> with a 30-second budget, never on a
/// caller's own token, so one caller leaving never aborts a fill another caller still needs. A failed
/// refresh answers <see cref="TenantExistenceOutcome.Unavailable" /> to every live waiter and refuses
/// to start a new refresh for 5 seconds, matching the precedent double-checked-refresh shape in
/// <see cref="ConfigurationServiceDataStoreProvider.RefreshInstancesIfExpiredAsync" /> and the
/// atomic snapshot swap in <see cref="DocumentCache.DocumentCacheTargetRegistry.RefreshAsync" />.
/// Captures no request-scoped service.
/// </summary>
internal sealed class IdentityTenantSnapshot(
    IDataStoreProvider dataStoreProvider,
    TimeProvider timeProvider,
    IHostApplicationLifetime hostApplicationLifetime,
    ILogger<IdentityTenantSnapshot> logger
)
{
    /// <summary>How long a successful snapshot answers presence/absence without a new fetch.</summary>
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(60);

    /// <summary>Maximum duration a shared refresh may run before it is treated as unavailable.</summary>
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a failed refresh blocks a new refresh attempt.</summary>
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(5);

    /// <summary>How long after a successful refresh a miss on the fresh snapshot answers without another fetch.</summary>
    private static readonly TimeSpan MissRefreshInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The current immutable snapshot, or null before the first successful refresh. Replaced
    /// atomically, and only on success, via <see cref="Volatile.Write{T}" />.
    /// </summary>
    private Snapshot? _snapshot;

    /// <summary>
    /// The one refresh currently in flight, or the most recently completed one. <c>true</c> once
    /// awaited means the refresh succeeded and <see cref="_snapshot" /> was replaced; <c>false</c>
    /// means it failed and <see cref="_lastFailure" /> was recorded. The refresh never throws - every
    /// failure, including its own timeout or a host-shutdown cancellation, is caught and folded into
    /// this boolean so a joining caller's <see cref="Task.WaitAsync(CancellationToken)" /> can only
    /// ever observe that caller's own cancellation.
    /// </summary>
    private Task<bool>? _refreshTask;

    /// <summary>When the most recent refresh failure was recorded, or null if none is outstanding.</summary>
    private FailureRecord? _lastFailure;

    /// <summary>Guards starting a new refresh so only one is ever created per cold or expired window.</summary>
    private readonly object _refreshGate = new();

    /// <summary>
    /// Answers whether <paramref name="tenant" /> exists, using a fresh snapshot that contains it
    /// immediately or joining a shared refresh otherwise. A tenant missing from a fresh snapshot is
    /// answered from the refreshed snapshot, or from the still-fresh one when that refresh fails, the
    /// failure cooldown suppresses it, or the snapshot is younger than the miss refresh interval. An <see cref="OperationCanceledException" /> raised because
    /// <paramref name="requestToken" /> itself was cancelled propagates untouched; every other failure
    /// - including the refresh's own timeout or a host-shutdown cancellation - is reported as
    /// <see cref="TenantExistenceOutcome.Unavailable" />.
    /// </summary>
    public async Task<TenantExistenceOutcome> CheckAsync(string tenant, CancellationToken requestToken)
    {
        requestToken.ThrowIfCancellationRequested();

        Snapshot? observed = Volatile.Read(ref _snapshot);
        if (observed is not null && IsFresh(observed) && observed.TenantNames.Contains(tenant))
        {
            return TenantExistenceOutcome.Exists;
        }

        Task<bool> refreshTask = GetOrStartRefreshTask(observed);

        // RunRefreshAsync never throws - the only OperationCanceledException WaitAsync can raise
        // here is for the caller's own requestToken, so it is left unhandled and propagates.
        bool refreshSucceeded = await refreshTask.WaitAsync(requestToken).ConfigureAwait(false);

        Snapshot? latest = Volatile.Read(ref _snapshot);
        if (latest is not null && (refreshSucceeded || IsFresh(latest)))
        {
            return Contains(latest, tenant);
        }

        return TenantExistenceOutcome.Unavailable;
    }

    private bool IsFresh(Snapshot snapshot) => timeProvider.GetUtcNow() - snapshot.LoadedAt < FreshnessWindow;

    private static TenantExistenceOutcome Contains(Snapshot snapshot, string tenant) =>
        snapshot.TenantNames.Contains(tenant) ? TenantExistenceOutcome.Exists : TenantExistenceOutcome.Absent;

    /// <summary>
    /// Returns the one shared in-flight refresh, joining an existing one when present. Reuses a
    /// recently failed refresh unchanged while it is still inside its 5-second cooldown so a joining
    /// caller neither starts a new <see cref="IDataStoreProvider.LoadTenants" /> call nor waits any
    /// longer than the original refresh already has. Reuses a completed successful refresh when it
    /// replaced the <paramref name="observed" /> snapshot after the caller read it, so a caller that
    /// raced that refresh does not start another, and while the snapshot it produced is younger than
    /// the 5-second miss refresh interval, so a miss does not refetch a list that was just fetched.
    /// Otherwise starts a brand new refresh.
    /// </summary>
    private Task<bool> GetOrStartRefreshTask(Snapshot? observed)
    {
        lock (_refreshGate)
        {
            if (_refreshTask is { IsCompleted: false } inFlight)
            {
                return inFlight;
            }

            if (_refreshTask is { IsCompleted: true } completed)
            {
                if (completed.Result)
                {
                    // A caller that read a snapshot just before another refresh replaced it reuses
                    // that refresh rather than starting a redundant load, and a miss on a snapshot
                    // fetched moments ago answers from it rather than fetching the whole list again.
                    Snapshot? current = Volatile.Read(ref _snapshot);
                    if (
                        !ReferenceEquals(current, observed)
                        || (
                            current is not null
                            && timeProvider.GetUtcNow() - current.LoadedAt < MissRefreshInterval
                        )
                    )
                    {
                        return completed;
                    }
                }
                else
                {
                    FailureRecord? failure = Volatile.Read(ref _lastFailure);
                    if (failure is not null && timeProvider.GetUtcNow() - failure.FailedAt < FailureCooldown)
                    {
                        return completed;
                    }
                }
            }

            Task<bool> refresh = RunRefreshAsync();
            _refreshTask = refresh;
            return refresh;
        }
    }

    /// <summary>
    /// Fetches every tenant name and atomically replaces the snapshot on success. Runs on a
    /// <see cref="CancellationTokenSource" /> linked only to <see cref="IHostApplicationLifetime.ApplicationStopping" />,
    /// paired with a 30-second timer scheduled through <see cref="TimeProvider.CreateTimer" /> that cancels it -
    /// never any individual caller's token - so a wrapping <see cref="Task.WaitAsync(CancellationToken)" /> bounds
    /// the call even against an <see cref="IDataStoreProvider" /> implementation that does not itself observe
    /// cancellation. Every failure, including that timeout and a host-shutdown cancellation, is caught, logged,
    /// and recorded rather than rethrown, so the returned task always completes successfully with the outcome.
    /// </summary>
    private async Task<bool> RunRefreshAsync()
    {
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            hostApplicationLifetime.ApplicationStopping
        );
        using ITimer timeoutTimer = timeProvider.CreateTimer(
            CancelWithoutThrowing,
            linkedCts,
            RefreshTimeout,
            Timeout.InfiniteTimeSpan
        );

        try
        {
            IList<string> tenantNames = await dataStoreProvider
                .LoadTenants(linkedCts.Token)
                .WaitAsync(linkedCts.Token)
                .ConfigureAwait(false);

            FrozenSet<string> frozenTenantNames = tenantNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
            Volatile.Write(ref _snapshot, new Snapshot(frozenTenantNames, timeProvider.GetUtcNow()));
            Volatile.Write(ref _lastFailure, null);
            return true;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastFailure, new FailureRecord(timeProvider.GetUtcNow()));
            logger.LogError(
                ex,
                "Identity tenant snapshot refresh failed; reporting unavailable to live callers for {CooldownSeconds}s",
                FailureCooldown.TotalSeconds
            );
            return false;
        }
    }

    /// <summary>
    /// Cancels the refresh's linked token source when the 30-second budget elapses. The source may
    /// already be disposed by a refresh that completed just before the timer fired; that race is
    /// harmless and is swallowed rather than surfaced.
    /// </summary>
    private static void CancelWithoutThrowing(object? state)
    {
        try
        {
            ((CancellationTokenSource)state!).Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The refresh already completed and disposed its token source; nothing to cancel.
        }
    }

    /// <summary>An immutable tenant-name set and the instant its refresh completed successfully.</summary>
    private sealed record Snapshot(FrozenSet<string> TenantNames, DateTimeOffset LoadedAt);

    /// <summary>Reference-typed so it can be swapped atomically with <see cref="Volatile.Write{T}" />.</summary>
    private sealed record FailureRecord(DateTimeOffset FailedAt);
}
