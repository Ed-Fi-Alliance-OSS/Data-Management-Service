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
    Task<ApplicationLockResult> AcquireAsync(long applicationId, CancellationToken cancellationToken);
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
