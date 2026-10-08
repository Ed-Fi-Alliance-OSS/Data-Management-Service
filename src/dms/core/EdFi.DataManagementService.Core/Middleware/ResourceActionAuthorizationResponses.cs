// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// The action-level denial and no-strategies responses. The middleware renders them for the action a request
/// maps to, and the upsert handler renders them for the action a POST's target selected, so both come from here.
/// </summary>
internal static class ResourceActionAuthorizationResponses
{
    public static FrontendResponse CreateActionDeniedResponse(
        RequestInfo requestInfo,
        string actionName,
        string resourceClaimName,
        string claimSetName
    ) =>
        new(
            StatusCode: (int)HttpStatusCode.Forbidden,
            Body: FailureResponse.ForForbidden(
                traceId: requestInfo.FrontendRequest.TraceId,
                errors:
                [
                    $"The API client's assigned claim set (currently '{claimSetName}') must grant permission of the '{actionName}' action on one of the following resource claims: {resourceClaimName}",
                ],
                typeExtension: "access-denied:action"
            ),
            Headers: [],
            ContentType: "application/problem+json"
        );

    public static FrontendResponse CreateNoStrategiesSecurityConfigurationResponse(
        ILogger logger,
        RequestInfo requestInfo,
        string actionName,
        IReadOnlyList<string> matchedResourceClaimUris,
        string matchedResourceClaimName
    )
    {
        string[] errors =
        [
            SecurityConfigurationFailureMessages.NoAuthorizationStrategies(
                actionName,
                matchedResourceClaimUris,
                matchedResourceClaimName
            ),
        ];
        SecurityConfigurationFailureLogger.Log(
            logger,
            requestInfo,
            errors,
            matchedResourceClaimUris,
            matchedResourceClaimName,
            requestInfo.ClientAuthorizations.ClaimSetName,
            actionName
        );

        return new FrontendResponse(
            StatusCode: (int)HttpStatusCode.InternalServerError,
            Body: FailureResponse.ForSecurityConfiguration(requestInfo.FrontendRequest.TraceId, errors),
            Headers: [],
            ContentType: "application/problem+json"
        );
    }
}
