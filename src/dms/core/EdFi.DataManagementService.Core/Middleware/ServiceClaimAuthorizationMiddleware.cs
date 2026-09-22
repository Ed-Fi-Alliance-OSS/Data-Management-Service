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
/// Authorizes an identity request against the token's claim set and the CMS-seeded
/// <c>http://ed-fi.org/identity/claims/services/identity</c> service claim (design.md:433-468).
/// The required CMS action is <c>Create</c> for <see cref="IdentityOperation.Create" /> and
/// <c>Read</c> for every other identity operation - <c>Update</c> is never consulted, so a claim set
/// granting only <c>Update</c> on the identity claim is forbidden on every operation.
/// A matched action's strategy list must be exactly one entry named
/// <see cref="AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired" />: an empty list, an
/// unknown name, or another recognized strategy is a security-configuration failure (500), following
/// the strategy-validation precedent in <see cref="ResourceActionAuthorizationMiddleware" />.
/// </summary>
internal sealed class ServiceClaimAuthorizationMiddleware(
    IClaimSetProvider claimSetProvider,
    ILogger<ServiceClaimAuthorizationMiddleware> logger
) : IPipelineStep
{
    private static readonly string _identityServiceClaimUri =
        $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        string requiredAction = requestInfo.IdentityOperation == IdentityOperation.Create ? "Create" : "Read";

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
                claimSetName,
                requestInfo.FrontendRequest.TraceId.Value
            );
            CreateForbiddenResponse(requestInfo);
            return;
        }

        ResourceClaim[] matchingClaims = claimSet.FindMatchingResourceClaims(_identityServiceClaimUri);

        ResourceClaim[] authorizedActions = matchingClaims
            .Where(claim => string.Equals(claim.Action, requiredAction, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (authorizedActions.Length == 0)
        {
            logger.LogDebug(
                "ServiceClaimAuthorizationMiddleware: Claim set '{ClaimSetName}' does not grant '{Action}' on the identity service claim - {TraceId}",
                claimSet.Name,
                requiredAction,
                requestInfo.FrontendRequest.TraceId.Value
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
                $"The identity service claim's authorization strategies for claim set '{claimSet.Name}' and action '{requiredAction}' must be exactly ['{AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired}'].";
            logger.LogError(
                "ServiceClaimAuthorizationMiddleware: {Message} - {TraceId}",
                message,
                requestInfo.FrontendRequest.TraceId.Value
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
