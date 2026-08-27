// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend.Mssql;

public static class MssqlServiceExtensions
{
    /// <summary>
    /// The SQL Server backend datastore configuration with per-request connection string support.
    /// </summary>
    public static IServiceCollection AddMssqlDatastore(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddRelationalMappingSetServices(configuration, SqlDialect.Mssql, new MssqlDialectRules());

        // The single SQL Server acquisition boundary, shared by every seam that opens a connection for
        // a request, so all of them realize the same effective connection string for a given target.
        services.TryAddSingleton<IMssqlConnectionAcquisition>(new MssqlConnectionAcquisition());

        // Clearing one exact SqlClient pool is a lifecycle action no caller performs yet; the seam is
        // registered here so the eventual retirement path has one adapter to use and so that
        // SqlConnection.ClearAllPools stays absent from the codebase.
        services.TryAddSingleton<ISqlServerPoolClearing, SqlClientPoolClearing>();

        return services;
    }

    /// <summary>
    /// Adds the SQL Server relational services required for shared DocumentCache target
    /// resolution, status, projection, and administrative command execution.
    /// </summary>
    public static IServiceCollection AddMssqlDocumentCacheRuntimeServices(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddMssqlDatastore(configuration);
        services.AddMssqlReferenceResolver();
        services.AddMssqlRelationalTokenInfoEducationOrganizationLookup();
        services.Replace(
            ServiceDescriptor.Singleton<IDatabaseFingerprintReader, MssqlDatabaseFingerprintReader>()
        );
        services.Replace(
            ServiceDescriptor.Singleton<
                IDocumentCachePhysicalSourceFingerprintReader,
                MssqlDocumentCachePhysicalSourceFingerprintReader
            >()
        );
        services.Replace(
            ServiceDescriptor.Singleton<
                IDocumentCacheInventoryValidator,
                MssqlDocumentCacheInventoryValidator
            >()
        );
        services.Replace(
            ServiceDescriptor.Singleton<IDocumentCacheLifecycleReader, MssqlDocumentCacheLifecycleReader>()
        );
        services.Replace(
            ServiceDescriptor.Singleton<
                IDocumentCacheProviderPrerequisiteValidator,
                MssqlDocumentCacheProviderPrerequisiteValidator
            >()
        );
        services.Replace(ServiceDescriptor.Singleton<IResourceKeyRowReader, MssqlResourceKeyRowReader>());

        return services;
    }
}
