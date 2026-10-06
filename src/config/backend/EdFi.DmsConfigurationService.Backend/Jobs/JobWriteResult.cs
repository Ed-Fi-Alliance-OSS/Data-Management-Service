// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The outcome of an ownership-dependent write (spec D-4): <c>Renew</c>, <c>Complete</c>,
/// <c>FailTransient</c>, <c>FailTerminal</c>, or <c>ReleaseToPending</c>.
/// </summary>
public record JobWriteResult
{
    /// <summary>
    /// The write committed. <paramref name="NewLeaseExpiresAt"/> is set for a renewal.
    /// </summary>
    public record Success(DateTime? NewLeaseExpiresAt, DateTime DatabaseUtcNow) : JobWriteResult;

    /// <summary>
    /// The guarded write matched no row: the lease expired, was reclaimed, or the token is stale.
    /// </summary>
    public record OwnershipLost() : JobWriteResult;

    /// <summary>
    /// The write may or may not have committed (a timeout or connection loss after it was sent). The caller
    /// treats this as ownership uncertainty and never retries the write (spec D-7a, Q15).
    /// </summary>
    public record ResultUnknown(JobFailureDiagnostic Diagnostic) : JobWriteResult;

    /// <summary>
    /// The write did not happen: for example a lock timeout, or the operation deadline reached before the
    /// guarded statement was sent.
    /// </summary>
    public record FailureUnknown(JobFailureDiagnostic Diagnostic) : JobWriteResult;
}
