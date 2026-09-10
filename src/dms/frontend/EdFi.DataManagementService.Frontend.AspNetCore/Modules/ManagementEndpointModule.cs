// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Management;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using Microsoft.Extensions.Options;
using FrontendAppSettings = EdFi.DataManagementService.Frontend.AspNetCore.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

/// <summary>
/// Management endpoints for administrative tasks.
/// In multi-tenant deployments, claimset endpoints require a tenant segment in the route.
/// </summary>
public class ManagementEndpointModule(
    IOptions<FrontendAppSettings> options,
    IOptions<ManagementEndpointsOptions> managementEndpointsOptions,
    IOptions<JwtAuthenticationOptions> jwtAuthenticationOptions,
    ILogger<ManagementEndpointModule> logger
) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        bool multiTenancy = options.Value.MultiTenancy;

        var managementEndpoints = endpoints.MapGroup("/management");

        // Fail closed at mapping time rather than at request time: without a usable required role
        // there is no way to authorize these endpoints, so they are not exposed at all.
        if (!IsRequiredRoleUsable())
        {
            return;
        }

        if (multiTenancy)
        {
            // The unscoped forms preserve the multi-tenant tenant-required 404 for authorized callers
            // while still denying unauthenticated and wrong-role callers before any response detail.
            managementEndpoints
                .MapPost("/reload-claimsets", ClaimsetsNotFound)
                .WithName("ReloadClaimsetsNoTenant")
                .ExcludeFromDescription();

            managementEndpoints
                .MapGet("/view-claimsets", ClaimsetsNotFound)
                .WithName("ViewClaimsetsNoTenant")
                .ExcludeFromDescription();

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
    /// True when a request to a claimset management endpoint could be authorized.
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

        logger.LogWarning(
            "Claimset management endpoints were not mapped because AppSettings:ManagementEndpoints:RequiredRole is missing or invalid, or JwtAuthentication:RoleClaimType is missing or blank. Configure a single role token such as dms-management-operator."
        );

        return false;
    }

    /// <summary>
    /// Returns the failure result to send, or null when the caller is authorized. Every protected
    /// handler calls this before touching <see cref="IApiService"/>, so a denied request never
    /// invalidates the claimset cache and never reaches the Configuration Service.
    /// </summary>
    private static async Task<IResult?> AuthorizeAsync(
        HttpContext httpContext,
        IManagementEndpointAuthorizationService authorizationService
    )
    {
        string? authorizationHeader = httpContext.Request.Headers.TryGetValue(
            "Authorization",
            out var headerValues
        )
            ? headerValues.ToString()
            : null;

        EndpointRoleAuthorizationResult authorizationResult = await authorizationService.AuthorizeAsync(
            authorizationHeader,
            httpContext.RequestAborted
        );

        if (authorizationResult.IsAuthorized)
        {
            return null;
        }

        TraceId traceId = new(httpContext.TraceIdentifier);

        if (authorizationResult.Outcome == EndpointRoleAuthorizationOutcome.Unauthorized)
        {
            httpContext.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            return Results.Text(
                FailureResponse
                    .ForAuthenticationFailure(traceId, [authorizationResult.Message ?? "Invalid token"])
                    .ToJsonString(),
                "application/problem+json",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        return Results.Text(
            FailureResponse
                .ForForbidden(traceId, [authorizationResult.Message ?? "Insufficient permissions"])
                .ToJsonString(),
            "application/problem+json",
            statusCode: StatusCodes.Status403Forbidden
        );
    }

    private static IResult ToResult(IFrontendResponse response) =>
        response.StatusCode switch
        {
            200 => Results.Ok(response.Body),
            404 => Results.NotFound(),
            500 => Results.Json(response.Body, statusCode: 500),
            _ => Results.StatusCode(response.StatusCode),
        };

    private static IResult NotFoundProblem() =>
        Results.NotFound(
            new
            {
                detail = "The specified resource could not be found.",
                type = "urn:ed-fi:api:not-found",
                title = "Not Found",
                status = 404,
            }
        );

    /// <summary>
    /// Reload claimsets for single-tenant deployments (no tenant required)
    /// </summary>
    internal static async Task<IResult> ReloadClaimsets(
        HttpContext httpContext,
        IManagementEndpointAuthorizationService authorizationService,
        IApiService apiService,
        ILogger<ManagementEndpointModule> logger
    )
    {
        IResult? authorizationFailure = await AuthorizeAsync(httpContext, authorizationService);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        logger.LogInformation("Claimsets reload requested via management endpoint");

        return ToResult(await apiService.ReloadClaimsetsAsync());
    }

    /// <summary>
    /// Reload claimsets for multi-tenant deployments (tenant required and validated)
    /// </summary>
    internal static async Task<IResult> ReloadClaimsetsTenantAware(
        string tenant,
        HttpContext httpContext,
        IManagementEndpointAuthorizationService authorizationService,
        IApiService apiService,
        ITenantValidator tenantValidator,
        ILogger<ManagementEndpointModule> logger
    )
    {
        // Authorization precedes tenant validation so an anonymous caller cannot probe tenant existence.
        IResult? authorizationFailure = await AuthorizeAsync(httpContext, authorizationService);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        if (!await tenantValidator.ValidateTenantAsync(tenant))
        {
            return NotFoundProblem();
        }

        logger.LogInformation("Claimsets reload requested via management endpoint for tenant");

        return ToResult(await apiService.ReloadClaimsetsAsync(tenant));
    }

    /// <summary>
    /// Returns 404 when a claimset management endpoint is called without a tenant in multi-tenant mode.
    /// </summary>
    internal static async Task<IResult> ClaimsetsNotFound(
        HttpContext httpContext,
        IManagementEndpointAuthorizationService authorizationService
    )
    {
        IResult? authorizationFailure = await AuthorizeAsync(httpContext, authorizationService);
        return authorizationFailure ?? NotFoundProblem();
    }

    /// <summary>
    /// View claimsets for single-tenant deployments (no tenant required)
    /// </summary>
    internal static async Task<IResult> ViewClaimsets(
        HttpContext httpContext,
        IManagementEndpointAuthorizationService authorizationService,
        IApiService apiService,
        ILogger<ManagementEndpointModule> logger
    )
    {
        IResult? authorizationFailure = await AuthorizeAsync(httpContext, authorizationService);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        logger.LogInformation("View claimsets requested via management endpoint");

        return ToResult(await apiService.ViewClaimsetsAsync());
    }

    /// <summary>
    /// View claimsets for multi-tenant deployments (tenant required and validated)
    /// </summary>
    internal static async Task<IResult> ViewClaimsetsTenantAware(
        string tenant,
        HttpContext httpContext,
        IManagementEndpointAuthorizationService authorizationService,
        IApiService apiService,
        ITenantValidator tenantValidator,
        ILogger<ManagementEndpointModule> logger
    )
    {
        // Authorization precedes tenant validation so an anonymous caller cannot probe tenant existence.
        IResult? authorizationFailure = await AuthorizeAsync(httpContext, authorizationService);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        if (!await tenantValidator.ValidateTenantAsync(tenant))
        {
            return NotFoundProblem();
        }

        logger.LogInformation("View claimsets requested via management endpoint for tenant");

        return ToResult(await apiService.ViewClaimsetsAsync(tenant));
    }
}
