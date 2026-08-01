// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Registers the natural-key reference resolver as the request-scoped <see cref="IReferenceResolver" />.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="ReferenceResolverServiceCollectionExtensions" />: through Phase 3
/// both resolvers are registerable, and the dialect entry points
/// (<c>Add&lt;Dialect&gt;NaturalKeyReferenceResolver</c>) call this first so its
/// <see cref="IReferenceResolver" /> registration wins the subsequent <c>TryAdd</c> in the shared
/// composition surface, which is otherwise reused verbatim.
/// </remarks>
public static class NaturalKeyReferenceResolverServiceCollectionExtensions
{
    public static IServiceCollection AddNaturalKeyReferenceResolver<TNaturalKeyLookupAdapterFactory>(
        this IServiceCollection services
    )
        where TNaturalKeyLookupAdapterFactory : class, INaturalKeyLookupAdapterFactory
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAdd(
            ServiceDescriptor.Scoped<IReferenceResolver>(
                static serviceProvider => new NaturalKeyReferenceResolver(
                    serviceProvider.GetRequiredService<INaturalKeyLookupAdapter>(),
                    serviceProvider.GetService<ILogger<NaturalKeyReferenceResolver>>()
                )
            )
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<INaturalKeyLookupAdapterFactory, TNaturalKeyLookupAdapterFactory>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<INaturalKeyLookupAdapter>(static serviceProvider =>
                serviceProvider.GetRequiredService<INaturalKeyLookupAdapterFactory>().CreateAdapter()
            )
        );

        return services;
    }
}
