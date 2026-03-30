// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend;

public static class ReferenceResolverServiceCollectionExtensions
{
    public static IServiceCollection AddReferenceResolver<TReferenceResolverAdapterFactory>(
        this IServiceCollection services
    )
        where TReferenceResolverAdapterFactory : class, IReferenceResolverAdapterFactory
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAdd(ServiceDescriptor.Scoped<IReferenceResolver, ReferenceResolver>());
        services.TryAdd(
            ServiceDescriptor.Scoped<IReferenceResolverAdapterFactory, TReferenceResolverAdapterFactory>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IReferenceResolverAdapter>(static serviceProvider =>
                serviceProvider.GetRequiredService<IReferenceResolverAdapterFactory>().CreateAdapter()
            )
        );

        return services;
    }

    internal static IServiceCollection AddReferenceResolver<
        TReferenceResolverAdapterFactory,
        TRelationalCommandExecutor
    >(this IServiceCollection services)
        where TReferenceResolverAdapterFactory : class, IReferenceResolverAdapterFactory
        where TRelationalCommandExecutor : class, IRelationalCommandExecutor
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAdd(ServiceDescriptor.Scoped<IRelationalCommandExecutor, TRelationalCommandExecutor>());
        services.TryAdd(ServiceDescriptor.Scoped<IRelationalWriteFlattener, RelationalWriteFlattener>());
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IRelationalWriteTargetContextResolver,
                RelationalWriteTargetContextResolver
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IRelationalWriteTerminalStage, DefaultRelationalWriteTerminalStage>()
        );

        return services.AddReferenceResolver<TReferenceResolverAdapterFactory>();
    }
}
