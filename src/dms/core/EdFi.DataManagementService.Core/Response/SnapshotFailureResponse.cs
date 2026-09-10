// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Response;

/// <summary>
/// The two responses a snapshot request can be refused with, constructed in exactly one place.
/// </summary>
/// <remarks>
/// <para>
/// Snapshot Not Found has more than one producer: the selection step answers a read for which no
/// snapshot is configured, and the connection-unavailable translation sites answer a read whose
/// selected snapshot could not be reached. Those are different failures at different points in the
/// pipeline, and a client must not be able to tell them apart - the response is identical in status,
/// body, and content type. Holding the construction here is what keeps them that way; producing it at
/// each site would let one drift.
/// </para>
/// <para>
/// Both responses carry <c>application/problem+json</c> and the shared DMS ProblemDetails envelope
/// with the request's correlation identifier, and neither assigns a target, so no database is opened
/// for either.
/// </para>
/// </remarks>
internal static class SnapshotFailureResponse
{
    /// <summary>
    /// The detail for Snapshot Not Found. Deliberately says nothing about which of the two causes
    /// produced it: whether a snapshot is configured, and whether a configured one is reachable, are
    /// both deployment facts a client has no business distinguishing.
    /// </summary>
    private const string NotFoundDetail = "Snapshot not found.";

    private const string ProblemJsonContentType = "application/problem+json";

    /// <summary>
    /// Snapshot Not Found: a snapshot-eligible read that has no usable snapshot to be served from.
    /// </summary>
    /// <remarks>
    /// The body is the shared not-found ProblemDetails rather than a snapshot-specific type, which is
    /// what the snapshot-support design calls for: only the detail names the snapshot.
    /// </remarks>
    public static IFrontendResponse NotFound(TraceId traceId) =>
        new FrontendResponse(
            StatusCode: 404,
            Body: FailureResponse.ForNotFound(NotFoundDetail, traceId),
            Headers: [],
            ContentType: ProblemJsonContentType
        );

    /// <summary>
    /// The snapshot method-not-allowed: a request that asked for a snapshot and would modify data.
    /// </summary>
    /// <remarks>
    /// <c>Allow: GET</c> states what is permitted in snapshot context, where the target is read-only,
    /// rather than what the route would permit on the primary - so it stays <c>GET</c> even on a route
    /// whose own set is <c>GET, POST</c> or <c>GET, PUT, DELETE</c>. It is deliberately not
    /// <c>ValidateRouteSemanticsMiddleware.AllowedMethodsFor</c>: that the two coincide on a partitions
    /// path is incidental, and reusing it would make the snapshot set follow route changes it has
    /// nothing to do with. On a partitions path the ProblemDetails fields and the content type are
    /// therefore the only things separating the two 405s.
    /// <para>
    /// A fresh header dictionary per call, because <see cref="FrontendResponse.Headers" /> is mutable
    /// and a shared instance would let one response's frontend edit another's.
    /// </para>
    /// </remarks>
    public static IFrontendResponse MethodNotAllowed(TraceId traceId) =>
        new FrontendResponse(
            StatusCode: 405,
            Body: FailureResponse.ForSnapshotMethodNotAllowed(traceId),
            // RFC 9110 section 15.5.6 requires Allow on a 405.
            Headers: new Dictionary<string, string> { ["Allow"] = "GET" },
            ContentType: ProblemJsonContentType
        );
}
