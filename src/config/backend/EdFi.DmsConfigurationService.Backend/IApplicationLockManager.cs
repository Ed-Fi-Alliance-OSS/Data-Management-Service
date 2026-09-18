// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend;

/// <summary>
/// Serializes every workflow that mutates an existing identity-provider client belonging to an
/// Application aggregate. The lock is database-backed, so it coordinates all CMS instances that
/// share one CMS database; deployments pointing multiple CMS databases at a single identity
/// provider fall outside this guarantee. Acquisition must precede every read the workflow relies
/// on; the returned handle is the sole release point and must be disposed on every path.
/// </summary>
public interface IApplicationLockManager
{
    /// <summary>
    /// Acquires the exclusive lock for the given Application aggregate. Cancellation before or
    /// during acquisition propagates as <see cref="OperationCanceledException"/>; it is never
    /// converted to a timeout or failure result.
    /// </summary>
    Task<ApplicationLockResult> AcquireAsync(int applicationId, CancellationToken cancellationToken);

    /// <summary>
    /// Acquires the exclusive locks of every given Application aggregate on a single database
    /// session, so one workflow spanning many applications holds one connection rather than one
    /// per application. The ids are deduplicated and acquired in ascending order, the same total
    /// order every single-application caller follows, on the same lock resources, so a lock-set
    /// holder and a single-application holder contend with each other and no cycle between them
    /// is possible. The whole acquisition shares one
    /// <see cref="ApplicationLockOptions.AcquireTimeout"/> contention budget (see that member),
    /// so a set of N ids waits for one timeout in total, not N of them. A failure to acquire any
    /// lock in the set releases the locks already taken on the session before the failure result
    /// is returned. Disposing the handle releases the whole set. Cancellation propagates as
    /// <see cref="OperationCanceledException"/>. The set must not be empty.
    /// </summary>
    Task<ApplicationLockResult> AcquireAllAsync(
        IReadOnlyCollection<int> applicationIds,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// The connection pool the lock managers draw their sessions from. A lock session stays open for
/// as long as its workflow holds the lock, which spans identity-provider calls, so the sessions
/// are pooled separately from the repositories' connections: the pool is identified by its own
/// application name and bounded on its own, so lock holders and lock waiters can exhaust only
/// this pool and never starve the repository operations the same workflow, or any other request,
/// still has to perform.
/// </summary>
public static class ApplicationLockConnectionPool
{
    public const string ApplicationName = "EdFi.DmsConfigurationService.ApplicationLock";

    public const int MaxPoolSize = 20;

    /// <summary>
    /// Normalizes a requested lock set: distinct application ids in ascending order. Both
    /// managers acquire exactly this sequence.
    /// </summary>
    public static int[] OrderedDistinct(IReadOnlyCollection<int> applicationIds)
    {
        ArgumentNullException.ThrowIfNull(applicationIds);
        int[] ordered = [.. applicationIds.Distinct().Order()];
        if (ordered.Length == 0)
        {
            throw new ArgumentException("At least one application id is required.", nameof(applicationIds));
        }

        return ordered;
    }
}

public record ApplicationLockResult
{
    /// <summary>
    /// The lock is held. Disposing the handle releases it; disposal never throws, and a failed
    /// release evicts the underlying connection so a leaked lock cannot ride a pooled session.
    /// </summary>
    public record Acquired(IAsyncDisposable Handle) : ApplicationLockResult;

    /// <summary>
    /// Another session held the lock for the whole acquisition window.
    /// </summary>
    public record FailureTimeout() : ApplicationLockResult;

    /// <summary>
    /// Lock infrastructure failure (connection, command, or unexpected lock-service result).
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationLockResult;
}

public class ApplicationLockOptions
{
    /// <summary>
    /// How long one acquisition attempt may spend contending for the locks it needs. It is a
    /// contention budget, not a request deadline: the managers start it once the lock session is
    /// open, so establishing the connection is not charged to it, and they stop charging it once
    /// the whole set is held, so releasing the locks is not charged to it either. Within an
    /// attempt the budget is shared by every id in the set and is never restarted for a later
    /// one; an attempt that exhausts it reports <see cref="ApplicationLockResult.FailureTimeout"/>
    /// after releasing whatever it already holds.
    /// </summary>
    public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

public class ApplicationLockOptionsValidator : IValidateOptions<ApplicationLockOptions>
{
    private static readonly TimeSpan _maximumAcquireTimeout = TimeSpan.FromSeconds(60);

    public ValidateOptionsResult Validate(string? name, ApplicationLockOptions options)
    {
        if (options.AcquireTimeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                "ApplicationLockSettings:AcquireTimeout must be greater than zero."
            );
        }

        if (options.AcquireTimeout > _maximumAcquireTimeout)
        {
            return ValidateOptionsResult.Fail(
                "ApplicationLockSettings:AcquireTimeout must not exceed 60 seconds."
            );
        }

        return ValidateOptionsResult.Success;
    }
}
