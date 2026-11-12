// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql;

/// <summary>
/// The Backend PostgreSQL service extensions to be registered to a Frontend DI container
/// </summary>
public static class PostgresqlServiceExtensions
{
    /// <summary>
    /// The Postgresql backend datastore configuration
    /// </summary>
    /// <param name="connectionString">The PostgreSQL database connection string</param>
    public static IServiceCollection AddPostgresqlDatastore(
        this IServiceCollection services,
        string connectionString
    )
    {
        services.AddSingleton((sp) => NpgsqlDataSource.Create(connectionString));
        return services;
    }

    /// <summary>
    /// Registers the internal PostgreSQL application lock manager.
    /// </summary>
    public static IServiceCollection AddPostgresqlApplicationLockManager(this IServiceCollection services)
    {
        services.AddTransient<IApplicationLockManager, PostgresqlApplicationLockManager>();
        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL adapter of the DMS provisioning engine port (<see cref="IDmsTargetEngine"/>) for
    /// <c>DmsDataStoreProvisioning:Provider = postgresql</c>, independently of the catalog datastore. The
    /// engine reads its settings from the statically validated options and opens every connection with the
    /// resolution the calling operation bound (<see cref="IDmsAdminConnectionSource.Current"/>), so overlapping
    /// operations never share one, and it is built without contacting the server. Requires the options, the
    /// connection source, and a <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddPostgresqlDmsTargetEngine(this IServiceCollection services)
    {
        services.AddSingleton<IDmsTargetEngine>(provider =>
        {
            DmsDataStoreProvisioningOptions options = provider
                .GetRequiredService<IOptions<DmsDataStoreProvisioningOptions>>()
                .Value;
            IDmsAdminConnectionSource source = provider.GetRequiredService<IDmsAdminConnectionSource>();
            DmsDataStoreProvisioningOptions.PostgresqlOptions postgresql =
                options.Postgresql
                ?? throw new InvalidOperationException(
                    "DmsDataStoreProvisioning:Postgresql is missing; static validation should have refused it."
                );
            return new PostgresqlDmsTargetEngine(
                new PostgresqlAdminConnection(() => source.Current, options.CommandTimeout),
                new PostgresqlDmsEngineSettings(
                    options.LockAcquireTimeout,
                    options.LockHeartbeatInterval,
                    options.SessionTerminationTimeout,
                    options.RestoreTimeout,
                    postgresql.PsqlPath
                        ?? throw new InvalidOperationException(
                            "DmsDataStoreProvisioning:Postgresql:PsqlPath is missing; static validation should have refused it."
                        )
                )
                {
                    MinimumPsqlMajorVersion = postgresql.MinimumPsqlMajorVersion,
                },
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<PostgresqlDmsTargetEngine>>()
            );
        });
        return services;
    }
}
