// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Claims and ownership-dependent writes (spec D-3, D-4, D-6). Every write after a claim is authorized by
/// the owner and fencing token and a lease that is live by fresh database time. Steps 2.5 and 2.6 implement it.
/// </summary>
public interface IJobLeaseRepository
{
    /// <summary>Claims the eligible job with the lowest <c>(NextAttemptAt, Id)</c>, skipping locked rows.</summary>
    Task<JobClaimResult> ClaimNext(
        string owner,
        int leaseSeconds,
        int maxAttempts,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Terminates jobs at or over <paramref name="maxAttempts"/> with <paramref name="errorCode"/>, in
    /// committed batches of at most 1 000 rows, checking <paramref name="cancellationToken"/> between batches.
    /// </summary>
    Task<JobExhaustResult> Exhaust(
        int maxAttempts,
        JobErrorCode errorCode,
        CancellationToken cancellationToken
    );

    Task<JobWriteResult> Renew(
        long id,
        string owner,
        long fencingToken,
        int leaseSeconds,
        CancellationToken cancellationToken
    );

    Task<JobWriteResult> Complete(
        long id,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken
    );

    /// <summary>Returns the job to <c>Pending</c> with <c>NextAttemptAt = now + backoff</c>.</summary>
    Task<JobWriteResult> FailTransient(
        long id,
        string owner,
        long fencingToken,
        int backoffSeconds,
        CancellationToken cancellationToken
    );

    Task<JobWriteResult> FailTerminal(
        long id,
        string owner,
        long fencingToken,
        JobErrorCode errorCode,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns the job to <c>Pending</c> with <c>NextAttemptAt = now</c>, keeping the attempt it used.
    /// </summary>
    Task<JobWriteResult> ReleaseToPending(
        long id,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken
    );
}

public record JobExhaustResult
{
    public record Success(int ExhaustedCount) : JobExhaustResult;

    public record FailureUnknown(JobFailureDiagnostic Diagnostic) : JobExhaustResult;
}
