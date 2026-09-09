// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.DocumentCacheRuntime;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace EdFi.DataManagementService.SchemaTools.Cdc;

public static class CdcCommandServices
{
    public static IServiceCollection AddCdcCommandRuntime(
        this IServiceCollection services,
        IConfiguration settings,
        Serilog.ILogger logger,
        DocumentCacheTargetKey targetKey
    )
    {
        services.AddLogging(b => b.ClearProviders());
        services.AddDocumentCacheRuntimeServices(
            settings,
            logger,
            targetKey,
            DocumentCacheRuntimeTargetSelection.RequireConfiguredMembership
        );
        services.AddCdcDownstreamPublicationHistory(settings);
        CdcComposeDataStoreProvider.Register(services, settings, targetKey);
        return services.AddCdcCommandControlPlane(settings, logger);
    }

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
