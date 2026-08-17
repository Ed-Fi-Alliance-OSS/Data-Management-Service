// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class HealthCheckEndpointModule(IOptions<DocumentCacheOptions> documentCacheOptions) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", GetHealthStatus);

        if (documentCacheOptions.Value.Status.TryGetRequiredRoleForEndpointMapping(out _))
        {
            endpoints.MapGet("/health/document-cache", GetDocumentCacheStatus).ExcludeFromDescription();
        }
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
        IDocumentCacheStatusService documentCacheStatusService
    )
    {
        DocumentCacheStatusResponse status = await documentCacheStatusService.GetStatusAsync(
            httpContext.RequestAborted
        );

        await httpContext.Response.WriteAsSerializedJsonAsync(status);
    }
}
