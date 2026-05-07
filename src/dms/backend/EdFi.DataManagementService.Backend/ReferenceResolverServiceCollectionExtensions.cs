// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Profile;
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
        TRelationalCommandExecutor,
        TRelationalWriteSessionFactory,
        TDocumentHydrator,
        TSessionDocumentHydrator
    >(this IServiceCollection services)
        where TReferenceResolverAdapterFactory : class, IReferenceResolverAdapterFactory
        where TRelationalCommandExecutor : class, IRelationalCommandExecutor
        where TRelationalWriteSessionFactory : class, IRelationalWriteSessionFactory
        where TDocumentHydrator : class, IDocumentHydrator
        where TSessionDocumentHydrator : class, ISessionDocumentHydrator
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();
        services.TryAdd(ServiceDescriptor.Scoped<IRelationalCommandExecutor, TRelationalCommandExecutor>());
        services.TryAdd(
            ServiceDescriptor.Scoped<IRelationalWriteSessionFactory, TRelationalWriteSessionFactory>()
        );
        services.Replace(ServiceDescriptor.Scoped<IDocumentHydrator, TDocumentHydrator>());
        services.TryAdd(ServiceDescriptor.Scoped<IRelationalWriteFlattener, RelationalWriteFlattener>());
        services.TryAdd(ServiceDescriptor.Scoped<ISessionDocumentHydrator, TSessionDocumentHydrator>());
        services.TryAdd(ServiceDescriptor.Scoped<IRelationalReadMaterializer, RelationalReadMaterializer>());
        services.TryAdd(
            ServiceDescriptor.Scoped<IRelationalReadTargetLookupService, RelationalReadTargetLookupService>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IRelationalWriteCurrentStateLoader, RelationalWriteCurrentStateLoader>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IRelationalCurrentEtagPreconditionChecker,
                RelationalCurrentEtagPreconditionChecker
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IRelationalCommittedRepresentationReader,
                RelationalCommittedRepresentationReader
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IRelationalWriteFreshnessChecker, RelationalWriteFreshnessChecker>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IRelationalWriteNoProfileMergeSynthesizer,
                RelationalWriteNoProfileMergeSynthesizer
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IProfileRootTableBindingClassifier, ProfileRootTableBindingClassifier>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IProfileRootKeyUnificationResolver, ProfileRootKeyUnificationResolver>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IProfileSeparateTableBindingClassifier,
                ProfileSeparateTableBindingClassifier
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IProfileSeparateTableKeyUnificationResolver,
                ProfileSeparateTableKeyUnificationResolver
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IProfileSeparateTableMergeDecider, ProfileSeparateTableMergeDecider>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IRelationalWriteProfileMergeSynthesizer,
                RelationalWriteProfileMergeSynthesizer
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IRelationalWritePersister, RelationalWriteNoProfilePersister>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IRelationalWriteExceptionClassifier,
                NoOpRelationalWriteExceptionClassifier
            >()
        );
        services.TryAdd(ServiceDescriptor.Scoped<IDescriptorReadHandler, DescriptorReadHandler>());
        services.TryAdd(ServiceDescriptor.Scoped<IDescriptorWriteHandler, DescriptorWriteHandler>());
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IRelationalWriteTargetLookupService,
                RelationalWriteTargetLookupService
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IRelationalWriteTargetLookupResolver,
                RelationalWriteTargetLookupResolver
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IRelationalWriteConstraintResolver, RelationalWriteConstraintResolver>()
        );
        // Singleton so the per-model-set ConditionalWeakTable cache is reused across requests.
        // The cache holds weak references to the DerivedRelationalModelSet, so it still tracks
        // mapping-set swaps without leaking.
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IRelationalDeleteConstraintResolver,
                RelationalDeleteConstraintResolver
            >()
        );
        services.TryAdd(ServiceDescriptor.Scoped<IRelationalWriteExecutor, DefaultRelationalWriteExecutor>());

        return services.AddReferenceResolver<TReferenceResolverAdapterFactory>();
    }
}
