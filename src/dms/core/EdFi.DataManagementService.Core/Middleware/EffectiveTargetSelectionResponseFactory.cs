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
/// an interface because the exact snapshot problem-detail bodies are owned by separate work:
/// replacing this one registration replaces both responses.
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
/// The method-not-allowed response is deliberately generic - the same wording the terminal
/// method-not-allowed step uses, no Allow header, and the default content type - because the allowed
/// method set, the exact problem detail, and the content type for a snapshot request are defined
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
                [$"The endpoint of the request does not support the '{requestInfo.MethodName}' method."],
                requestInfo.FrontendRequest.TraceId
            ),
            Headers: []
        );
    }
}
