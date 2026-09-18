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
    /// The result payload. DMS performs no runtime validation of it, but its shape is defined rather
    /// than provider-chosen; each operation and status below names the shape it must carry, built from
    /// the standard properties documented on <see cref="IIdentityService"/>.
    /// <list type="bullet">
    /// <item>
    /// <c>CreateAsync</c> <see cref="IdentityResultStatus.Success"/> carries a JSON string holding the
    /// newly issued UniqueId - a bare string, not an object wrapping one.
    /// </item>
    /// <item>
    /// <c>GetByIdAsync</c> <see cref="IdentityResultStatus.Success"/> carries a single
    /// <c>IdentityResponse</c> object for the matched identity.
    /// </item>
    /// <item>
    /// <c>ResultsAsync</c> carries an <c>IdentitySearchResponse</c> object, whose shape is documented on
    /// <see cref="IdentityAsyncResult.Payload"/>, with wire <c>Status: "Complete"</c> for
    /// <see cref="IdentityResultStatus.Success"/> and wire <c>Status: "Incomplete"</c> for
    /// <see cref="IdentityResultStatus.Incomplete"/>. The object is required even for an incomplete
    /// poll; it simply carries no result data yet and may omit <c>SearchResponses</c> or send it empty.
    /// </item>
    /// </list>
    /// Null is correct only for a status that carries no payload:
    /// <see cref="IdentityResultStatus.NotFound"/>, <see cref="IdentityResultStatus.InvalidProperties"/>,
    /// and <see cref="IdentityResultStatus.JobFailed"/>. A <see cref="IdentityResultStatus.Success"/> or
    /// <see cref="IdentityResultStatus.Incomplete"/> with no payload is provider contract misuse and
    /// becomes a provider-contract-violation <c>502</c>. A payload present but shaped differently is
    /// served to the client verbatim, so it is a provider defect the host does not detect on its behalf.
    /// </summary>
    public JsonNode? Payload { get; init; }

    /// <summary>
    /// The failures reported for an <see cref="IdentityResultStatus.InvalidProperties"/> result.
    /// DMS projects entries only for that status; entries returned alongside any other status are
    /// ignored. Defaults to an empty list.
    /// </summary>
    public IReadOnlyList<IdentityError> Errors { get; init; } = [];
}
