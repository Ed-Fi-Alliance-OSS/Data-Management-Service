// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Cdc;

public static class CdcCommandServices
{
    /// <summary>Provider controllers without CMS, schema initialization or projection composition.</summary>
    public static IServiceCollection AddCdcCommandControlPlane(
        this IServiceCollection services,
        IConfiguration settings,
        Serilog.ILogger logger
    )
    {
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(logger);
        if (settings["AppSettings:Datastore"] == "postgresql")
        {
            services.AddPostgresqlDmsCdcControlPlane();
        }
        else
        {
            services.AddMssqlDmsCdcControlPlane();
        }
        services.AddCdcProviderSetup();
        services.AddCdcConnectorTemplates();
        return services;
    }
}
