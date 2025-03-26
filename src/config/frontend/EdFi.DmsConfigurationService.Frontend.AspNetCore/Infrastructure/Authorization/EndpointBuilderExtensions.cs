// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;

/// <summary>
/// EndpointBuilderExtensions can be used to apply authorization policies based on HTTP methods
/// </summary>
public static class EndpointBuilderExtensions
{
    public static RouteHandlerBuilder MapSecuredGet(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Delegate handler
    )
    {
        return endpoints
            .MapGet(pattern, handler)
            .RequireAuthorization(
                [SecurityConstants.ServicePolicy, AuthorizationScopePolicies.ReadOnlyOrAdminScopePolicy]
            );
    }

    public static RouteHandlerBuilder MapSecuredPost(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Delegate handler
    )
    {
        return endpoints
            .MapPost(pattern, handler)
            .RequireAuthorization(
                [SecurityConstants.ServicePolicy, AuthorizationScopePolicies.AdminScopePolicy]
            );
    }

    public static RouteHandlerBuilder MapSecuredPut(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Delegate handler
    )
    {
        return endpoints
            .MapPut(pattern, handler)
            .RequireAuthorization(
                [SecurityConstants.ServicePolicy, AuthorizationScopePolicies.AdminScopePolicy]
            );
    }

    public static RouteHandlerBuilder MapSecuredDelete(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Delegate handler
    )
    {
        return endpoints
            .MapDelete(pattern, handler)
            .RequireAuthorization(
                [SecurityConstants.ServicePolicy, AuthorizationScopePolicies.AdminScopePolicy]
            );
    }

    public static RouteHandlerBuilder MapLimitedAccess(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Delegate handler
    )
    {
        return endpoints
            .MapGet(pattern, handler)
            .RequireAuthorization(
                [
                    SecurityConstants.ServicePolicy,
                    AuthorizationScopePolicies.AdminOrAuthorizationEndpointsAccessScopePolicyOrReadOnly,
                ]
            );
    }

    public static RouteHandlerBuilder MapPublic(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Delegate handler
    )
    {
        return endpoints.MapGet(pattern, handler).AllowAnonymous();
    }
}
