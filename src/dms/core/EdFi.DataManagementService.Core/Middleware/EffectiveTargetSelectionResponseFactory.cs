// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Produces the response for a request that asked for a target it cannot be served from. Kept behind
/// an interface so both snapshot responses have a single producer for the selection step: replacing
/// this one registration replaces both.
/// </summary>
internal interface IEffectiveTargetSelectionResponseFactory
{
    /// <summary>A snapshot was requested on a read that allows one, and none is configured.</summary>
    IFrontendResponse ForMissingSnapshot(RequestInfo requestInfo);

    /// <summary>A snapshot was requested on a request that would modify data.</summary>
    IFrontendResponse ForRejectedAsMutation(RequestInfo requestInfo);
}

/// <summary>
/// The two snapshot failure responses: Snapshot Not Found for a read with no usable snapshot, and the
/// snapshot-specific method-not-allowed for a request that would modify one.
/// </summary>
/// <remarks>
/// Both carry the shared DMS ProblemDetails envelope, the request correlation identifier, and
/// <c>application/problem+json</c>. Neither assigns a target, so no database is opened for either.
/// </remarks>
internal sealed class DefaultEffectiveTargetSelectionResponseFactory
    : IEffectiveTargetSelectionResponseFactory
{
    /// <summary>
    /// The snapshot-support design reuses the existing not-found response for a snapshot that is not
    /// configured, so the body comes from the shared not-found factory rather than a snapshot-specific
    /// one; only the detail names the snapshot.
    /// </summary>
    public IFrontendResponse ForMissingSnapshot(RequestInfo requestInfo)
    {
        ArgumentNullException.ThrowIfNull(requestInfo);

        return new FrontendResponse(
            StatusCode: 404,
            Body: FailureResponse.ForNotFound("Snapshot not found.", requestInfo.FrontendRequest.TraceId),
            Headers: [],
            ContentType: "application/problem+json"
        );
    }

    /// <summary>
    /// The snapshot method-not-allowed response. It deliberately displaces the route-semantics 405 for
    /// a request carrying a parsed <c>Use-Snapshot: true</c>: selection runs ahead of
    /// <see cref="ValidateRouteSemanticsMiddleware" />, so an invalid mutation route - a collection
    /// DELETE or PUT, or an item POST - that asks for a snapshot is answered here, with this body,
    /// rather than with the route-construction body. The same route without the header, or with
    /// <c>false</c>, keeps the route-semantics response unchanged.
    /// </summary>
    /// <remarks>
    /// <c>Allow: GET</c> states what is permitted in snapshot context, where the target is read-only,
    /// rather than what the route would permit on the primary - so it stays GET even on a route whose
    /// own set is <c>GET, POST</c> or <c>GET, PUT, DELETE</c>. It is deliberately not
    /// <see cref="ValidateRouteSemanticsMiddleware.AllowedMethodsFor" />: that the two coincide on a
    /// partitions path is incidental, and reusing it would make the snapshot set follow route changes
    /// it has nothing to do with. On a partitions path the ProblemDetails fields and the content type
    /// are therefore the only things separating the two 405s.
    /// </remarks>
    public IFrontendResponse ForRejectedAsMutation(RequestInfo requestInfo)
    {
        ArgumentNullException.ThrowIfNull(requestInfo);

        return new FrontendResponse(
            StatusCode: 405,
            Body: FailureResponse.ForSnapshotMethodNotAllowed(requestInfo.FrontendRequest.TraceId),
            // RFC 9110 section 15.5.6 requires Allow on a 405.
            Headers: new Dictionary<string, string> { ["Allow"] = "GET" },
            ContentType: "application/problem+json"
        );
    }
}
