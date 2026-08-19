// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class HealthCheckEndpointModule(
    IOptions<DocumentCacheOptions> documentCacheOptions,
    IOptions<JwtAuthenticationOptions> jwtAuthenticationOptions,
    ILogger<HealthCheckEndpointModule> logger
) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", GetHealthStatus);

        if (!documentCacheOptions.Value.Status.TryGetRequiredRoleForEndpointMapping(out _))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(jwtAuthenticationOptions.Value.RoleClaimType))
        {
            logger.LogWarning(
                "DocumentCache status endpoint was not mapped because JwtAuthentication:RoleClaimType is missing or blank"
            );
            return;
        }

        endpoints.MapGet("/health/document-cache", GetDocumentCacheStatus).ExcludeFromDescription();
    }

    internal static async Task GetHealthStatus(HttpContext httpContext, HealthCheckService healthCheckService)
    {
        var healthReport = await healthCheckService.CheckHealthAsync();

        var healthResponse = new
        {
            Status = healthReport.Status.ToString(),
            Results = healthReport.Entries.Select(entry => new
            {
                Name = entry.Key,
                Status = entry.Value.Status.ToString(),
                entry.Value.Description,
            }),
        };

        await httpContext.Response.WriteAsSerializedJsonAsync(healthResponse);
    }

    internal static async Task GetDocumentCacheStatus(
        HttpContext httpContext,
        IDocumentCacheStatusAuthorizationService authorizationService,
        IDocumentCacheStatusService documentCacheStatusService
    )
    {
        DocumentCacheStatusAuthorizationResult authorizationResult =
            await authorizationService.AuthorizeAsync(
                GetAuthorizationHeader(httpContext),
                httpContext.RequestAborted
            );

        if (!authorizationResult.IsAuthorized)
        {
            await WriteAuthorizationFailureAsync(httpContext, authorizationResult);
            return;
        }

        DocumentCacheStatusResponse status = await documentCacheStatusService.GetStatusAsync(
            httpContext.RequestAborted
        );

        await httpContext.Response.WriteAsSerializedJsonAsync(status);
    }

    private static string? GetAuthorizationHeader(HttpContext httpContext) =>
        httpContext.Request.Headers.TryGetValue("Authorization", out var authorizationHeader)
            ? authorizationHeader.ToString()
            : null;

    private static async Task WriteAuthorizationFailureAsync(
        HttpContext httpContext,
        DocumentCacheStatusAuthorizationResult authorizationResult
    )
    {
        TraceId traceId = new(httpContext.TraceIdentifier);
        httpContext.Response.ContentType = "application/problem+json";

        if (authorizationResult.Outcome == DocumentCacheStatusAuthorizationOutcome.Unauthorized)
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            httpContext.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            await httpContext.Response.WriteAsync(
                FailureResponse
                    .ForAuthenticationFailure(traceId, [authorizationResult.Message ?? "Invalid token"])
                    .ToJsonString()
            );
            return;
        }

        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        await httpContext.Response.WriteAsync(
            FailureResponse
                .ForForbidden(traceId, [authorizationResult.Message ?? "Insufficient permissions"])
                .ToJsonString()
        );
    }
}
