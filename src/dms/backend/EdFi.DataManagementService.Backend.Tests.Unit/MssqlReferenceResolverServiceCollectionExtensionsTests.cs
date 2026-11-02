// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Mssql_Reference_Resolver_Service_Collection_Extensions
{
    [Test]
    public void It_registers_the_mssql_reference_resolution_composition_surface()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(A.Fake<IReadableProfileProjector>());
        services.AddSingleton(A.Fake<IDataStoreProvider>());
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddMssqlReferenceResolver();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IReferenceResolver>();
        var writeFlattener = scope.ServiceProvider.GetRequiredService<IRelationalWriteFlattener>();
        var currentStateLoader =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteCurrentStateLoader>();
        var noProfileMergeSynthesizer =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteNoProfileMergeSynthesizer>();
        var noProfilePersister = scope.ServiceProvider.GetRequiredService<IRelationalWritePersister>();
        var targetLookupService =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteTargetLookupService>();
        var writeExecutor = scope.ServiceProvider.GetRequiredService<IRelationalWriteExecutor>();
        var writeSessionFactory = scope.ServiceProvider.GetRequiredService<IRelationalWriteSessionFactory>();
        var documentHydrator = scope.ServiceProvider.GetRequiredService<IDocumentHydrator>();
        var lookupCommandBuilder =
            scope.ServiceProvider.GetRequiredService<INaturalKeyLookupCommandBuilder>();
        var commandExecutor = scope.ServiceProvider.GetRequiredService<IRelationalCommandExecutor>();
        var parameterConfigurator =
            scope.ServiceProvider.GetRequiredService<IRelationalParameterConfigurator>();
        var readMaterializer = scope.ServiceProvider.GetRequiredService<IRelationalReadMaterializer>();
        var documentCacheMaterializationDataStore =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheMaterializationDataStore>();
        var documentCacheWriter = scope.ServiceProvider.GetRequiredService<IDocumentCacheWriter>();
        var documentCacheSessionBoundWriter =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheSessionBoundWriter>();
        var documentCacheWriterRetryAdapter =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheWriterRetryAdapter>();
        var documentProjectionWorkPager =
            scope.ServiceProvider.GetRequiredService<IDocumentProjectionWorkPager>();
        var documentCacheAdministrativeMutex =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheAdministrativeMutex>();
        var documentCacheAdministrativePrimitives =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheAdministrativePrimitives>();
        var providerCommandTimeoutClassifier =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheProviderCommandTimeoutClassifier>();
        var documentCacheBaselineSeeder =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheBaselineSeeder>();
        var documentCacheOfflineActivationCommand =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheOfflineActivationCommand>();
        var documentCacheOfflineDeactivationCommand =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheOfflineDeactivationCommand>();
        var documentCacheOnlineCacheRebuildCommand =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheOnlineCacheRebuildCommand>();
        var documentCacheInternalOnlyCacheAheadRecoveryCommand =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheInternalOnlyCacheAheadRecoveryCommand>();
        var documentCacheExplicitIntegrityScrubCommand =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheExplicitIntegrityScrubCommand>();
        var documentCacheProjectionDrainPageProcessor =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheProjectionDrainPageProcessor>();
        var readTargetLookupService =
            scope.ServiceProvider.GetRequiredService<IRelationalReadTargetLookupService>();
        var writeExceptionClassifier =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteExceptionClassifier>();
        var writeConstraintResolver =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteConstraintResolver>();
        var deleteConstraintResolver =
            scope.ServiceProvider.GetRequiredService<IRelationalDeleteConstraintResolver>();
        var currentEtagPreconditionChecker =
            scope.ServiceProvider.GetRequiredService<IRelationalCurrentEtagPreconditionChecker>();
        var deleteEtagPreconditionChecker =
            scope.ServiceProvider.GetRequiredService<IRelationalDeleteEtagPreconditionChecker>();
        var relationshipAuthorizationProviderFailureExtractor =
            scope.ServiceProvider.GetRequiredService<IRelationshipAuthorizationProviderFailureExtractor>();
        var documentCacheReadAccelerationCoordinator =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheReadAccelerationCoordinator>();

        resolver.Should().BeOfType<NaturalKeyReferenceResolver>();
        writeFlattener.Should().BeOfType<RelationalWriteFlattener>();
        currentStateLoader.Should().BeOfType<RelationalWriteCurrentStateLoader>();
        noProfileMergeSynthesizer.Should().BeOfType<RelationalWriteNoProfileMergeSynthesizer>();
        noProfilePersister.Should().BeOfType<RelationalWriteNoProfilePersister>();
        targetLookupService.Should().BeOfType<RelationalWriteTargetLookupService>();
        writeExecutor.Should().BeOfType<DefaultRelationalWriteExecutor>();
        writeSessionFactory.Should().BeOfType<MssqlRelationalWriteSessionFactory>();
        documentHydrator.Should().BeOfType<MssqlDocumentHydrator>();
        // The natural-key resolver is the only reference resolver, with this provider's lookup builder.
        services
            .Where(descriptor => descriptor.ServiceType == typeof(IReferenceResolver))
            .Should()
            .ContainSingle();
        lookupCommandBuilder.Should().BeOfType<MssqlNaturalKeyLookupCommandBuilder>();
        commandExecutor.Should().BeOfType<MssqlRelationalCommandExecutor>();
        parameterConfigurator.Should().BeOfType<MssqlRelationalParameterConfigurator>();
        readMaterializer.Should().BeOfType<RelationalReadMaterializer>();
        documentCacheMaterializationDataStore.Should().BeOfType<MssqlDocumentCacheMaterializationDataStore>();
        documentCacheWriter.Should().BeOfType<MssqlDocumentCacheWriter>();
        documentCacheSessionBoundWriter.Should().BeSameAs(documentCacheWriter);
        documentCacheWriterRetryAdapter.Should().BeOfType<DocumentCacheWriterRetryAdapter>();
        documentProjectionWorkPager.Should().BeOfType<MssqlDocumentProjectionWorkPager>();
        documentCacheAdministrativeMutex.Should().BeOfType<MssqlDocumentCacheAdministrativeMutex>();
        documentCacheAdministrativePrimitives.Should().BeOfType<DocumentCacheAdministrativePrimitives>();
        documentCacheAdministrativePrimitives.ProviderToken.Should().Be(RelationalProviderToken.SqlServer);
        providerCommandTimeoutClassifier
            .Should()
            .BeOfType<MssqlDocumentCacheProviderCommandTimeoutClassifier>();
        documentCacheBaselineSeeder.Should().BeOfType<DocumentCacheBaselineSeeder>();
        documentCacheOfflineActivationCommand.Should().BeOfType<DocumentCacheOfflineActivationCommand>();
        documentCacheOfflineDeactivationCommand.Should().BeOfType<DocumentCacheOfflineDeactivationCommand>();
        documentCacheOnlineCacheRebuildCommand.Should().BeOfType<DocumentCacheOnlineCacheRebuildCommand>();
        documentCacheInternalOnlyCacheAheadRecoveryCommand
            .Should()
            .BeOfType<DocumentCacheInternalOnlyCacheAheadRecoveryCommand>();
        documentCacheExplicitIntegrityScrubCommand
            .Should()
            .BeOfType<DocumentCacheExplicitIntegrityScrubCommand>();
        documentCacheProjectionDrainPageProcessor
            .Should()
            .BeOfType<DocumentCacheProjectionDrainPageProcessor>();
        readTargetLookupService.Should().BeOfType<RelationalReadTargetLookupService>();
        writeExceptionClassifier.Should().BeOfType<MssqlRelationalWriteExceptionClassifier>();
        writeConstraintResolver.Should().BeOfType<RelationalWriteConstraintResolver>();
        deleteConstraintResolver.Should().BeOfType<RelationalDeleteConstraintResolver>();
        currentEtagPreconditionChecker.Should().BeOfType<RelationalCurrentEtagPreconditionChecker>();
        deleteEtagPreconditionChecker.Should().BeOfType<RelationalCurrentEtagPreconditionChecker>();
        deleteEtagPreconditionChecker.Should().BeSameAs(currentEtagPreconditionChecker);
        relationshipAuthorizationProviderFailureExtractor
            .Should()
            .BeOfType<DefaultRelationshipAuthorizationProviderFailureExtractor>();
        documentCacheReadAccelerationCoordinator
            .Should()
            .BeSameAs(PassthroughDocumentCacheReadAccelerationCoordinator.Instance);
    }

    [Test]
    public void It_registers_the_full_read_acceleration_coordinator_when_read_acceleration_is_enabled()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(A.Fake<IReadableProfileProjector>());
        services.AddSingleton(A.Fake<IDataStoreProvider>());
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DocumentCacheOptions>(options => options.ReadAcceleration.Enabled = true);
        services.AddMssqlReferenceResolver();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDocumentCacheReadAccelerationCoordinator>()
            .Should()
            .BeOfType<DocumentCacheReadAccelerationCoordinator>();
    }

    [Test]
    public void DocumentCacheWriter_ServiceRegistration_registers_mssql_writer_and_projection_pager_adapters()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(A.Fake<IReadableProfileProjector>());
        services.AddSingleton(A.Fake<IDataStoreProvider>());
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddMssqlReferenceResolver();

        services
            .Where(descriptor => descriptor.ServiceType == typeof(MssqlDocumentCacheWriter))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Match<ServiceDescriptor>(descriptor =>
                descriptor.Lifetime == ServiceLifetime.Scoped
                && descriptor.ImplementationType == typeof(MssqlDocumentCacheWriter)
            );
        AssertScopedFactory<IDocumentCacheWriter>(services);
        AssertScopedFactory<IDocumentCacheSessionBoundWriter>(services);
        services
            .Where(descriptor => descriptor.ServiceType == typeof(IDocumentProjectionWorkPager))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Match<ServiceDescriptor>(descriptor =>
                descriptor.Lifetime == ServiceLifetime.Singleton
                && descriptor.ImplementationType == typeof(MssqlDocumentProjectionWorkPager)
            );
        services
            .Where(descriptor => descriptor.ServiceType == typeof(IDocumentCacheAdministrativeMutex))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Match<ServiceDescriptor>(descriptor =>
                descriptor.Lifetime == ServiceLifetime.Singleton
                && descriptor.ImplementationType == typeof(MssqlDocumentCacheAdministrativeMutex)
            );
        services
            .Single(descriptor =>
                descriptor.ServiceType == typeof(IDocumentCacheProjectionDrainPageProcessor)
            )
            .ImplementationType.Should()
            .Be<DocumentCacheProjectionDrainPageProcessor>();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        var documentCacheWriter = scope.ServiceProvider.GetRequiredService<IDocumentCacheWriter>();
        var documentCacheSessionBoundWriter =
            scope.ServiceProvider.GetRequiredService<IDocumentCacheSessionBoundWriter>();

        documentCacheWriter.Should().BeOfType<MssqlDocumentCacheWriter>();
        documentCacheSessionBoundWriter.Should().BeSameAs(documentCacheWriter);
        scope
            .ServiceProvider.GetRequiredService<IDocumentProjectionWorkPager>()
            .Should()
            .BeOfType<MssqlDocumentProjectionWorkPager>();
        scope
            .ServiceProvider.GetRequiredService<IDocumentCacheAdministrativeMutex>()
            .Should()
            .BeOfType<MssqlDocumentCacheAdministrativeMutex>();
    }

    [Test]
    public void It_does_not_register_the_mssql_Cdc_source_position_adapter_by_default()
    {
        var services = new ServiceCollection();

        services.AddMssqlReferenceResolver();

        services
            .Where(descriptor => descriptor.ServiceType == typeof(MssqlCdcSourcePositionAdapter))
            .Should()
            .BeEmpty();
    }

    [Test]
    public void ServiceCollection_replaces_existing_relational_token_info_lookup_with_mssql_lookup()
    {
        var services = new ServiceCollection();

        services.AddScoped<IRelationalCommandExecutor>(_ => A.Fake<IRelationalCommandExecutor>());
        services.AddScoped<IRelationalParameterConfigurator>(_ => A.Fake<IRelationalParameterConfigurator>());
        services.AddScoped<
            IRelationalTokenInfoEducationOrganizationLookup,
            StubRelationalTokenInfoEducationOrganizationLookup
        >();
        services.AddMssqlRelationalTokenInfoEducationOrganizationLookup();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetServices<IRelationalTokenInfoEducationOrganizationLookup>()
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<MssqlTokenInfoEducationOrganizationLookup>();
    }

    private static ServiceProvider BuildServiceProvider(IServiceCollection services)
    {
        services.TryAddSingleton(new DeadlockRetrySettings());
        services.TryAddSingleton<IDocumentLinkSlugResolver, NoLinkSlugResolver>();
        services.TryAddSingleton<IDocumentCacheProjectionSupervisor, StubDocumentCacheProjectionSupervisor>();
        services.TryAddSingleton<IDocumentCacheTargetRegistry, StubDocumentCacheTargetRegistry>();
        services.AddOptions<ResourceLinksOptions>();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private static void AssertScopedFactory<TService>(IServiceCollection services)
        where TService : class
    {
        ServiceDescriptor descriptor = services
            .Where(descriptor => descriptor.ServiceType == typeof(TService))
            .Should()
            .ContainSingle()
            .Subject;

        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationFactory.Should().NotBeNull();
        descriptor.ImplementationType.Should().BeNull();
        descriptor.ImplementationInstance.Should().BeNull();
    }

    private sealed class NoLinkSlugResolver : IDocumentLinkSlugResolver
    {
        public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId) =>
            throw new InvalidOperationException("NoLinkSlugResolver is unused in composition-surface tests.");
    }

    private sealed class StubDocumentCacheProjectionSupervisor : IDocumentCacheProjectionSupervisor
    {
        public System.Collections.Immutable.ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts =>
            [];

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Stub supervisor is unused in composition-surface tests.");
    }

    private sealed class StubDocumentCacheTargetRegistry : IDocumentCacheTargetRegistry
    {
        private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; } = new([], ObservedAt);

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } = new([], ObservedAt);

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Stub registry is unused in composition-surface tests.");
    }

    private sealed class StubRelationalTokenInfoEducationOrganizationLookup
        : IRelationalTokenInfoEducationOrganizationLookup
    {
        public Task<IEnumerable<TokenInfoEducationOrganization>> GetEducationOrganizations(
            IReadOnlyCollection<EducationOrganizationId> educationOrganizationIds,
            MappingSet mappingSet
        ) => Task.FromResult<IEnumerable<TokenInfoEducationOrganization>>([]);
    }
}
