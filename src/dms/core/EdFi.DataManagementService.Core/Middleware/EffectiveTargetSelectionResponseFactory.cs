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
/// an interface because the exact snapshot problem-detail bodies are owned by separate work: replacing
/// this one registration replaces both responses, and an implementation may also read
/// <see cref="RequestInfo.EffectiveTargetSelection"/> to tell the two cases apart itself.
/// </summary>
internal interface IEffectiveTargetSelectionResponseFactory
{
    /// <summary>A snapshot was requested on a read that allows one, and none is configured.</summary>
    IFrontendResponse ForMissingSnapshot(RequestInfo requestInfo);

    /// <summary>A snapshot was requested on a request that would modify data.</summary>
    IFrontendResponse ForRejectedAsMutation(RequestInfo requestInfo);
}

/// <summary>
/// The interim responses: the existing generic not-found and method-not-allowed bodies.
/// </summary>
/// <remarks>
/// Neither response is snapshot-specific yet, and the method-not-allowed one carries no Allow header,
/// because the allowed-method set and the problem-detail bodies for snapshot requests are defined
/// elsewhere. What is settled here, and what this story's tests pin, is the status code, the point in
/// the pipeline the decision is made, and that no target is assigned and no database is opened.
/// </remarks>
internal sealed class DefaultEffectiveTargetSelectionResponseFactory
    : IEffectiveTargetSelectionResponseFactory
{
    /// <summary>
    /// The snapshot-support design reuses the existing not-found response for a snapshot that is not
    /// configured, so this one is already in its final form.
    /// </summary>
    public IFrontendResponse ForMissingSnapshot(RequestInfo requestInfo)
    {
        ArgumentNullException.ThrowIfNull(requestInfo);

        return new FrontendResponse(
            StatusCode: 404,
            Body: FailureResponse.ForNotFound("Snapshot not found.", requestInfo.FrontendRequest.TraceId),
            Headers: []
        );
    }

    public IFrontendResponse ForRejectedAsMutation(RequestInfo requestInfo)
    {
        ArgumentNullException.ThrowIfNull(requestInfo);

        return new FrontendResponse(
            StatusCode: 405,
            Body: FailureResponse.ForMethodNotAllowed(
                ["A snapshot cannot be modified. Remove the Use-Snapshot header to modify current data."],
                requestInfo.FrontendRequest.TraceId
            ),
            Headers: [],
            ContentType: "application/json; charset=utf-8"
        );
    }
}
