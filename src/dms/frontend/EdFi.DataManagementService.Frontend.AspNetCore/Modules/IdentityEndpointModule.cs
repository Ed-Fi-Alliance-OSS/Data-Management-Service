// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using Microsoft.Extensions.Options;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;
using FrontendAppSettings = EdFi.DataManagementService.Frontend.AspNetCore.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

/// <summary>
/// Maps the DMS-owned Identity HTTP surface under /identity/v2, gated by
/// AppSettings:EnableIdentityManagement (default false). Follows the mapping-time fail-closed
/// shape of ManagementEndpointModule: with the toggle off, nothing is mapped and every identity
/// route falls through to the fallback 404.
/// </summary>
public class IdentityEndpointModule(
    IOptions<FrontendAppSettings> frontendOptions,
    IOptions<CoreAppSettings> coreOptions,
    ILogger<IdentityEndpointModule> logger
) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        if (!coreOptions.Value.EnableIdentityManagement)
        {
            return;
        }

        string[] routeQualifierSegments = frontendOptions.Value.GetRouteQualifierSegmentsArray();

        // Fail closed at mapping time rather than at request time: a configured route-qualifier
        // name that collides with another under case-insensitive comparison would make
        // IdentityRequestContext.RouteQualifiers ambiguous (D15), so the routes are not exposed
        // at all rather than silently overwriting one qualifier's value with another's.
        if (HasCaseInsensitiveCollision(routeQualifierSegments, out string? collidingNames))
        {
            logger.LogError(
                "Identity routes were not mapped because configured route qualifier segments "
                    + "collide under case-insensitive comparison: {CollidingNames}. Configure "
                    + "unique qualifier names to enable the identity endpoints.",
                collidingNames
            );
            return;
        }

        string prefix = FixedRoutePattern.Build(routeQualifierSegments, frontendOptions.Value.MultiTenancy);
        string identitiesBase = $"{prefix}/identity/v2/identities";

        // A GET to "{identitiesBase}/results" with no further segment naturally matches the
        // get-by-id pattern below (id = "results") rather than the results pattern, which requires
        // an additional token segment. No separate mapping is needed for that case; verified by
        // IdentityEndpointModuleTests.
        //
        // The get-by-id and results-token segments are named "__identityId"/"__identityToken"
        // rather than the literal "id"/"token": a configured route-qualifier segment
        // (AppSettings:RouteQualifierSegments) can itself be named "id" or "token", and that
        // qualifier's own segment is a literal "{id}"/"{token}" earlier in this same route
        // template (FixedRoutePattern.Build), so a literal "id"/"token" here would make the route
        // template repeat a parameter name and fail host start. Follows the
        // "__metadataRouteQualifier{n}" precedent in MetadataRouteValidator.
        endpoints
            .MapPost(identitiesBase, AspNetCoreFrontend.IdentityCreate)
            .WithMetadata(new IdentityOperationEndpointMetadata());

        endpoints
            .MapGet(
                $"{identitiesBase}/{{{AspNetCoreFrontend.IdentityIdRouteParameterName}}}",
                AspNetCoreFrontend.IdentityGetById
            )
            .WithMetadata(new IdentityOperationEndpointMetadata());

        endpoints
            .MapPost($"{identitiesBase}/find", AspNetCoreFrontend.IdentityFind)
            .WithMetadata(new IdentityOperationEndpointMetadata());

        endpoints
            .MapPost($"{identitiesBase}/search", AspNetCoreFrontend.IdentitySearch)
            .WithMetadata(new IdentityOperationEndpointMetadata());

        endpoints
            .MapGet(
                $"{identitiesBase}/results/{{{AspNetCoreFrontend.IdentityTokenRouteParameterName}}}",
                AspNetCoreFrontend.IdentityResults
            )
            .WithMetadata(new IdentityOperationEndpointMetadata());
    }

    /// <summary>
    /// True when two or more configured route-qualifier segment names collide under
    /// OrdinalIgnoreCase, which is the comparer IdentityRequestContext.RouteQualifiers uses (D15).
    /// </summary>
    private static bool HasCaseInsensitiveCollision(string[] segments, out string? collidingNames)
    {
        Dictionary<string, string> seenByCaseInsensitiveName = new(StringComparer.OrdinalIgnoreCase);

        foreach (string segment in segments)
        {
            if (seenByCaseInsensitiveName.TryGetValue(segment, out string? existing))
            {
                collidingNames = $"{existing}, {segment}";
                return true;
            }

            seenByCaseInsensitiveName[segment] = segment;
        }

        collidingNames = null;
        return false;
    }
}
