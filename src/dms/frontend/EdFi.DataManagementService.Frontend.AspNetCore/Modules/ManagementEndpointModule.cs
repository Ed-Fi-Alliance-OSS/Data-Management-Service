// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

/// <summary>
/// Management endpoints for administrative tasks.
/// In multi-tenant deployments, claimset endpoints require a tenant segment in the route.
/// </summary>
public class ManagementEndpointModule(IOptions<AppSettings> options) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        bool multiTenancy = options.Value.MultiTenancy;

        var managementEndpoints = endpoints.MapGroup("/management");

        // Reload ApiSchema endpoint (not tenant-aware)
        managementEndpoints
            .MapPost("/reload-api-schema", ReloadApiSchema)
            .WithName("ReloadApiSchema")
            .WithSummary("Reloads the ApiSchema from the configured source");

        // Upload ApiSchema endpoint (not tenant-aware)
        managementEndpoints
            .MapPost("/upload-api-schema", UploadApiSchema)
            .WithName("UploadApiSchema")
            .WithSummary("Uploads ApiSchema from request body")
            .Accepts<UploadSchemaRequest>("application/json")
            .Produces<UploadSchemaResponse>(200)
            .Produces(400)
            .Produces(404)
            .Produces(500);

        // Claimset endpoints - tenant-aware in multi-tenant deployments
        if (multiTenancy)
        {
            // Multi-tenant: require tenant in route
            managementEndpoints
                .MapPost("/{tenant}/reload-claimsets", ReloadClaimsetsTenantAware)
                .WithName("ReloadClaimsets")
                .WithSummary("Reloads the Claimsets from the configured source for a specific tenant");

            managementEndpoints
                .MapGet("/{tenant}/view-claimsets", ViewClaimsetsTenantAware)
                .WithName("ViewClaimsets")
                .WithSummary("Views the current Claimsets configuration for a specific tenant");

            // Map non-tenant routes to return 404 in multi-tenant mode
            managementEndpoints
                .MapPost("/reload-claimsets", ReloadClaimsetsNotFound)
                .WithName("ReloadClaimsetsNoTenant")
                .ExcludeFromDescription();

            managementEndpoints
                .MapGet("/view-claimsets", ViewClaimsetsNotFound)
                .WithName("ViewClaimsetsNoTenant")
                .ExcludeFromDescription();
        }
        else
        {
            // Single-tenant: no tenant required
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

    internal static async Task<IResult> ReloadApiSchema(
        IApiService apiService,
        ILogger<ManagementEndpointModule> logger
    )
    {
        logger.LogInformation("Schema reload requested via management endpoint");

        var response = await apiService.ReloadApiSchemaAsync();

        return response.StatusCode switch
        {
            200 => Results.Ok(response.Body),
            404 => Results.NotFound(),
            500 => Results.Json(response.Body, statusCode: 500),
            _ => Results.StatusCode(response.StatusCode),
        };
    }

    internal static async Task<IResult> UploadApiSchema(
        UploadSchemaRequest request,
        IApiService apiService,
        ILogger<ManagementEndpointModule> logger
    )
    {
        logger.LogInformation("Schema upload requested via management endpoint");

        var response = await apiService.UploadApiSchemaAsync(request);

        return response.StatusCode switch
        {
            200 => Results.Json(response.Body, statusCode: 200),
            400 => Results.Json(response.Body, statusCode: 400),
            404 => Results.NotFound(),
            500 => Results.Json(response.Body, statusCode: 500),
            _ => Results.StatusCode(response.StatusCode),
        };
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
