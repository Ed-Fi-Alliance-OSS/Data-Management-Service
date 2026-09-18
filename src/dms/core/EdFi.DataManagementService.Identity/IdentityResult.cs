// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Identity;

/// <summary>
/// The outcome of an identity operation that always resolves synchronously to a definitive answer:
/// <c>CreateAsync</c>, <c>GetByIdAsync</c>, and <c>ResultsAsync</c>.
/// This record carries no request token. Only <c>FindAsync</c> and <c>SearchAsync</c> can begin an
/// asynchronous job and hand back a token to poll, so a token is structurally impossible from create,
/// get-by-id, and results; see <see cref="IdentityAsyncResult.RequestToken"/> for the type that
/// carries one.
/// </summary>
public sealed record IdentityResult
{
    /// <summary>
    /// The result status. <c>Incomplete</c> is valid only from <c>ResultsAsync</c>, where it reports
    /// that a previously accepted asynchronous job is still running; a provider returning it from
    /// <c>CreateAsync</c> or <c>GetByIdAsync</c> is provider contract misuse. <c>JobFailed</c> is also
    /// valid only from <c>ResultsAsync</c>, reporting a definitively failed accepted job rather than a
    /// failure to retrieve the job's state; DMS ignores any payload or errors supplied alongside it and
    /// instead emits its own fixed, sanitized terminal problem. A failure to obtain an answer at all -
    /// as opposed to a definitive status - is signaled by throwing rather than by any status here; DMS
    /// logs the exception and returns a sanitized upstream-failure problem with no provider detail in
    /// the client response.
    /// </summary>
    public required IdentityResultStatus Status { get; init; }

    /// <summary>
    /// The result payload, or null when the status carries none. DMS performs no runtime validation
    /// of this payload; a provider is responsible for the shape and correctness of what it returns.
    /// </summary>
    public JsonNode? Payload { get; init; }

    /// <summary>
    /// The failures reported for an <see cref="IdentityResultStatus.InvalidProperties"/> result.
    /// DMS projects entries only for that status; entries returned alongside any other status are
    /// ignored. Defaults to an empty list.
    /// </summary>
    public IReadOnlyList<IdentityError> Errors { get; init; } = [];
}
