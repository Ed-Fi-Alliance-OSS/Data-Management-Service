// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// The relational-safe PostgreSQL datastore services to be registered to a Frontend DI container.
/// </summary>
public static class PostgresqlServiceExtensions
{
    /// <summary>
    /// The PostgreSQL backend datastore configuration with per-request connection string support.
    /// </summary>
    public static IServiceCollection AddPostgresqlDatastore(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddRelationalMappingSetServices(configuration, SqlDialect.Pgsql, new PgsqlDialectRules());

        services.TryAddSingleton<NpgsqlDataSourceCache>();
        services.TryAddScoped<NpgsqlDataSourceProvider>();

        return services;
    }

    /// <summary>
    /// Adds the PostgreSQL relational services required for shared DocumentCache target
    /// resolution, status, projection, and administrative command execution.
    /// </summary>
    public static IServiceCollection AddPostgresqlDocumentCacheRuntimeServices(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddPostgresqlDatastore(configuration);
        services.AddPostgresqlReferenceResolver();
        services.AddPostgresqlRelationalTokenInfoEducationOrganizationLookup();
        services.Replace(
            ServiceDescriptor.Singleton<IDatabaseFingerprintReader, PostgresqlDatabaseFingerprintReader>()
        );
        services.Replace(
            ServiceDescriptor.Singleton<
                IDocumentCachePhysicalSourceFingerprintReader,
                PostgresqlDocumentCachePhysicalSourceFingerprintReader
            >()
        );
        services.Replace(
            ServiceDescriptor.Singleton<
                IDocumentCacheInventoryValidator,
                PostgresqlDocumentCacheInventoryValidator
            >()
        );
        services.Replace(
            ServiceDescriptor.Singleton<
                IDocumentCacheLifecycleReader,
                PostgresqlDocumentCacheLifecycleReader
            >()
        );
        services.Replace(
            ServiceDescriptor.Singleton<
                IDocumentCacheProviderPrerequisiteValidator,
                PostgresqlDocumentCacheProviderPrerequisiteValidator
            >()
        );
        services.Replace(
            ServiceDescriptor.Singleton<IResourceKeyRowReader, PostgresqlResourceKeyRowReader>()
        );

        return services;
    }
}
