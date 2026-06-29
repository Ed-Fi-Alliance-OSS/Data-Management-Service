// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend;

public static class RelationalMappingSetServiceCollectionExtensions
{
    public static IServiceCollection AddRelationalMappingSetServices(
        this IServiceCollection services,
        IConfiguration configuration,
        SqlDialect sqlDialect,
        ISqlDialectRules dialectRules
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dialectRules);

        services.Configure<MappingSetProviderOptions>(configuration.GetSection("MappingPacks"));
        services.TryAddSingleton<MappingSetCompiler>();
        services.TryAddSingleton<IMappingPackStore, NoOpMappingPackStore>();
        services.TryAddSingleton<IRuntimeMappingSetCompiler>(serviceProvider =>
        {
            var effectiveSchemaSetProvider =
                serviceProvider.GetRequiredService<IEffectiveSchemaSetProvider>();
            var mappingSetCompiler = serviceProvider.GetRequiredService<MappingSetCompiler>();

            return new RuntimeMappingSetCompiler(
                () => effectiveSchemaSetProvider.EffectiveSchemaSet,
                mappingSetCompiler,
                sqlDialect,
                dialectRules
            );
        });
        services.TryAddSingleton<IMappingSetProvider, MappingSetProvider>();

        return services;
    }
}
