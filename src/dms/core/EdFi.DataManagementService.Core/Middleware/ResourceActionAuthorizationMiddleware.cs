// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Authorizes requests resource and action based on the client's authorization information.
/// </summary>
internal class ResourceActionAuthorizationMiddleware(IClaimSetProvider _claimSetProvider, ILogger _logger)
    : IPipelineStep
{
    private static readonly Dictionary<RequestMethod, string> _methodToActionNameMapping = new()
    {
        { RequestMethod.POST, "Create" },
        { RequestMethod.GET, "Read" },
        { RequestMethod.PUT, "Update" },
        { RequestMethod.DELETE, "Delete" },
    };

    /// <summary>
    /// Executes the authorization middleware to validate resource access permissions.
    /// </summary>
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        try
        {
            _logger.LogDebug(
                "Entering ResourceActionAuthorizationMiddleware - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            if (!ValidateClientAuthorizations(requestInfo))
            {
                return;
            }

            ClaimSet? claimSet = await GetClaimSetForClient(requestInfo);
            if (claimSet == null)
            {
                CreateForbiddenResponse(requestInfo);
                return;
            }

            Debug.Assert(
                requestInfo.PathComponents != null,
                "ResourceActionAuthorizationMiddleware: There should be PathComponents"
            );

            if (!ValidateResourceClaims(requestInfo, claimSet))
            {
                return;
            }

            ResourceClaim[] matchingClaims = FindMatchingResourceClaims(
                claimSet,
                BuildResourceClaimUri(requestInfo)
            );

            if (!ValidateMatchingClaims(requestInfo, matchingClaims))
            {
                return;
            }

            var actionName = GetActionName(requestInfo);
            ResourceClaim? authorizedAction = FindAuthorizedAction(matchingClaims, actionName);

            if (!ValidateAuthorizedAction(requestInfo, authorizedAction, actionName, claimSet.Name))
            {
                return;
            }

            IReadOnlyList<string> strategies = ExtractAuthorizationStrategies(authorizedAction!);
            if (!ValidateAuthorizationStrategies(requestInfo, strategies, actionName, claimSet.Name))
            {
                return;
            }

            requestInfo.ResourceActionAuthStrategies = strategies;
            await next();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error while authorizing the request - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 500,
                Body: new JsonObject
                {
                    ["message"] = "Error while authorizing the request.",
                    ["traceId"] = requestInfo.FrontendRequest.TraceId.Value,
                },
                Headers: []
            );
        }
    }

    /// <summary>
    /// Validates that client authorizations exist in the request.
    /// </summary>
    private bool ValidateClientAuthorizations(RequestInfo requestInfo)
    {
        if (requestInfo.ClientAuthorizations == No.ClientAuthorizations)
        {
            _logger.LogWarning(
                "ResourceActionAuthorizationMiddleware: No ClientAuthorizations found - JWT authentication may have failed - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
            CreateUnauthorizedResponse(requestInfo);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Retrieves the claim set for the authenticated client.
    /// </summary>
    private async Task<ClaimSet?> GetClaimSetForClient(RequestInfo requestInfo)
    {
        string claimSetName = requestInfo.ClientAuthorizations.ClaimSetName;
        _logger.LogInformation("Claim set name from token scope - {ClaimSetName}", claimSetName);

        _logger.LogInformation("Retrieving claim set list");
        IList<ClaimSet> claimsList = await _claimSetProvider.GetAllClaimSets();

        ClaimSet? claimSet = claimsList.SingleOrDefault(c =>
            string.Equals(c.Name, claimSetName, StringComparison.InvariantCultureIgnoreCase)
        );

        if (claimSet == null)
        {
            _logger.LogInformation(
                "ResourceActionAuthorizationMiddleware: No ClaimSet matching Scope {Scope} - {TraceId}",
                claimSetName,
                requestInfo.FrontendRequest.TraceId.Value
            );
        }

        return claimSet;
    }

    /// <summary>
    /// Validates that the claim set contains resource claims.
    /// </summary>
    private bool ValidateResourceClaims(RequestInfo requestInfo, ClaimSet claimSet)
    {
        if (claimSet.ResourceClaims.Count == 0)
        {
            _logger.LogDebug("ResourceActionAuthorizationMiddleware: No ResourceClaims found");
            CreateForbiddenResponse(requestInfo);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Builds the resource claim URI for the requested resource.
    /// </summary>
    private string BuildResourceClaimUri(RequestInfo requestInfo)
    {
        string resourceClaimName = requestInfo.ResourceSchema.ResourceName.Value;
        string resourceClaimUri =
            $"{Conventions.EdFiOdsResourceClaimBaseUri}/{requestInfo.PathComponents.ProjectEndpointName.Value}/{resourceClaimName}";

        _logger.LogDebug("resourceClaimUri: {ResourceClaimUri}", resourceClaimUri);
        return resourceClaimUri;
    }

    /// <summary>
    /// Finds resource claims matching the requested resource URI.
    /// </summary>
    private static ResourceClaim[] FindMatchingResourceClaims(ClaimSet claimSet, string resourceClaimUri)
    {
        return claimSet
            .ResourceClaims.Where(r =>
                string.Equals(r.Name, resourceClaimUri, StringComparison.InvariantCultureIgnoreCase)
            )
            .ToArray();
    }

    /// <summary>
    /// Validates that matching claims were found for the resource.
    /// </summary>
    private bool ValidateMatchingClaims(RequestInfo requestInfo, ResourceClaim[] matchingClaims)
    {
        if (matchingClaims.Length == 0)
        {
            string resourceClaimName = requestInfo.ResourceSchema.ResourceName.Value;
            _logger.LogDebug(
                "ResourceActionAuthorizationMiddleware: No ResourceClaim matching Endpoint {Endpoint} - {TraceId}",
                resourceClaimName,
                requestInfo.FrontendRequest.TraceId.Value
            );
            CreateForbiddenResponse(requestInfo);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Gets the action name for the given requestInfo.
    /// </summary>
    private static string GetActionName(RequestInfo requestInfo)
    {
        return _methodToActionNameMapping[requestInfo.Method];
    }

    /// <summary>
    /// Finds the authorized action from the matching claims.
    /// </summary>
    private static ResourceClaim? FindAuthorizedAction(ResourceClaim[] matchingClaims, string actionName)
    {
        return matchingClaims.SingleOrDefault(x =>
            string.Equals(x.Action, actionName, StringComparison.InvariantCultureIgnoreCase)
        );
    }

    /// <summary>
    /// Validates that an authorized action was found for the request.
    /// </summary>
    private bool ValidateAuthorizedAction(
        RequestInfo requestInfo,
        ResourceClaim? authorizedAction,
        string actionName,
        string claimSetName
    )
    {
        if (authorizedAction == null)
        {
            string resourceClaimName = requestInfo.ResourceSchema.ResourceName.Value;
            _logger.LogDebug(
                "ResourceAuthorizationMiddleware: Can not perform {RequestMethod} on the resource {ResourceName} - {TraceId}",
                requestInfo.Method.ToString(),
                resourceClaimName,
                requestInfo.FrontendRequest.TraceId.Value
            );
            CreateActionDeniedResponse(requestInfo, actionName, resourceClaimName, claimSetName);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Extracts authorization strategies from the authorized action.
    /// </summary>
    private IReadOnlyList<string> ExtractAuthorizationStrategies(ResourceClaim authorizedAction)
    {
        IReadOnlyList<string> strategies = authorizedAction
            .AuthorizationStrategies.Select(auth => auth.Name)
            .ToList();

        _logger.LogDebug(
            "resourceActionAuthStrategies: {ResourceActionAuthStrategies}",
            string.Join(", ", strategies)
        );

        return strategies;
    }

    /// <summary>
    /// Validates that authorization strategies exist for the action.
    /// </summary>
    private static bool ValidateAuthorizationStrategies(
        RequestInfo requestInfo,
        IReadOnlyList<string> strategies,
        string actionName,
        string claimSetName
    )
    {
        if (strategies.Count == 0)
        {
            string resourceClaimName = requestInfo.ResourceSchema.ResourceName.Value;
            CreateNoStrategiesResponse(requestInfo, actionName, resourceClaimName, claimSetName);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Creates an unauthorized (401) response.
    /// </summary>
    private static void CreateUnauthorizedResponse(RequestInfo requestInfo)
    {
        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: (int)HttpStatusCode.Unauthorized,
            Body: FailureResponse.ForUnauthorized(
                requestInfo.FrontendRequest.TraceId,
                error: "Unauthorized",
                description: "No authorization information found. Ensure valid JWT token is provided."
            ),
            Headers: [],
            ContentType: "application/problem+json"
        );
    }

    /// <summary>
    /// Creates a generic forbidden (403) response.
    /// </summary>
    private static void CreateForbiddenResponse(RequestInfo requestInfo)
    {
        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 403,
            Body: FailureResponse.ForForbidden(traceId: requestInfo.FrontendRequest.TraceId, errors: []),
            Headers: [],
            ContentType: "application/problem+json"
        );
    }

    /// <summary>
    /// Creates a forbidden response for denied action.
    /// </summary>
    private static void CreateActionDeniedResponse(
        RequestInfo requestInfo,
        string actionName,
        string resourceClaimName,
        string claimSetName
    )
    {
        requestInfo.FrontendResponse = new FrontendResponse(
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
    }

    /// <summary>
    /// Creates a forbidden response for missing authorization strategies.
    /// </summary>
    private static void CreateNoStrategiesResponse(
        RequestInfo requestInfo,
        string actionName,
        string resourceClaimName,
        string claimSetName
    )
    {
        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: (int)HttpStatusCode.Forbidden,
            Body: FailureResponse.ForForbidden(
                traceId: requestInfo.FrontendRequest.TraceId,
                errors:
                [
                    $"No authorization strategies were defined for the requested action '{actionName}' against resource ['{resourceClaimName}'] matched by the caller's claim '{claimSetName}'.",
                ]
            ),
            Headers: [],
            ContentType: "application/problem+json"
        );
    }
}
