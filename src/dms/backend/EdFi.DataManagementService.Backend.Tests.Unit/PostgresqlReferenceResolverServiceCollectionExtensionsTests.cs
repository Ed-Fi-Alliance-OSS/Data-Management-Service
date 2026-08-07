// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Postgresql_Reference_Resolver_Service_Collection_Extensions
{
    [Test]
    public void It_registers_the_postgresql_reference_resolution_composition_surface()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(A.Fake<IReadableProfileProjector>());
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddSingleton(A.Fake<IDataStoreProvider>());
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddPostgresqlReferenceResolver();

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
        var targetLookupResolver =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteTargetLookupResolver>();
        var writeExecutor = scope.ServiceProvider.GetRequiredService<IRelationalWriteExecutor>();
        var writeSessionFactory = scope.ServiceProvider.GetRequiredService<IRelationalWriteSessionFactory>();
        var documentHydrator = scope.ServiceProvider.GetRequiredService<IDocumentHydrator>();
        var factory = scope.ServiceProvider.GetRequiredService<IReferenceResolverAdapterFactory>();
        var adapter = scope.ServiceProvider.GetRequiredService<IReferenceResolverAdapter>();
        var commandExecutor = scope.ServiceProvider.GetRequiredService<IRelationalCommandExecutor>();
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

        resolver.Should().BeOfType<ReferenceResolver>();
        writeFlattener.Should().BeOfType<RelationalWriteFlattener>();
        currentStateLoader.Should().BeOfType<RelationalWriteCurrentStateLoader>();
        noProfileMergeSynthesizer.Should().BeOfType<RelationalWriteNoProfileMergeSynthesizer>();
        noProfilePersister.Should().BeOfType<RelationalWriteNoProfilePersister>();
        targetLookupService.Should().BeOfType<RelationalWriteTargetLookupService>();
        targetLookupResolver.Should().BeOfType<RelationalWriteTargetLookupResolver>();
        writeExecutor.Should().BeOfType<DefaultRelationalWriteExecutor>();
        writeSessionFactory.Should().BeOfType<PostgresqlRelationalWriteSessionFactory>();
        documentHydrator.Should().BeOfType<PostgresqlDocumentHydrator>();
        factory.Should().BeOfType<PostgresqlReferenceResolverAdapterFactory>();
        adapter.Should().BeOfType<PostgresqlReferenceResolverAdapter>();
        commandExecutor.Should().BeOfType<PostgresqlRelationalCommandExecutor>();
        readMaterializer.Should().BeOfType<RelationalReadMaterializer>();
        documentCacheMaterializationDataStore
            .Should()
            .BeOfType<PostgresqlDocumentCacheMaterializationDataStore>();
        documentCacheWriter.Should().BeOfType<PostgresqlDocumentCacheWriter>();
        documentCacheSessionBoundWriter.Should().BeSameAs(documentCacheWriter);
        documentCacheWriterRetryAdapter.Should().BeOfType<DocumentCacheWriterRetryAdapter>();
        documentProjectionWorkPager.Should().BeOfType<PostgresqlDocumentProjectionWorkPager>();
        documentCacheAdministrativePrimitives.Should().BeOfType<DocumentCacheAdministrativePrimitives>();
        documentCacheAdministrativePrimitives.ProviderToken.Should().Be(RelationalProviderToken.Postgresql);
        providerCommandTimeoutClassifier
            .Should()
            .BeOfType<PostgresqlDocumentCacheProviderCommandTimeoutClassifier>();
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
        documentCacheAdministrativeMutex.Should().BeOfType<PostgresqlDocumentCacheAdministrativeMutex>();
        documentCacheProjectionDrainPageProcessor
            .Should()
            .BeOfType<DocumentCacheProjectionDrainPageProcessor>();
        readTargetLookupService.Should().BeOfType<RelationalReadTargetLookupService>();
        writeExceptionClassifier.Should().BeOfType<PostgresqlRelationalWriteExceptionClassifier>();
        writeConstraintResolver.Should().BeOfType<RelationalWriteConstraintResolver>();
        deleteConstraintResolver.Should().BeOfType<RelationalDeleteConstraintResolver>();
        currentEtagPreconditionChecker.Should().BeOfType<RelationalCurrentEtagPreconditionChecker>();
        deleteEtagPreconditionChecker.Should().BeOfType<RelationalCurrentEtagPreconditionChecker>();
        deleteEtagPreconditionChecker.Should().BeSameAs(currentEtagPreconditionChecker);
        relationshipAuthorizationProviderFailureExtractor
            .Should()
            .BeOfType<PostgresqlRelationshipAuthorizationProviderFailureExtractor>();
    }

    [Test]
    public void DocumentCacheWriter_ServiceRegistration_registers_postgresql_writer_and_projection_pager_adapters()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(A.Fake<IReadableProfileProjector>());
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddSingleton(A.Fake<IDataStoreProvider>());
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddPostgresqlReferenceResolver();

        services
            .Where(descriptor => descriptor.ServiceType == typeof(PostgresqlDocumentCacheWriter))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Match<ServiceDescriptor>(descriptor =>
                descriptor.Lifetime == ServiceLifetime.Scoped
                && descriptor.ImplementationType == typeof(PostgresqlDocumentCacheWriter)
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
                && descriptor.ImplementationType == typeof(PostgresqlDocumentProjectionWorkPager)
            );
        services
            .Where(descriptor => descriptor.ServiceType == typeof(IDocumentCacheAdministrativeMutex))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Match<ServiceDescriptor>(descriptor =>
                descriptor.Lifetime == ServiceLifetime.Singleton
                && descriptor.ImplementationType == typeof(PostgresqlDocumentCacheAdministrativeMutex)
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

        documentCacheWriter.Should().BeOfType<PostgresqlDocumentCacheWriter>();
        documentCacheSessionBoundWriter.Should().BeSameAs(documentCacheWriter);
        scope
            .ServiceProvider.GetRequiredService<IDocumentProjectionWorkPager>()
            .Should()
            .BeOfType<PostgresqlDocumentProjectionWorkPager>();
        scope
            .ServiceProvider.GetRequiredService<IDocumentCacheAdministrativeMutex>()
            .Should()
            .BeOfType<PostgresqlDocumentCacheAdministrativeMutex>();
    }

    [Test]
    public void It_extracts_postgresql_provider_failure_metadata_from_postgres_exception()
    {
        var extractor = new PostgresqlRelationshipAuthorizationProviderFailureExtractor();
        var exception = new PostgresException(
            "1|7|1|0:0:n",
            "ERROR",
            "ERROR",
            RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode
        );

        var providerFailure = extractor.Extract(exception);

        providerFailure
            .ErrorCode.Should()
            .Be(RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode);
        providerFailure.Message.Should().Be("1|7|1|0:0:n");
    }

    [Test]
    public void It_builds_an_embeddable_postgresql_reference_lookup_command()
    {
        var factory = new PostgresqlReferenceResolverAdapterFactory(A.Fake<IRelationalCommandExecutor>());
        var request = CreateLookupRequest(3);

        var command = factory.TryBuildSessionLookupCommand(request);

        command.Should().NotBeNull();
        command!.Parameters.Should().ContainSingle();
        command.Parameters[0].Name.Should().Be("@referentialIds");
        command.CommandText.Should().Contain("unnest(@referentialIds::uuid[])");
    }

    [Test]
    public void ServiceCollection_replaces_existing_relational_token_info_lookup_with_postgresql_lookup()
    {
        var services = new ServiceCollection();

        services.AddScoped<IRelationalCommandExecutor>(_ => A.Fake<IRelationalCommandExecutor>());
        services.AddScoped<
            IRelationalTokenInfoEducationOrganizationLookup,
            StubRelationalTokenInfoEducationOrganizationLookup
        >();
        services.AddPostgresqlRelationalTokenInfoEducationOrganizationLookup();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetServices<IRelationalTokenInfoEducationOrganizationLookup>()
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<PostgresqlTokenInfoEducationOrganizationLookup>();
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

    private static ReferenceLookupRequest CreateLookupRequest(int count)
    {
        var requestResource = new QualifiedResourceName("Ed-Fi", "Student");
        var mappingSet = RelationalAccessTestData.CreateMappingSet(requestResource);

        return new ReferenceLookupRequest(
            mappingSet,
            requestResource,
            Enumerable
                .Range(1, count)
                .Select(index =>
                    RelationalAccessTestData.CreateSchoolLookup(
                        new ReferentialId(Guid.ParseExact($"{index:x8}000000000000000000000000", "N"))
                    )
                )
                .ToArray()
        );
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
