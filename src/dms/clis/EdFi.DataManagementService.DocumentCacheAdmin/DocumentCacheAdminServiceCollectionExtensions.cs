// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Cdc.Control;
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
        DocumentCacheTargetKey invocationTarget
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(invocationTarget);

        services.TryAddSingleton(configuration);

        services.AddOptions<AppSettings>().Bind(configuration.GetSection("AppSettings"));
        services
            .AddOptions<DocumentCacheOptions>()
            .Configure(options => ConfigureDocumentCacheOptions(configuration, invocationTarget, options))
            .ValidateOnStart();

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

        services.AddSingleton<IDocumentCacheAdminTargetResolver, DocumentCacheAdminTargetResolver>();
        services.AddSingleton<
            IDocumentCacheAdminMutatingCommandDispatcher,
            DocumentCacheAdminMutatingCommandDispatcher
        >();
        services.TryAddSingleton<IDocumentCacheAdminCliTelemetry, DocumentCacheAdminCliTelemetry>();

        // The CDC control plane branches on the same AppSettings:Datastore value the DocumentCache
        // runtime services below do, so the configuration is passed through rather than the branch being
        // repeated. Its own options are validated on first resolution, which happens only on a cdc verb,
        // so a DocumentCache-only invocation is unaffected by CDC configuration it does not use.
        services.AddDmsCdcControl(configuration);
        services.AddScoped<IDocumentCacheAdminCdcCommandDispatcher, DocumentCacheAdminCdcCommandDispatcher>();

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

    private static void ConfigureDocumentCacheOptions(
        IConfiguration configuration,
        DocumentCacheTargetKey invocationTarget,
        DocumentCacheOptions options
    )
    {
        IConfigurationRoot configurationWithoutConfiguredTargets =
            CreateConfigurationWithoutConfiguredTargets(configuration);

        configurationWithoutConfiguredTargets.GetSection(DocumentCacheOptions.SectionName).Bind(options);
        options.Targets =
        [
            new DocumentCacheTargetOptions
            {
                TenantKey = invocationTarget.TenantKey,
                DataStoreId = invocationTarget.DataStoreId,
            },
        ];
    }

    private static IConfigurationRoot CreateConfigurationWithoutConfiguredTargets(
        IConfiguration configuration
    )
    {
        const string documentCacheSectionName = DocumentCacheOptions.SectionName;
        const string targetsSectionName = $"{DocumentCacheOptions.SectionName}:Targets";
        string documentCacheSectionPrefix = $"{documentCacheSectionName}:";
        string targetsSectionPrefix = $"{targetsSectionName}:";
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string?> setting in configuration.AsEnumerable())
        {
            if (
                !string.Equals(setting.Key, documentCacheSectionName, StringComparison.OrdinalIgnoreCase)
                && !setting.Key.StartsWith(documentCacheSectionPrefix, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            if (
                string.Equals(setting.Key, targetsSectionName, StringComparison.OrdinalIgnoreCase)
                || setting.Key.StartsWith(targetsSectionPrefix, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            settings[setting.Key] = setting.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
