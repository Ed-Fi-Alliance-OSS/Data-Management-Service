// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Frontend;
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
/// The two snapshot failure responses as the selection step produces them: Snapshot Not Found for a
/// read with no usable snapshot, and the snapshot-specific method-not-allowed for a request that would
/// modify one.
/// </summary>
/// <remarks>
/// Both bodies come from <see cref="SnapshotFailureResponse" />, which is also what the
/// connection-unavailable translation sites use, so a read refused here and a read whose selected
/// snapshot could not be reached are indistinguishable to a client.
/// </remarks>
internal sealed class DefaultEffectiveTargetSelectionResponseFactory
    : IEffectiveTargetSelectionResponseFactory
{
    public IFrontendResponse ForMissingSnapshot(RequestInfo requestInfo)
    {
        ArgumentNullException.ThrowIfNull(requestInfo);

        return SnapshotFailureResponse.NotFound(requestInfo.FrontendRequest.TraceId);
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
    /// Why the accompanying <c>Allow</c> is the snapshot-context set rather than
    /// <see cref="ValidateRouteSemanticsMiddleware.AllowedMethodsFor" /> is recorded on
    /// <see cref="SnapshotFailureResponse" />, alongside the header it produces.
    /// </remarks>
    public IFrontendResponse ForRejectedAsMutation(RequestInfo requestInfo)
    {
        ArgumentNullException.ThrowIfNull(requestInfo);

        return SnapshotFailureResponse.MethodNotAllowed(requestInfo.FrontendRequest.TraceId);
    }
}
