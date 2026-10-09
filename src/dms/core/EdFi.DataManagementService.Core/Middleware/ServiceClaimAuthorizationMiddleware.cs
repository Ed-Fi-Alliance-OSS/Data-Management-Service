// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// A CMS-seeded service claim a pipeline requires, the CMS action a request needs on it, and the
/// wording that names the claim in a security-configuration failure.
/// </summary>
/// <param name="ClaimUri">The service claim's resource-claim URI.</param>
/// <param name="RequiredAction">Selects the CMS action the request needs on the claim.</param>
/// <param name="ClaimDescription">
/// The phrase naming the claim in the security-configuration response, e.g. "identity service claim".
/// </param>
internal sealed record ServiceClaimRequirement(
    string ClaimUri,
    Func<RequestInfo, string> RequiredAction,
    string ClaimDescription
)
{
    /// <summary>
    /// The <c>http://ed-fi.org/identity/claims/services/identity</c> claim. The required action is
    /// <c>Create</c> for <see cref="IdentityOperation.Create" /> and <c>Read</c> for every other identity
    /// operation - <c>Update</c> is never consulted, so a claim set granting only <c>Update</c> on the
    /// identity claim is forbidden on every operation.
    /// </summary>
    public static ServiceClaimRequirement Identity { get; } =
        new(
            $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity",
            static requestInfo =>
                requestInfo.IdentityOperation == IdentityOperation.Create ? "Create" : "Read",
            "identity service claim"
        );

    /// <summary>
    /// The <see cref="Conventions.EducationOrganizationProjectionServiceClaimUri" /> claim; every
    /// projection request requires <c>Read</c>.
    /// </summary>
    public static ServiceClaimRequirement EducationOrganizationProjection { get; } =
        new(Conventions.EducationOrganizationProjectionServiceClaimUri, static _ => "Read", "service claim");
}

/// <summary>
/// Authorizes a service request against the token's claim set and the
/// <see cref="ServiceClaimRequirement" /> the pipeline was built with.
/// A matched action's strategy list must be exactly one entry named
/// <see cref="AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired" />: an empty list, an
/// unknown name, or another recognized strategy is a security-configuration failure (500), following
/// the strategy-validation precedent in <see cref="ResourceActionAuthorizationMiddleware" />.
/// </summary>
internal sealed class ServiceClaimAuthorizationMiddleware(
    ServiceClaimRequirement requirement,
    IClaimSetProvider claimSetProvider,
    ILogger<ServiceClaimAuthorizationMiddleware> logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        string requiredAction = requirement.RequiredAction(requestInfo);

        IList<ClaimSet> claimSets = await claimSetProvider.GetAllClaimSets(
            requestInfo.FrontendRequest.Tenant,
            requestInfo.RequestCancellationToken
        );

        string claimSetName = requestInfo.ClientAuthorizations.ClaimSetName;
        ClaimSet? claimSet = claimSets.FindClaimSetByName(claimSetName);

        if (claimSet is null)
        {
            logger.LogInformation(
                "ServiceClaimAuthorizationMiddleware: No ClaimSet matching Scope {Scope} - {TraceId}",
                LoggingSanitizer.SanitizeInternalValueForLogging(claimSetName),
                LoggingSanitizer.SanitizeCorrelationId(requestInfo.FrontendRequest.TraceId.Value)
            );
            CreateForbiddenResponse(requestInfo);
            return;
        }

        ResourceClaim[] matchingClaims = claimSet.FindMatchingResourceClaims(requirement.ClaimUri);

        ResourceClaim[] authorizedActions = matchingClaims
            .Where(claim => string.Equals(claim.Action, requiredAction, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (authorizedActions.Length == 0)
        {
            logger.LogDebug(
                "ServiceClaimAuthorizationMiddleware: Claim set '{ClaimSetName}' does not grant '{Action}' on service claim '{ServiceClaim}' - {TraceId}",
                LoggingSanitizer.SanitizeInternalValueForLogging(claimSet.Name),
                requiredAction,
                requirement.ClaimUri,
                LoggingSanitizer.SanitizeCorrelationId(requestInfo.FrontendRequest.TraceId.Value)
            );
            CreateForbiddenResponse(requestInfo);
            return;
        }

        IReadOnlyList<string> strategies = authorizedActions
            .SelectMany(static authorizedAction => authorizedAction.AuthorizationStrategies)
            .Select(static strategy => strategy.Name)
            .ToList();

        bool isNoFurtherAuthorizationRequired =
            strategies.Count == 1
            && string.Equals(
                strategies[0],
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                StringComparison.Ordinal
            );

        if (!isNoFurtherAuthorizationRequired)
        {
            string message =
                $"The {requirement.ClaimDescription}'s authorization strategies for claim set '{claimSet.Name}' and action '{requiredAction}' must be exactly ['{AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired}'].";
            logger.LogError(
                "ServiceClaimAuthorizationMiddleware: The authorization strategies of service claim '{ServiceClaim}' for claim set '{ClaimSetName}' and action '{Action}' must be exactly ['{Strategy}']. - {TraceId}",
                requirement.ClaimUri,
                LoggingSanitizer.SanitizeInternalValueForLogging(claimSet.Name),
                requiredAction,
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                LoggingSanitizer.SanitizeCorrelationId(requestInfo.FrontendRequest.TraceId.Value)
            );
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 500,
                Body: FailureResponse.ForSecurityConfiguration(
                    requestInfo.FrontendRequest.TraceId,
                    [message]
                ),
                Headers: [],
                ContentType: "application/problem+json"
            );
            return;
        }

        await next();
    }

    private static void CreateForbiddenResponse(RequestInfo requestInfo)
    {
        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 403,
            Body: FailureResponse.ForForbidden(traceId: requestInfo.FrontendRequest.TraceId, errors: []),
            Headers: [],
            ContentType: "application/problem+json"
        );
    }
}
