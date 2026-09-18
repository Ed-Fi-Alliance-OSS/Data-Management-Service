// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Identity;

/// <summary>
/// The outcome a provider reports for an identity operation.
/// <c>Incomplete</c> and <c>JobFailed</c> are valid only when returned from <c>ResultsAsync</c>;
/// a provider returning either from any other operation - <c>CreateAsync</c>, <c>GetByIdAsync</c>,
/// <c>FindAsync</c>, or <c>SearchAsync</c> - is provider contract misuse.
/// </summary>
public enum IdentityResultStatus
{
    /// <summary>
    /// The operation completed and produced a definitive answer.
    /// </summary>
    Success,

    /// <summary>
    /// The previously accepted asynchronous job is still running.
    /// Valid only from <c>ResultsAsync</c>; returning it from any other operation is provider
    /// contract misuse.
    /// </summary>
    Incomplete,

    /// <summary>
    /// The request data was invalid. <c>IdentityError</c> entries are projected only for this status.
    /// </summary>
    InvalidProperties,

    /// <summary>
    /// No matching identity, or no matching namespace, was found, including an unknown or
    /// unauthorized tenant/route-qualifier namespace.
    /// </summary>
    NotFound,

    /// <summary>
    /// The previously accepted asynchronous job definitively failed.
    /// This reports a known terminal job failure, not a failure to retrieve the job's state, so
    /// providers must throw instead when the job's state itself cannot be obtained.
    /// It requires no payload: DMS ignores any payload or errors supplied alongside it and instead
    /// emits its own fixed, sanitized terminal problem.
    /// Valid only from <c>ResultsAsync</c>; returning it from any other operation is provider
    /// contract misuse.
    /// </summary>
    JobFailed,
}
