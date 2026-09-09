// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using Microsoft.Extensions.Options;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;
using FrontendAppSettings = EdFi.DataManagementService.Frontend.AspNetCore.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

/// <summary>
/// Management endpoints for administrative tasks.
/// In multi-tenant deployments, claimset endpoints require a tenant segment in the route.
/// </summary>
public class ManagementEndpointModule(
    IOptions<FrontendAppSettings> options,
    IOptions<CoreAppSettings> coreAppSettings,
    IOptions<ManagementEndpointsOptions> managementEndpointsOptions,
    IOptions<JwtAuthenticationOptions> jwtAuthenticationOptions,
    ILogger<ManagementEndpointModule> logger
) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        bool multiTenancy = options.Value.MultiTenancy;

        var managementEndpoints = endpoints.MapGroup("/management");

        // The unscoped forms are 404 in multi-tenant mode. They reach no claimset code, so they are
        // mapped whether or not the protected routes can be authorized, and they stay anonymous.
        if (multiTenancy)
        {
            managementEndpoints
                .MapPost("/reload-claimsets", ReloadClaimsetsNotFound)
                .WithName("ReloadClaimsetsNoTenant")
                .ExcludeFromDescription();

            managementEndpoints
                .MapGet("/view-claimsets", ViewClaimsetsNotFound)
                .WithName("ViewClaimsetsNoTenant")
                .ExcludeFromDescription();
        }

        // Fail closed at mapping time rather than at request time: without a usable required role
        // there is no way to authorize these endpoints, so they are not exposed at all.
        if (!IsRequiredRoleUsable())
        {
            return;
        }

        if (multiTenancy)
        {
            managementEndpoints
                .MapPost("/{tenant}/reload-claimsets", ReloadClaimsetsTenantAware)
                .WithName("ReloadClaimsets")
                .WithSummary("Reloads the Claimsets from the configured source for a specific tenant");

            managementEndpoints
                .MapGet("/{tenant}/view-claimsets", ViewClaimsetsTenantAware)
                .WithName("ViewClaimsets")
                .WithSummary("Views the current Claimsets configuration for a specific tenant");
        }
        else
        {
            managementEndpoints
                .MapPost("/reload-claimsets", ReloadClaimsets)
                .WithName("ReloadClaimsets")
                .WithSummary("Reloads the Claimsets from the configured source");

            managementEndpoints
                .MapGet("/view-claimsets", ViewClaimsets)
                .WithName("ViewClaimsets")
                .WithSummary("Views the current Claimsets configuration");
        }
    }

    /// <summary>
    /// True when a request to a claimset management endpoint could be authorized. Warns only when
    /// claimset reload is enabled, so a deployment that intentionally leaves these endpoints off does
    /// not log a security warning at every startup.
    /// </summary>
    private bool IsRequiredRoleUsable()
    {
        bool usable =
            managementEndpointsOptions.Value.TryGetRequiredRoleForEndpointMapping(out _)
            && !string.IsNullOrWhiteSpace(jwtAuthenticationOptions.Value.RoleClaimType);

        if (usable)
        {
            return true;
        }

        if (coreAppSettings.Value.EnableClaimsetReload)
        {
            logger.LogWarning(
                "Claimset management endpoints were not mapped because AppSettings:ManagementEndpoints:RequiredRole is missing or invalid, or JwtAuthentication:RoleClaimType is missing or blank. Configure a single role token such as dms-management-operator."
            );
        }

        return false;
    }

    /// <summary>
    /// Reload claimsets for single-tenant deployments (no tenant required)
    /// </summary>
    internal static async Task<IResult> ReloadClaimsets(
        IApiService apiService,
        ILogger<ManagementEndpointModule> logger
    )
    {
        logger.LogInformation("Claimsets reload requested via management endpoint");

        var response = await apiService.ReloadClaimsetsAsync();

        return response.StatusCode switch
        {
            200 => Results.Ok(response.Body),
            404 => Results.NotFound(),
            500 => Results.Json(response.Body, statusCode: 500),
            _ => Results.StatusCode(response.StatusCode),
        };
    }

    /// <summary>
    /// Reload claimsets for multi-tenant deployments (tenant required and validated)
    /// </summary>
    internal static async Task<IResult> ReloadClaimsetsTenantAware(
        string tenant,
        IApiService apiService,
        ITenantValidator tenantValidator,
        ILogger<ManagementEndpointModule> logger
    )
    {
        // Validate tenant exists
        if (!await tenantValidator.ValidateTenantAsync(tenant))
        {
            return Results.NotFound(
                new
                {
                    detail = "The specified resource could not be found.",
                    type = "urn:ed-fi:api:not-found",
                    title = "Not Found",
                    status = 404,
                }
            );
        }

        logger.LogInformation("Claimsets reload requested via management endpoint for tenant");

        var response = await apiService.ReloadClaimsetsAsync(tenant);

        return response.StatusCode switch
        {
            200 => Results.Ok(response.Body),
            404 => Results.NotFound(),
            500 => Results.Json(response.Body, statusCode: 500),
            _ => Results.StatusCode(response.StatusCode),
        };
    }

    /// <summary>
    /// Returns 404 when reload-claimsets is called without a tenant in multi-tenant mode
    /// </summary>
    internal static IResult ReloadClaimsetsNotFound()
    {
        return Results.NotFound(
            new
            {
                detail = "The specified resource could not be found.",
                type = "urn:ed-fi:api:not-found",
                title = "Not Found",
                status = 404,
            }
        );
    }

    /// <summary>
    /// View claimsets for single-tenant deployments (no tenant required)
    /// </summary>
    internal static async Task<IResult> ViewClaimsets(
        IApiService apiService,
        ILogger<ManagementEndpointModule> logger
    )
    {
        logger.LogInformation("View claimsets requested via management endpoint");

        var response = await apiService.ViewClaimsetsAsync();

        return response.StatusCode switch
        {
            200 => Results.Ok(response.Body),
            404 => Results.NotFound(),
            500 => Results.Json(response.Body, statusCode: 500),
            _ => Results.StatusCode(response.StatusCode),
        };
    }

    /// <summary>
    /// View claimsets for multi-tenant deployments (tenant required and validated)
    /// </summary>
    internal static async Task<IResult> ViewClaimsetsTenantAware(
        string tenant,
        IApiService apiService,
        ITenantValidator tenantValidator,
        ILogger<ManagementEndpointModule> logger
    )
    {
        // Validate tenant exists
        if (!await tenantValidator.ValidateTenantAsync(tenant))
        {
            return Results.NotFound(
                new
                {
                    detail = "The specified resource could not be found.",
                    type = "urn:ed-fi:api:not-found",
                    title = "Not Found",
                    status = 404,
                }
            );
        }

        logger.LogInformation("View claimsets requested via management endpoint for tenant");

        var response = await apiService.ViewClaimsetsAsync(tenant);

        return response.StatusCode switch
        {
            200 => Results.Ok(response.Body),
            404 => Results.NotFound(),
            500 => Results.Json(response.Body, statusCode: 500),
            _ => Results.StatusCode(response.StatusCode),
        };
    }

    /// <summary>
    /// Returns 404 when view-claimsets is called without a tenant in multi-tenant mode
    /// </summary>
    internal static IResult ViewClaimsetsNotFound()
    {
        return Results.NotFound(
            new
            {
                detail = "The specified resource could not be found.",
                type = "urn:ed-fi:api:not-found",
                title = "Not Found",
                status = 404,
            }
        );
    }
}
