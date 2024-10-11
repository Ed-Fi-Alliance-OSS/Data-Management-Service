// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class HealthCheckEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", GetHealthStatus);
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
                Description = entry.Value.Description
            })
        };

        await httpContext.Response.WriteAsSerializedJsonAsync(healthResponse);
    }
}
