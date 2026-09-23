// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Utilities;
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
            if (claimSet is null)
            {
                CreateMissingSecurityMetadataResponse(requestInfo);
                return;
            }

            Debug.Assert(
                requestInfo.PathComponents is not null,
                "ResourceActionAuthorizationMiddleware: There should be PathComponents",
                ""
            );

            if (!ValidateResourceClaims(requestInfo, claimSet))
            {
                return;
            }

            ResourceClaim[] matchingClaims = claimSet.FindMatchingResourceClaims(
                BuildResourceClaimUri(requestInfo)
            );

            if (!ValidateMatchingClaims(requestInfo, matchingClaims))
            {
                return;
            }

            // A POST is a create or an update depending on whether its target exists, which only the backend
            // observes, so it resolves both actions here and the backend applies the one its target selects.
            bool authorized =
                requestInfo.Method is RequestMethod.POST
                    ? ResolveUpsertActionPolicies(requestInfo, matchingClaims, claimSet.Name)
                    : AuthorizeRequestAction(requestInfo, matchingClaims, claimSet.Name);

            if (!authorized)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ResourceActionAuthorizationMiddleware: Error while authorizing the request - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 500,
                Body: FailureResponse.ForServerErrorMessageBody(
                    "Error while authorizing the request.",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );

            return;
        }

        await next();
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
        IList<ClaimSet> claimsList = await _claimSetProvider.GetAllClaimSets(
            requestInfo.FrontendRequest.Tenant
        );

        ClaimSet? claimSet = claimsList.FindClaimSetByName(claimSetName);

        if (claimSet is null)
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
        string resourceClaimUri = requestInfo.PathComponents.ProjectEndpointName.GetResourceClaimUri(
            requestInfo.ResourceSchema.ResourceName
        );

        _logger.LogDebug("resourceClaimUri: {ResourceClaimUri}", resourceClaimUri);
        return resourceClaimUri;
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
    /// Authorizes the one action the request maps to and records its strategies.
    /// </summary>
    private bool AuthorizeRequestAction(
        RequestInfo requestInfo,
        ResourceClaim[] matchingClaims,
        string claimSetName
    )
    {
        var actionName = GetActionName(requestInfo);
        ResourceClaim[] authorizedActions = FindAuthorizedActions(matchingClaims, actionName);

        if (!ValidateAuthorizedAction(requestInfo, authorizedActions, actionName, claimSetName))
        {
            return false;
        }

        IReadOnlyList<string> strategies = ExtractAuthorizationStrategies(authorizedActions);
        if (!ValidateAuthorizationStrategies(requestInfo, strategies, actionName, authorizedActions))
        {
            return false;
        }

        requestInfo.ResourceActionAuthStrategies = strategies;
        return true;
    }

    /// <summary>
    /// Resolves what the claim set grants a POST for Create and for Update. Only a POST neither action permits
    /// is refused here, with the Create denial a POST has always answered; any other combination proceeds and
    /// the action its target selects decides the outcome.
    /// </summary>
    private bool ResolveUpsertActionPolicies(
        RequestInfo requestInfo,
        ResourceClaim[] matchingClaims,
        string claimSetName
    )
    {
        var policies = new UpsertActionPolicies(
            ResolveUpsertActionPolicyEvidence(requestInfo, matchingClaims, CreateActionName, claimSetName),
            ResolveUpsertActionPolicyEvidence(requestInfo, matchingClaims, UpdateActionName, claimSetName)
        );

        if (
            policies is
            {
                Create: UpsertActionPolicyEvidence.Denied createDenied,
                Update: UpsertActionPolicyEvidence.Denied
            }
        )
        {
            _logger.LogDebug(
                "ResourceAuthorizationMiddleware: Can not perform {RequestMethod} on the resource {ResourceName} - {TraceId}",
                requestInfo.Method.ToString(),
                createDenied.ResourceClaimName,
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = ResourceActionAuthorizationResponses.CreateActionDeniedResponse(
                requestInfo,
                createDenied.ActionName,
                createDenied.ResourceClaimName,
                createDenied.ClaimSetName
            );
            return false;
        }

        requestInfo.UpsertActionPolicies = policies;
        return true;
    }

    private UpsertActionPolicyEvidence ResolveUpsertActionPolicyEvidence(
        RequestInfo requestInfo,
        ResourceClaim[] matchingClaims,
        string actionName,
        string claimSetName
    )
    {
        ResourceClaim[] authorizedActions = FindAuthorizedActions(matchingClaims, actionName);

        if (authorizedActions.Length == 0)
        {
            return new UpsertActionPolicyEvidence.Denied(
                actionName,
                requestInfo.ResourceSchema.ResourceName.Value,
                claimSetName
            );
        }

        IReadOnlyList<string> strategies = ExtractAuthorizationStrategies(authorizedActions);

        if (strategies.Count == 0)
        {
            string[] matchedResourceClaimUris = GetMatchedResourceClaimUris(authorizedActions);

            return new UpsertActionPolicyEvidence.NoStrategies(
                actionName,
                matchedResourceClaimUris,
                matchedResourceClaimUris[0]
            );
        }

        return new UpsertActionPolicyEvidence.Permitted(actionName, strategies);
    }

    private const string CreateActionName = "Create";

    private const string UpdateActionName = "Update";

    private const string ReadChangesActionName = "ReadChanges";

    /// <summary>
    /// Gets the action name for the request. Tracked-change Change Query requests (/deletes and
    /// /keyChanges) authorize against the dedicated ReadChanges action rather than Read.
    /// </summary>
    private static string GetActionName(RequestInfo requestInfo)
    {
        if (requestInfo.ChangeQueryOperation is not null)
        {
            return ReadChangesActionName;
        }

        // The mapping covers the four verbs that carry out a resource action. RequestMethod also
        // has UNSUPPORTED, which has no action and belongs to a pipeline that terminates at
        // MethodNotAllowedMiddleware well before this step - so reaching here with it means a
        // pipeline was misassembled, and saying so beats a bare KeyNotFoundException.
        if (!_methodToActionNameMapping.TryGetValue(requestInfo.Method, out string? actionName))
        {
            throw new InvalidOperationException(
                $"No resource action is defined for request method '{requestInfo.Method}'. "
                    + "ResourceActionAuthorizationMiddleware only runs in pipelines whose method is GET, POST, PUT or DELETE."
            );
        }

        return actionName;
    }

    /// <summary>
    /// Finds the authorized action from the matching claims.
    /// </summary>
    private static ResourceClaim[] FindAuthorizedActions(ResourceClaim[] matchingClaims, string actionName)
    {
        return matchingClaims
            .Where(x => string.Equals(x.Action, actionName, StringComparison.InvariantCultureIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// Validates that an authorized action was found for the request.
    /// </summary>
    private bool ValidateAuthorizedAction(
        RequestInfo requestInfo,
        ResourceClaim[] authorizedActions,
        string actionName,
        string claimSetName
    )
    {
        if (authorizedActions.Length == 0)
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
    private IReadOnlyList<string> ExtractAuthorizationStrategies(ResourceClaim[] authorizedActions)
    {
        IReadOnlyList<string> strategies = authorizedActions
            .SelectMany(static authorizedAction => authorizedAction.AuthorizationStrategies)
            .Select(static auth => auth.Name)
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
    private bool ValidateAuthorizationStrategies(
        RequestInfo requestInfo,
        IReadOnlyList<string> strategies,
        string actionName,
        ResourceClaim[] authorizedActions
    )
    {
        if (strategies.Count == 0)
        {
            string[] matchedResourceClaimUris = GetMatchedResourceClaimUris(authorizedActions);
            string matchedResourceClaimName = matchedResourceClaimUris[0];

            CreateNoStrategiesSecurityConfigurationResponse(
                requestInfo,
                actionName,
                matchedResourceClaimUris,
                matchedResourceClaimName
            );
            return false;
        }
        return true;
    }

    private static string[] GetMatchedResourceClaimUris(ResourceClaim[] authorizedActions)
    {
        string[] resourceClaimUris = authorizedActions
            .Select(static authorizedAction => authorizedAction.Name)
            .Where(static resourceClaimUri => resourceClaimUri is not null)
            .Select(static resourceClaimUri => resourceClaimUri!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static resourceClaimUri => resourceClaimUri, StringComparer.Ordinal)
            .ToArray();

        if (resourceClaimUris.Length == 0)
        {
            throw new UnreachableException("Matched resource action claims must have a name.");
        }

        return resourceClaimUris;
    }

    /// <summary>
    /// Creates an unauthorized (401) response.
    /// </summary>
    private static void CreateUnauthorizedResponse(RequestInfo requestInfo)
    {
        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: (int)HttpStatusCode.Unauthorized,
            Body: FailureResponse.ForAuthenticationFailure(
                requestInfo.FrontendRequest.TraceId,
                ["No authorization information found. Ensure valid JWT token is provided."]
            ),
            Headers: new Dictionary<string, string>
            {
                ["WWW-Authenticate"] = "Bearer error=\"invalid_token\"",
            },
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

    private void CreateMissingSecurityMetadataResponse(RequestInfo requestInfo)
    {
        string[] errors = [SecurityConfigurationFailureMessages.MissingSecurityMetadata];
        SecurityConfigurationFailureLogger.Log(
            _logger,
            requestInfo,
            errors,
            assignedClaimSetName: requestInfo.ClientAuthorizations.ClaimSetName,
            cmsAction: GetActionName(requestInfo)
        );

        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: (int)HttpStatusCode.InternalServerError,
            Body: FailureResponse.ForSecurityConfiguration(requestInfo.FrontendRequest.TraceId, errors),
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
    ) =>
        requestInfo.FrontendResponse = ResourceActionAuthorizationResponses.CreateActionDeniedResponse(
            requestInfo,
            actionName,
            resourceClaimName,
            claimSetName
        );

    private void CreateNoStrategiesSecurityConfigurationResponse(
        RequestInfo requestInfo,
        string actionName,
        IReadOnlyList<string> matchedResourceClaimUris,
        string matchedResourceClaimName
    ) =>
        requestInfo.FrontendResponse =
            ResourceActionAuthorizationResponses.CreateNoStrategiesSecurityConfigurationResponse(
                _logger,
                requestInfo,
                actionName,
                matchedResourceClaimUris,
                matchedResourceClaimName
            );
}
