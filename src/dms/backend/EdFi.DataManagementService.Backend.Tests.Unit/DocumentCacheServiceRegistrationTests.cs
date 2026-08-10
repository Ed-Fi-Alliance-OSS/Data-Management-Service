// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheServiceRegistration")]
public class Given_DocumentCacheServiceRegistration
{
    [Test]
    public void It_registers_the_shared_projection_and_administrative_runtime_surface()
    {
        IServiceCollection services = new ServiceCollection();

        AddSharedReferenceResolverForTest(services);

        AssertSingleton<DocumentCacheProjectionObservationStore, DocumentCacheProjectionObservationStore>(
            services
        );
        AssertSingleton<IDocumentCacheProjectionTelemetry, DocumentCacheProjectionTelemetry>(services);
        AssertSingleton<
            IDocumentCacheDownstreamPublicationHistoryProvider,
            DocumentCacheUnknownDownstreamPublicationHistoryProvider
        >(services);
        AssertSingletonFactory<IDocumentCacheProjectionObservationProvider>(services);
        AssertSingletonFactory<IDocumentCacheProjectionObservationSink>(services);
        AssertSingleton<
            IDocumentCacheProjectionTargetRuntimeContextFactory,
            DocumentCacheProjectionTargetRuntimeContextFactory
        >(services);
        AssertSingleton<IDocumentCacheProjectionItemProcessor, DocumentCacheProjectionItemProcessor>(
            services
        );
        AssertSingleton<IDocumentCacheProjectionScheduler, DocumentCacheProjectionScheduler>(services);
        AssertSingleton<IDocumentCacheAdministrativeCommandRunner, DocumentCacheAdministrativeCommandRunner>(
            services
        );
        AssertSingleton<
            IDocumentCacheGuardedNewEmptyActivationCommand,
            DocumentCacheGuardedNewEmptyActivationCommand
        >(services);
        AssertSingleton<IDocumentCacheOfflineActivationCommand, DocumentCacheOfflineActivationCommand>(
            services
        );
        AssertSingleton<IDocumentCacheOfflineDeactivationCommand, DocumentCacheOfflineDeactivationCommand>(
            services
        );
        AssertSingleton<IDocumentCacheOnlineCacheRebuildCommand, DocumentCacheOnlineCacheRebuildCommand>(
            services
        );
        AssertSingleton<
            IDocumentCacheExplicitIntegrityScrubCommand,
            DocumentCacheExplicitIntegrityScrubCommand
        >(services);
        AssertSingleton<
            IDocumentCacheInternalOnlyCacheAheadRecoveryCommand,
            DocumentCacheInternalOnlyCacheAheadRecoveryCommand
        >(services);
        AssertSingleton<IDocumentCacheBaselineSeeder, DocumentCacheBaselineSeeder>(services);
        AssertSingleton<IDocumentCacheAdministrativeDrainer, DocumentCacheAdministrativeDrainer>(services);
        AssertScoped<IDocumentCacheWriterRetryAdapter, DocumentCacheWriterRetryAdapter>(services);
        AssertSingletonFactory<IDocumentCacheProjectionTargetDiagnosticSink>(services);
        AssertScoped<IDocumentCacheReadResponseShaper, DocumentCacheReadResponseShaper>(services);
        AssertScopedFactory<IDocumentCacheReadAccelerationCoordinator>(services);
        services
            .Should()
            .NotContain(descriptor => descriptor.ServiceType == typeof(IDocumentCacheReadLookupAdapter));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Test]
    public void It_preserves_a_custom_downstream_publication_history_provider()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<
            IDocumentCacheDownstreamPublicationHistoryProvider,
            CustomDocumentCacheDownstreamPublicationHistoryProvider
        >();

        services.AddPostgresqlReferenceResolver();

