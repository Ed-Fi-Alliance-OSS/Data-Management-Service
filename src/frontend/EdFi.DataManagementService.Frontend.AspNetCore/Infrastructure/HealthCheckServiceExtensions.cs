// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public static class HealthCheckServiceExtensions
{
    public static IServiceCollection AddHealthCheck(
        this IServiceCollection services,
        WebApplicationBuilder webAppBuilder
    )
    {
        var hcBuilder = services.AddHealthChecks();
        string connectionString =
            webAppBuilder.Configuration.GetSection("ConnectionStrings:DatabaseConnection").Value
            ?? string.Empty;

        if (
            string.Equals(
                webAppBuilder.Configuration.GetSection("AppSettings:Datastore").Value,
                "postgresql",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            hcBuilder.AddNpgSql(connectionString);
        }
        else
        {
            hcBuilder.AddSqlServer(connectionString);
        }

        return services;
    }
}
