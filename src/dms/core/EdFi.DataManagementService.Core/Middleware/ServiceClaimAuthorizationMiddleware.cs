// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
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
    /// How a claim set's resource-claim name is compared with <see cref="ClaimUri" />. <c>null</c>
    /// keeps the shared <see cref="ClaimSetExtensions.FindMatchingResourceClaims" /> comparison.
    /// </summary>
    public StringComparison? ClaimNameComparison { get; init; }

    /// <summary>The claim set's resource claims whose name matches <see cref="ClaimUri" />.</summary>
    public ResourceClaim[] FindMatchingClaims(ClaimSet claimSet) =>
        ClaimNameComparison is { } comparison
            ? [.. claimSet.ResourceClaims.Where(claim => string.Equals(claim.Name, ClaimUri, comparison))]
            : claimSet.FindMatchingResourceClaims(ClaimUri);

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
    /// projection request requires <c>Read</c>. The claim name matches ordinally ignoring case, as CMS
    /// resolves claim names, so a distinct claim whose name differs only by a character a culture-aware
    /// comparison ignores (such as U+00AD) does not authorize the projection.
    /// </summary>
    public static ServiceClaimRequirement EducationOrganizationProjection { get; } =
        new(Conventions.EducationOrganizationProjectionServiceClaimUri, static _ => "Read", "service claim")
        {
            ClaimNameComparison = StringComparison.OrdinalIgnoreCase,
        };
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

        string scopeForLogging = LoggingSanitizer.SanitizeFreeTextForLogging(
            string.Join(' ', ClaimSetResolver.GetCandidates(requestInfo.ClientAuthorizations)),
            maxLength: 1024
        );
        ClaimSet claimSet;

        switch (ClaimSetResolver.Resolve(requestInfo.ClientAuthorizations, claimSets))
        {
            case ClaimSetResolution.Success success:
                // A request-local copy: the authenticated record may be shared through the JWT cache.
                requestInfo.ClientAuthorizations = requestInfo.ClientAuthorizations with
                {
                    ClaimSetName = success.ClaimSetName,
                };
                claimSet = success.ClaimSet;
                break;
            case ClaimSetResolution.Ambiguous:
                logger.LogInformation(
                    "ServiceClaimAuthorizationMiddleware: The token identifies multiple claim sets. Scope: {Scope} - {TraceId}",
                    scopeForLogging,
                    LoggingSanitizer.SanitizeCorrelationId(requestInfo.FrontendRequest.TraceId.Value)
                );
                requestInfo.FrontendResponse = ClaimSetResolutionResponses.Ambiguous(
                    requestInfo.FrontendRequest.TraceId
                );
                return;
            case ClaimSetResolution.InvalidMetadata invalidMetadata:
                logger.LogError(
                    "ServiceClaimAuthorizationMiddleware: The security metadata contains duplicate claim-set names: {DuplicateNames}. Tenant: {Tenant}; CorrelationId: {CorrelationId}",
                    LoggingSanitizer.SanitizeFreeTextForLogging(
                        string.Join(' ', invalidMetadata.DuplicateNames),
                        maxLength: 1024
                    ),
                    LoggingSanitizer.SanitizeInternalValueForLogging(requestInfo.FrontendRequest.Tenant),
                    LoggingSanitizer.SanitizeCorrelationId(requestInfo.FrontendRequest.TraceId.Value)
                );
                requestInfo.FrontendResponse = ClaimSetResolutionResponses.InvalidMetadata(
                    requestInfo.FrontendRequest.TraceId
                );
                return;
            case ClaimSetResolution.NoMatch:
                logger.LogInformation(
                    "ServiceClaimAuthorizationMiddleware: No ClaimSet matching Scope {Scope} - {TraceId}",
                    scopeForLogging,
                    LoggingSanitizer.SanitizeCorrelationId(requestInfo.FrontendRequest.TraceId.Value)
                );
                CreateForbiddenResponse(requestInfo);
                return;
            default:
                throw new UnreachableException("Unknown claim-set resolution result.");
        }

        ResourceClaim[] matchingClaims = requirement.FindMatchingClaims(claimSet);

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
