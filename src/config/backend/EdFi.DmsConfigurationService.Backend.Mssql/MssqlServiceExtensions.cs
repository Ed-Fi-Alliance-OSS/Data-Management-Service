// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql;

/// <summary>
/// The Backend SQL Server service extensions to be registered to a Frontend DI container.
/// Connections are created per-operation from DatabaseOptions, so no shared data source
/// is registered here.
/// </summary>
public static class MssqlServiceExtensions
{
    public static IServiceCollection AddMssqlDatastore(this IServiceCollection services)
    {
        services.AddMssqlSessionLanguagePin();
        services.AddTransient<IVendorRepository, VendorRepository>();
        services.AddTransient<IOwnershipTokenRepository, OwnershipTokenRepository>();
        services.AddTransient<IApplicationRepository, ApplicationRepository>();
        services.AddTransient<IApiClientRepository, ApiClientRepository>();
        services.AddTransient<ITenantRepository, TenantRepository>();
        services.AddTransient<IProfileRepository, ProfileRepository>();
        services.AddTransient<IDataStoreDerivativeRepository, DataStoreDerivativeRepository>();
        services.AddTransient<IDataStoreContextRepository, DataStoreContextRepository>();
        services.AddTransient<IDataStoreRepository, DataStoreRepository>();
        services.AddTransient<IClaimsHierarchyRepository, ClaimsHierarchyRepository>();
        services.AddTransient<IClaimSetRepository, ClaimSetRepository>();
        services.AddTransient<IClaimsDocumentRepository, ClaimsDocumentRepository>();
        services.AddTransient<IResourceClaimRepository, ResourceClaimRepository>();
        services.AddTransient<IClaimsTableValidator, ClaimsDataLoader.ClaimsTableValidator>();
        services.AddTransient<
            IResourceClaimMetadataRepository,
            ClaimsDataLoader.ResourceClaimMetadataRepository
        >();
        services.AddTransient<IClaimSetDataProvider, ClaimSetDataProvider>();
        services.AddTransient<IApplicationLockManager, MssqlApplicationLockManager>();
        return services;
    }

    /// <summary>
    /// Registers the SQL Server adapter of the DMS provisioning engine port (spec 4.4) for
    /// <c>DmsDataStoreProvisioning:Provider = mssql</c>, independently of the catalog datastore. The engine reads
    /// its settings, including the staging directory, its server view, and the staged file mode, from the
    /// statically validated options and opens every connection with the resolution the calling operation bound
    /// (<see cref="IDmsAdminConnectionSource.Current"/>), so overlapping operations never share one; it is built
    /// without contacting the server or the staging directory. Requires the options, the connection source, and a
    /// <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddMssqlDmsTargetEngine(this IServiceCollection services)
    {
        services.AddSingleton<IDmsTargetEngine>(provider =>
        {
            DmsDataStoreProvisioningOptions options = provider
                .GetRequiredService<IOptions<DmsDataStoreProvisioningOptions>>()
                .Value;
            IDmsAdminConnectionSource source = provider.GetRequiredService<IDmsAdminConnectionSource>();
            DmsDataStoreProvisioningOptions.MssqlOptions mssql =
                options.Mssql
                ?? throw new InvalidOperationException(
                    "DmsDataStoreProvisioning:Mssql is missing; static validation should have refused it."
                );
            return new MssqlDmsTargetEngine(
                new MssqlAdminConnection(
                    () => source.Current,
                    options.CommandTimeout,
                    provider.GetRequiredService<TimeProvider>()
                ),
                new MssqlDmsEngineSettings(
                    options.LockAcquireTimeout,
                    options.LockHeartbeatInterval,
                    options.SessionTerminationTimeout,
                    options.RestoreTimeout,
                    new MssqlStagingSettings(
                        Required(mssql.StagingPath, "StagingPath"),
                        Required(mssql.StagingServerPath, "StagingServerPath"),
                        MssqlStagingSettings.ParseFileMode(
                            Required(mssql.StagingFileMode, "StagingFileMode")
                        ),
                        options.CleanupAttempts,
                        options.CommandTimeout
                    )
                ),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<MssqlDmsTargetEngine>>()
            );
        });
        return services;
    }

    private static string Required(string? value, string key) =>
        value
        ?? throw new InvalidOperationException(
            $"DmsDataStoreProvisioning:Mssql:{key} is missing; static validation should have refused it."
        );

    /// <summary>
    /// Pins the session language of the SQL Server connection (see <see cref="PinMssqlSessionLanguage"/>).
    /// Both SQL Server registrations call it, and the pin is registered once however often it is called.
    /// </summary>
    internal static IServiceCollection AddMssqlSessionLanguagePin(this IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<DatabaseOptions>, PinMssqlSessionLanguage>()
        );
        return services;
    }
}
