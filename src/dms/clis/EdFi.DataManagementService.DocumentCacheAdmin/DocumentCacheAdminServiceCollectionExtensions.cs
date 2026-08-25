// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal static class DocumentCacheAdminServiceCollectionExtensions
{
    public static IServiceCollection AddDocumentCacheAdminRuntimeServices(
        this IServiceCollection services,
        IConfiguration configuration,
        ILogger logger,
        DocumentCacheTargetKey? invocationTarget = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        services.AddOptions<AppSettings>().Bind(configuration.GetSection("AppSettings"));

        services
            .AddDmsDefaultConfiguration(
                logger,
                configuration.GetSection("CircuitBreaker"),
                configuration.GetSection("DeadlockRetry"),
                maskRequestBodyInLogs: false
            )
            .AddDmsConfigurationServiceDataStoreProvider(configuration)
            .AddDmsDocumentCacheTargetRegistry(configuration)
            .AddDocumentCacheProjectionSupervisor(registerHostedService: false);

        if (invocationTarget is not null)
        {
            services.Configure<DocumentCacheOptions>(options =>
            {
                options.Targets =
                [
                    new DocumentCacheTargetOptions
                    {
                        TenantKey = invocationTarget.TenantKey,
                        DataStoreId = invocationTarget.DataStoreId,
                    },
                ];
            });
        }

        services.AddSingleton<IDocumentCacheAdminTargetResolver, DocumentCacheAdminTargetResolver>();
        services.AddSingleton<
            IDocumentCacheAdminMutatingCommandDispatcher,
            DocumentCacheAdminMutatingCommandDispatcher
        >();
        services.TryAddSingleton<IDocumentCacheAdminCliTelemetry, DocumentCacheAdminCliTelemetry>();

        string datastore = configuration.GetSection("AppSettings:Datastore").Value ?? string.Empty;
        if (string.Equals(datastore, "postgresql", StringComparison.OrdinalIgnoreCase))
        {
            services.AddPostgresqlDocumentCacheRuntimeServices(configuration);
            return services;
        }

        if (string.Equals(datastore, "mssql", StringComparison.OrdinalIgnoreCase))
        {
            services.AddMssqlDocumentCacheRuntimeServices(configuration);
            return services;
        }

        throw new InvalidOperationException("AppSettings:Datastore must be one of: postgresql, mssql");
    }
}
