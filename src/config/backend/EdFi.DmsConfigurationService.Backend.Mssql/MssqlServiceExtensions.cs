// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddTransient<IVendorRepository, VendorRepository>();
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
        return services;
    }
}
