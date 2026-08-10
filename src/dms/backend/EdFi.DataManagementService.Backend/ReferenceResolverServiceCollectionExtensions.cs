// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Interface;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        services.TryAdd(ServiceDescriptor.Scoped<IChangeQueryRepository, RelationalChangeQueryRepository>());
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IRelationalParameterConfigurator,
                DefaultRelationalParameterConfigurator
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IRelationalWriteSessionFactory, TRelationalWriteSessionFactory>()
        );
        services.Replace(ServiceDescriptor.Scoped<IDocumentHydrator, TDocumentHydrator>());
        services.TryAdd(ServiceDescriptor.Scoped<IRelationalWriteFlattener, RelationalWriteFlattener>());
        services.TryAdd(ServiceDescriptor.Scoped<ISessionDocumentHydrator, TSessionDocumentHydrator>());
        // Stateless composer for the ContentVersion-based _etag; singleton so it is reused.
        services.TryAdd(ServiceDescriptor.Singleton<IServedEtagComposer, ServedEtagComposer>());
        services.TryAdd(ServiceDescriptor.Scoped<IRelationalReadMaterializer, RelationalReadMaterializer>());
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IDocumentCacheMaterializationDataStore,
                AmbientDocumentCacheMaterializationDataStore
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IDocumentCacheSourceMetadataReader, DocumentCacheSourceMetadataReader>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IDocumentCacheDescriptorHydrator, DocumentCacheDescriptorHydrator>()
        );
        services.TryAdd(ServiceDescriptor.Scoped<IDocumentCacheMaterializer, DocumentCacheMaterializer>());
        services.TryAdd(
            ServiceDescriptor.Scoped<IDocumentCacheReadResponseShaper, DocumentCacheReadResponseShaper>()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentCacheReadTelemetry, DocumentCacheReadTelemetry>()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<ITransactionFaultInjectionObserver>(
                NoOpTransactionFaultInjectionObserver.Instance
            )
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentCacheWriterTelemetry, DocumentCacheWriterTelemetry>()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentCacheProjectionTelemetry, DocumentCacheProjectionTelemetry>()
        );
        services.TryAddSingleton(TimeProvider.System);
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheDownstreamPublicationHistoryProvider,
                DocumentCacheUnknownDownstreamPublicationHistoryProvider
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                DocumentCacheProjectionObservationStore,
                DocumentCacheProjectionObservationStore
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentCacheProjectionObservationProvider>(static serviceProvider =>
                serviceProvider.GetRequiredService<DocumentCacheProjectionObservationStore>()
            )
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentCacheProjectionObservationSink>(static serviceProvider =>
                serviceProvider.GetRequiredService<DocumentCacheProjectionObservationStore>()
            )
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentCacheProjectionTargetDiagnosticSink>(
                static serviceProvider =>
                    serviceProvider.GetRequiredService<DocumentCacheProjectionObservationStore>()
            )
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheProjectionTargetRuntimeContextFactory,
                DocumentCacheProjectionTargetRuntimeContextFactory
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheProjectionDrainPageProcessor,
                DocumentCacheProjectionDrainPageProcessor
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheProjectionItemProcessor,
                DocumentCacheProjectionItemProcessor
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentCacheProjectionScheduler, DocumentCacheProjectionScheduler>()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheAdministrativeCommandRunner,
                DocumentCacheAdministrativeCommandRunner
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheGuardedNewEmptyActivationCommand,
                DocumentCacheGuardedNewEmptyActivationCommand
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheOfflineActivationCommand,
                DocumentCacheOfflineActivationCommand
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheOfflineDeactivationCommand,
                DocumentCacheOfflineDeactivationCommand
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheOnlineCacheRebuildCommand,
                DocumentCacheOnlineCacheRebuildCommand
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheExplicitIntegrityScrubCommand,
                DocumentCacheExplicitIntegrityScrubCommand
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheInternalOnlyCacheAheadRecoveryCommand,
                DocumentCacheInternalOnlyCacheAheadRecoveryCommand
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentCacheBaselineSeedDelay, DocumentCacheBaselineSeedDelay>()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<IDocumentCacheBaselineSeeder, DocumentCacheBaselineSeeder>()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheAdministrativeDrainDelay,
                DocumentCacheAdministrativeDrainDelay
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Singleton<
                IDocumentCacheAdministrativeDrainer,
                DocumentCacheAdministrativeDrainer
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IDocumentCacheWriterRetryAdapter, DocumentCacheWriterRetryAdapter>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IRelationalReadTargetLookupService, RelationalReadTargetLookupService>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<ISingleRecordRelationshipAuthorizationExecutor>(
                static serviceProvider => new SingleRecordRelationshipAuthorizationExecutor(
                    serviceProvider.GetRequiredService<IRelationalCommandExecutor>(),
                    serviceProvider.GetService<IRelationalParameterConfigurator>(),
                    serviceProvider.GetService<IRelationshipAuthorizationProviderFailureExtractor>(),
                    serviceProvider.GetService<ILogger<SingleRecordRelationshipAuthorizationExecutor>>()
                )
            )
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<INamespaceAuthorizationExecutor, NamespaceAuthorizationExecutor>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<
                IRelationshipAuthorizationProviderFailureExtractor,
                DefaultRelationshipAuthorizationProviderFailureExtractor
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IRelationalWriteCurrentStateLoader, RelationalWriteCurrentStateLoader>()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<
                RelationalCurrentEtagPreconditionChecker,
                RelationalCurrentEtagPreconditionChecker
            >()
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IRelationalCurrentEtagPreconditionChecker>(static serviceProvider =>
                serviceProvider.GetRequiredService<RelationalCurrentEtagPreconditionChecker>()
            )
        );
        services.TryAdd(
            ServiceDescriptor.Scoped<IRelationalDeleteEtagPreconditionChecker>(static serviceProvider =>
                serviceProvider.GetRequiredService<RelationalCurrentEtagPreconditionChecker>()
            )
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
            ServiceDescriptor.Singleton<
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
        services.AddRelationalRelationshipAuthorizationServices();

        return services.AddReferenceResolver<TReferenceResolverAdapterFactory>();
    }

    internal static IServiceCollection AddDocumentCacheReadAccelerationCoordinator(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAdd(
            ServiceDescriptor.Scoped<IDocumentCacheReadAccelerationCoordinator>(
                static serviceProvider => new DocumentCacheReadAccelerationCoordinator(
                    serviceProvider.GetRequiredService<IOptions<DocumentCacheOptions>>(),
                    serviceProvider.GetRequiredService<IDataStoreSelection>(),
                    serviceProvider.GetRequiredService<IDocumentCacheTargetRegistry>(),
                    serviceProvider.GetRequiredService<IDocumentCacheReadLookupAdapter>(),
                    serviceProvider.GetRequiredService<IDocumentCacheMaterializer>(),
                    serviceProvider.GetRequiredService<IDocumentCacheWriter>(),
                    serviceProvider.GetRequiredService<IDocumentCacheReadTelemetry>(),
                    serviceProvider.GetRequiredService<IDocumentCacheProjectionTargetDiagnosticSink>(),
                    serviceProvider.GetRequiredService<TimeProvider>(),
                    serviceProvider.GetRequiredService<ILogger<DocumentCacheReadAccelerationCoordinator>>()
                )
            )
        );

        return services;
    }
}