        AssertSingleton<
            IDocumentCacheDownstreamPublicationHistoryProvider,
            CustomDocumentCacheDownstreamPublicationHistoryProvider
        >(services);
    }

    [Test]
    public void It_registers_the_postgresql_projection_and_administrative_provider_adapters()
    {
        IServiceCollection services = new ServiceCollection();

        services.AddPostgresqlReferenceResolver();

        AssertScoped<PostgresqlDocumentCacheWriter, PostgresqlDocumentCacheWriter>(services);
        AssertScopedFactory<IDocumentCacheWriter>(services);
        AssertScopedFactory<IDocumentCacheSessionBoundWriter>(services);
        AssertSingleton<IDocumentProjectionWorkPager, PostgresqlDocumentProjectionWorkPager>(services);
        AssertSingleton<IDocumentCacheAdministrativeMutex, PostgresqlDocumentCacheAdministrativeMutex>(
            services
        );
        AssertSingleton<
            IDocumentCacheProviderCommandTimeoutClassifier,
            PostgresqlDocumentCacheProviderCommandTimeoutClassifier
        >(services);
        AssertSingleton<IServedEtagComposer, ServedEtagComposer>(services);
        AssertSingletonFactory<IDocumentCacheAdministrativePrimitives>(services);
        AssertSingleton<
            IDocumentCacheProjectionDrainPageProcessor,
            DocumentCacheProjectionDrainPageProcessor
        >(services);
        AssertScoped<IDocumentCacheReadLookupAdapter, PostgresqlDocumentCacheReadLookupAdapter>(services);
        AssertScoped<IDocumentCacheReadResponseShaper, DocumentCacheReadResponseShaper>(services);
        AssertScopedFactory<IDocumentCacheReadAccelerationCoordinator>(services);
    }

    [Test]
    public void It_registers_the_mssql_projection_and_administrative_provider_adapters()
    {
        IServiceCollection services = new ServiceCollection();

        services.AddMssqlReferenceResolver();

        AssertScoped<MssqlDocumentCacheWriter, MssqlDocumentCacheWriter>(services);
        AssertScopedFactory<IDocumentCacheWriter>(services);
        AssertScopedFactory<IDocumentCacheSessionBoundWriter>(services);
        AssertSingleton<IDocumentProjectionWorkPager, MssqlDocumentProjectionWorkPager>(services);
        AssertSingleton<IDocumentCacheAdministrativeMutex, MssqlDocumentCacheAdministrativeMutex>(services);
        AssertSingleton<
            IDocumentCacheProviderCommandTimeoutClassifier,
            MssqlDocumentCacheProviderCommandTimeoutClassifier
        >(services);
        AssertSingleton<IServedEtagComposer, ServedEtagComposer>(services);
        AssertSingletonFactory<IDocumentCacheAdministrativePrimitives>(services);
        AssertSingleton<
            IDocumentCacheProjectionDrainPageProcessor,
            DocumentCacheProjectionDrainPageProcessor
        >(services);
        AssertScoped<IDocumentCacheReadLookupAdapter, MssqlDocumentCacheReadLookupAdapter>(services);
        AssertScoped<IDocumentCacheReadResponseShaper, DocumentCacheReadResponseShaper>(services);
        AssertScopedFactory<IDocumentCacheReadAccelerationCoordinator>(services);
    }

    private static void AddSharedReferenceResolverForTest(IServiceCollection services)
    {
        ReferenceResolverServiceCollectionExtensions.AddReferenceResolver<
            PostgresqlReferenceResolverAdapterFactory,
            PostgresqlRelationalCommandExecutor,
            PostgresqlRelationalWriteSessionFactory,
            PostgresqlDocumentHydrator,
            PostgresqlSessionDocumentHydrator
        >(services);
    }

    private static void AssertSingleton<TService, TImplementation>(IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        ServiceDescriptor descriptor = GetSingleDescriptor<TService>(services);

        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be(typeof(TImplementation));
        descriptor.ImplementationFactory.Should().BeNull();
        descriptor.ImplementationInstance.Should().BeNull();
    }

    private static void AssertScoped<TService, TImplementation>(IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        ServiceDescriptor descriptor = GetSingleDescriptor<TService>(services);

        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be(typeof(TImplementation));
        descriptor.ImplementationFactory.Should().BeNull();
        descriptor.ImplementationInstance.Should().BeNull();
    }

    private static void AssertSingletonFactory<TService>(IServiceCollection services)
        where TService : class
    {
        ServiceDescriptor descriptor = GetSingleDescriptor<TService>(services);

        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().BeNull();
        descriptor.ImplementationFactory.Should().NotBeNull();
        descriptor.ImplementationInstance.Should().BeNull();
    }

    private static void AssertScopedFactory<TService>(IServiceCollection services)
        where TService : class
    {
        ServiceDescriptor descriptor = GetSingleDescriptor<TService>(services);

        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().BeNull();
        descriptor.ImplementationFactory.Should().NotBeNull();
        descriptor.ImplementationInstance.Should().BeNull();
    }

    private static ServiceDescriptor GetSingleDescriptor<TService>(IServiceCollection services)
        where TService : class
    {
        return services
            .Should()
            .ContainSingle(descriptor => descriptor.ServiceType == typeof(TService))
            .Subject;
    }

    private sealed class CustomDocumentCacheDownstreamPublicationHistoryProvider
        : IDocumentCacheDownstreamPublicationHistoryProvider
    {
        public Task<DocumentCacheDownstreamPublicationHistoryObservation> ObserveAsync(
            DocumentCacheTargetKey targetKey,
            DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
