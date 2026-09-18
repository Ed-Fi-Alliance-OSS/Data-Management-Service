// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Identity;

/// <summary>
/// The outcome of <c>FindAsync</c> or <c>SearchAsync</c>, the only two operations that may either
/// resolve synchronously or begin an asynchronous job and hand back a token to poll through
/// <c>ResultsAsync</c>. <c>CreateAsync</c> and <c>GetByIdAsync</c> always resolve synchronously and
/// return <see cref="IdentityResult"/>, which has no request-token member for that reason.
/// </summary>
public sealed record IdentityAsyncResult
{
    /// <summary>
    /// The result status. <c>FindAsync</c> and <c>SearchAsync</c> may return only <c>Success</c>,
    /// <c>InvalidProperties</c>, or <c>NotFound</c>; <c>Incomplete</c> and <c>JobFailed</c> are valid
    /// only from <c>ResultsAsync</c>, so a provider returning either one here is provider contract
    /// misuse. A failure to obtain an answer at all - as opposed to a definitive status - is signaled
    /// by throwing rather than by any status here; DMS logs the exception and returns a sanitized
    /// upstream-failure problem with no provider detail in the client response.
    /// </summary>
    public required IdentityResultStatus Status { get; init; }

    /// <summary>
    /// The result payload, or null when the status carries none. A synchronous <c>Success</c> carries
    /// a complete payload with wire <c>Status: "Complete"</c>; a pending job instead returns
    /// <c>Success</c> with a usable <see cref="RequestToken"/> and no payload here, because the result
    /// data does not exist yet. Wire <c>Status: "Incomplete"</c> is legal only when polling
    /// <c>ResultsAsync</c>, never as a synchronous find or search answer.
    /// <para>
    /// That complete payload is an <c>IdentitySearchResponse</c>: a JSON object whose properties are
    /// </para>
    /// <list type="bullet">
    /// <item><c>Status</c>, a required string that is either <c>"Complete"</c> or <c>"Incomplete"</c>.</item>
    /// <item>
    /// <c>SearchResponses</c>, an array the complete shape requires, holding exactly one entry per
    /// submitted UniqueId or search request, in request order. Only an incomplete results poll may omit
    /// it or send it empty.
    /// </item>
    /// <item>
    /// a required <c>Responses</c> array on each <c>SearchResponses</c> entry: zero or one
    /// <c>IdentityResponse</c> for a find, zero or more for a search, every search match carrying a
    /// numeric <c>Score</c>. A request that matched nothing is an entry with an empty <c>Responses</c>
    /// array - not <see cref="IdentityResultStatus.NotFound"/>, and not a dropped entry, because the
    /// entries are positional.
    /// </item>
    /// </list>
    /// DMS performs no runtime validation of this payload, but the shape above is what the deployment's
    /// OpenAPI document pins; see <see cref="IIdentityService"/> for the standard properties of an
    /// <c>IdentityResponse</c> and <see cref="IdentityResult.Payload"/> for the shapes the synchronous
    /// operations carry. A <see cref="IdentityResultStatus.Success"/> that is neither a complete payload
    /// nor a usable <see cref="RequestToken"/> is provider contract misuse and becomes a
    /// provider-contract-violation <c>502</c>.
    /// </summary>
    public JsonNode? Payload { get; init; }

    /// <summary>
    /// The token a client polls through <c>ResultsAsync</c> to retrieve a pending job's outcome, or
    /// null when the operation resolved synchronously. Only <c>FindAsync</c> and <c>SearchAsync</c>
    /// can begin an asynchronous job, which is why this member exists here and not on
    /// <see cref="IdentityResult"/>: a token is structurally impossible from create, get-by-id, and
    /// results.
    /// A token is meaningful only alongside <see cref="IdentityResultStatus.Success"/>; DMS ignores any
    /// token returned alongside <see cref="IdentityResultStatus.InvalidProperties"/> or
    /// <see cref="IdentityResultStatus.NotFound"/>, and returning
    /// <see cref="IdentityResultStatus.JobFailed"/> from find/search is misuse regardless of any token
    /// supplied with it.
    /// A usable token is not null, blank, or whitespace; contains no <c>/</c>, no <c>\</c>, and no
    /// control character; is not exactly <c>.</c> or <c>..</c>; compares ordinally equal to
    /// <c>Uri.UnescapeDataString(Uri.EscapeDataString(token))</c>; escapes to at most 1024 characters
    /// under <c>Uri.EscapeDataString</c>; and composes a poll path that fits the deployment's
    /// request-line budget once DMS appends the escaped token as the final segment of
    /// <c>GET /identity/v2/identities/results/{token}</c>. The 1024-character ceiling bounds the
    /// escaped form because escaping only ever expands a token, and the deployment's own path base,
    /// tenant segment, and route-qualifier segments are additional, provider-invisible budget the
    /// ceiling alone cannot account for. An unusable token is provider contract misuse, and DMS
    /// returns <c>502</c> with no <c>Location</c> header rather than accept it.
    /// </summary>
    public string? RequestToken { get; init; }

    /// <summary>
    /// The failures reported for an <see cref="IdentityResultStatus.InvalidProperties"/> result.
    /// DMS projects entries only for that status; entries returned alongside any other status are
    /// ignored. Defaults to an empty list.
    /// </summary>
    public IReadOnlyList<IdentityError> Errors { get; init; } = [];
}
