// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

public sealed class CdcBindingStateStoreOptions
{
    public string RootPath { get; set; } = LocalCdcBindingStateStore.DefaultRootPath;
}

public static class CdcServiceCollectionExtensions
{
    public static IServiceCollection AddDmsCdcControlPlane(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<CdcBindingStateStoreOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICdcBindingLifecycleService, CdcBindingLifecycleService>();
        services.TryAdd(
            ServiceDescriptor.Singleton<ICdcLocalStateStorePermissions>(CdcLocalStateStorePermissions.Current)
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<ICdcBindingStateStore>(serviceProvider =>
            {
                CdcBindingStateStoreOptions options = serviceProvider
                    .GetRequiredService<IOptions<CdcBindingStateStoreOptions>>()
                    .Value;

                return new LocalCdcBindingStateStore(
                    options.RootPath,
                    serviceProvider.GetRequiredService<ICdcLocalStateStorePermissions>(),
                    CdcLocalStateStoreFileSystem.Current,
                    serviceProvider.GetRequiredService<TimeProvider>()
                );
            })
        );

        return services;
    }
}
