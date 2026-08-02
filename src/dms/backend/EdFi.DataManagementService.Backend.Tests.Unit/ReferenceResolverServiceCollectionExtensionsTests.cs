// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_ReferenceResolver_Service_Collection_Extensions
{
    [Test]
    public void It_registers_the_narrow_reference_resolver_surface()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddNaturalKeyReferenceResolver<TestNaturalKeyLookupAdapterFactory>();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IReferenceResolver>();
        var factory = scope.ServiceProvider.GetRequiredService<INaturalKeyLookupAdapterFactory>();
        var adapter = scope.ServiceProvider.GetRequiredService<INaturalKeyLookupAdapter>();

        resolver.Should().BeOfType<NaturalKeyReferenceResolver>();
        scope.ServiceProvider.GetService<IDescriptorReadHandler>().Should().BeNull();
        scope.ServiceProvider.GetService<IDescriptorWriteHandler>().Should().BeNull();
        scope.ServiceProvider.GetService<IRelationalWriteTargetLookupService>().Should().BeNull();
        scope.ServiceProvider.GetService<IRelationalWriteTargetLookupResolver>().Should().BeNull();
        factory.Should().BeOfType<TestNaturalKeyLookupAdapterFactory>();
        adapter.Should().BeOfType<TestNaturalKeyLookupAdapter>();
    }

    [Test]
    public void It_registers_the_natural_key_resolver_from_the_shared_composition_surface()
    {
        // The dialect entry points production hosts call delegate to this shared surface, and it is the
        // surface that decides which resolver the query path consumes — pin it.
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(A.Fake<IReadableProfileProjector>());
        services.AddReferenceResolver<
            TestNaturalKeyLookupAdapterFactory,
            TestRelationalCommandExecutor,
            TestRelationalWriteSessionFactory,
            TestDocumentHydrator,
            TestSessionDocumentHydrator
        >();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IReferenceResolver>()
            .Should()
            .BeOfType<NaturalKeyReferenceResolver>();
        scope
            .ServiceProvider.GetRequiredService<INaturalKeyLookupAdapterFactory>()
            .Should()
            .BeOfType<TestNaturalKeyLookupAdapterFactory>();
        scope
            .ServiceProvider.GetRequiredService<INaturalKeyLookupAdapter>()
            .Should()
            .BeOfType<TestNaturalKeyLookupAdapter>();
    }

    [Test]
    public void It_registers_the_relational_access_seam_for_dialect_composition()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(A.Fake<IReadableProfileProjector>());

        services.AddReferenceResolver<
            ExecutorBackedNaturalKeyLookupAdapterFactory,
            TestRelationalCommandExecutor,
            TestRelationalWriteSessionFactory,
            TestDocumentHydrator,
            TestSessionDocumentHydrator
        >();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        var commandExecutor = scope.ServiceProvider.GetRequiredService<IRelationalCommandExecutor>();
        var parameterConfigurator =
            scope.ServiceProvider.GetRequiredService<IRelationalParameterConfigurator>();
        var writeSessionFactory = scope.ServiceProvider.GetRequiredService<IRelationalWriteSessionFactory>();
        var documentHydrator = scope.ServiceProvider.GetRequiredService<IDocumentHydrator>();
        var writeFlattener = scope.ServiceProvider.GetRequiredService<IRelationalWriteFlattener>();
        var sessionDocumentHydrator = scope.ServiceProvider.GetRequiredService<ISessionDocumentHydrator>();
        var readMaterializer = scope.ServiceProvider.GetRequiredService<IRelationalReadMaterializer>();
        var readTargetLookupService =
            scope.ServiceProvider.GetRequiredService<IRelationalReadTargetLookupService>();
        var singleRecordRelationshipAuthorizationExecutor =
            scope.ServiceProvider.GetRequiredService<ISingleRecordRelationshipAuthorizationExecutor>();
        var relationshipAuthorizationProviderFailureExtractor =
            scope.ServiceProvider.GetRequiredService<IRelationshipAuthorizationProviderFailureExtractor>();
        var currentStateLoader =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteCurrentStateLoader>();
        var writeFreshnessChecker =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteFreshnessChecker>();
        var noProfileMergeSynthesizer =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteNoProfileMergeSynthesizer>();
        var noProfilePersister = scope.ServiceProvider.GetRequiredService<IRelationalWritePersister>();
        var writeExceptionClassifier =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteExceptionClassifier>();
        var deleteConstraintResolver =
            scope.ServiceProvider.GetRequiredService<IRelationalDeleteConstraintResolver>();
        var descriptorReadHandler = scope.ServiceProvider.GetRequiredService<IDescriptorReadHandler>();
        var descriptorWriteHandler = scope.ServiceProvider.GetRequiredService<IDescriptorWriteHandler>();
        var targetLookupService =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteTargetLookupService>();
        var targetLookupResolver =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteTargetLookupResolver>();
        var writeExecutor = scope.ServiceProvider.GetRequiredService<IRelationalWriteExecutor>();
        var currentEtagPreconditionChecker =
            scope.ServiceProvider.GetRequiredService<IRelationalCurrentEtagPreconditionChecker>();
        var deleteEtagPreconditionChecker =
            scope.ServiceProvider.GetRequiredService<IRelationalDeleteEtagPreconditionChecker>();
        var edOrgAuthorizationElementResolutionCache =
            scope.ServiceProvider.GetRequiredService<RelationalEdOrgAuthorizationElementResolutionCache>();
        var edOrgAuthorizationSubjectSelector =
            scope.ServiceProvider.GetRequiredService<RelationalEdOrgAuthorizationSubjectSelector>();
        var factory = scope
            .ServiceProvider.GetRequiredService<INaturalKeyLookupAdapterFactory>()
            .Should()
            .BeOfType<ExecutorBackedNaturalKeyLookupAdapterFactory>()
            .Subject;
        var adapter = scope
            .ServiceProvider.GetRequiredService<INaturalKeyLookupAdapter>()
            .Should()
            .BeOfType<ExecutorBackedNaturalKeyLookupAdapter>()
            .Subject;

        commandExecutor.Should().BeOfType<TestRelationalCommandExecutor>();
        parameterConfigurator.Should().BeOfType<DefaultRelationalParameterConfigurator>();
        writeSessionFactory.Should().BeOfType<TestRelationalWriteSessionFactory>();
        documentHydrator.Should().BeOfType<TestDocumentHydrator>();
        writeFlattener.Should().BeOfType<RelationalWriteFlattener>();
        sessionDocumentHydrator.Should().BeOfType<TestSessionDocumentHydrator>();
        readMaterializer.Should().BeOfType<RelationalReadMaterializer>();
        readTargetLookupService.Should().BeOfType<RelationalReadTargetLookupService>();
        singleRecordRelationshipAuthorizationExecutor
            .Should()
            .BeOfType<SingleRecordRelationshipAuthorizationExecutor>();
        relationshipAuthorizationProviderFailureExtractor
            .Should()
            .BeOfType<DefaultRelationshipAuthorizationProviderFailureExtractor>();
        currentStateLoader.Should().BeOfType<RelationalWriteCurrentStateLoader>();
        writeFreshnessChecker.Should().BeOfType<RelationalWriteFreshnessChecker>();
        noProfileMergeSynthesizer.Should().BeOfType<RelationalWriteNoProfileMergeSynthesizer>();
        noProfilePersister.Should().BeOfType<RelationalWriteNoProfilePersister>();
        writeExceptionClassifier.Should().BeOfType<NoOpRelationalWriteExceptionClassifier>();
        deleteConstraintResolver.Should().BeOfType<RelationalDeleteConstraintResolver>();
        descriptorReadHandler.Should().BeOfType<DescriptorReadHandler>();
        descriptorWriteHandler.Should().BeOfType<DescriptorWriteHandler>();
        targetLookupService.Should().BeOfType<RelationalWriteTargetLookupService>();
        targetLookupResolver.Should().BeOfType<RelationalWriteTargetLookupResolver>();
        writeExecutor.Should().BeOfType<DefaultRelationalWriteExecutor>();
        currentEtagPreconditionChecker.Should().BeOfType<RelationalCurrentEtagPreconditionChecker>();
        deleteEtagPreconditionChecker.Should().BeOfType<RelationalCurrentEtagPreconditionChecker>();
        deleteEtagPreconditionChecker.Should().BeSameAs(currentEtagPreconditionChecker);
        edOrgAuthorizationElementResolutionCache.Should().NotBeNull();
        edOrgAuthorizationSubjectSelector.Should().NotBeNull();
        factory.CommandExecutor.Should().BeSameAs(commandExecutor);
        adapter.CommandExecutor.Should().BeSameAs(commandExecutor);
    }

    [Test]
    public void It_registers_the_delete_constraint_resolver_as_a_singleton_shared_across_scopes()
    {
        // The resolver caches its per-model-set index in a ConditionalWeakTable; registering as
        // Singleton (rather than Scoped, which is the default for this composition surface) is
        // what lets the cache survive beyond a single request. Asserting both lifetime and
        // instance identity pins the decision so a future "make everything Scoped" sweep doesn't
        // silently defeat the cache.
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(A.Fake<IReadableProfileProjector>());

        services.AddReferenceResolver<
            ExecutorBackedNaturalKeyLookupAdapterFactory,
            TestRelationalCommandExecutor,
            TestRelationalWriteSessionFactory,
            TestDocumentHydrator,
            TestSessionDocumentHydrator
        >();

        var descriptor = services.Single(s => s.ServiceType == typeof(IRelationalDeleteConstraintResolver));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be<RelationalDeleteConstraintResolver>();

        using var serviceProvider = BuildServiceProvider(services);
        using var firstScope = serviceProvider.CreateScope();
        using var secondScope = serviceProvider.CreateScope();

        var firstResolver =
            firstScope.ServiceProvider.GetRequiredService<IRelationalDeleteConstraintResolver>();
        var secondResolver =
            secondScope.ServiceProvider.GetRequiredService<IRelationalDeleteConstraintResolver>();

        firstResolver.Should().BeSameAs(secondResolver);
    }

    private static ServiceProvider BuildServiceProvider(IServiceCollection services)
    {
        // The Backend's reference-resolver registration tree includes the read materializer,
        // which depends on Core-owned IDocumentLinkSlugResolver and bound ResourceLinksOptions.
        // Provide stubs so the composition surface validates end-to-end without pulling in the
        // full Core DI extension.
        services.TryAddSingleton<IDocumentLinkSlugResolver, NoLinkSlugResolver>();
        services.AddOptions<ResourceLinksOptions>();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private sealed class NoLinkSlugResolver : IDocumentLinkSlugResolver
    {
        public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, string discriminator) =>
            throw new InvalidOperationException("NoLinkSlugResolver is unused in composition-surface tests.");
    }

    private sealed class TestNaturalKeyLookupAdapterFactory : INaturalKeyLookupAdapterFactory
    {
        public INaturalKeyLookupAdapter CreateAdapter() => new TestNaturalKeyLookupAdapter();

        public INaturalKeyLookupAdapter CreateSessionAdapter(
            DbConnection connection,
            DbTransaction transaction
        ) => new TestNaturalKeyLookupAdapter();
    }

    private sealed class TestNaturalKeyLookupAdapter : INaturalKeyLookupAdapter
    {
        public Task<IReadOnlyList<NaturalKeyLookupRow>> ResolveAsync(
            NaturalKeyLookupBatch batch,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult<IReadOnlyList<NaturalKeyLookupRow>>([]);
        }
    }

    private sealed class ExecutorBackedNaturalKeyLookupAdapterFactory(
        IRelationalCommandExecutor commandExecutor
    ) : INaturalKeyLookupAdapterFactory
    {
        public IRelationalCommandExecutor CommandExecutor { get; } =
            commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

        public INaturalKeyLookupAdapter CreateAdapter()
        {
            return new ExecutorBackedNaturalKeyLookupAdapter(CommandExecutor);
        }

        public INaturalKeyLookupAdapter CreateSessionAdapter(
            DbConnection connection,
            DbTransaction transaction
        )
        {
            return new ExecutorBackedNaturalKeyLookupAdapter(
                new SessionRelationalCommandExecutor(connection, transaction)
            );
        }
    }

    private sealed class ExecutorBackedNaturalKeyLookupAdapter(IRelationalCommandExecutor commandExecutor)
        : INaturalKeyLookupAdapter
    {
        public IRelationalCommandExecutor CommandExecutor { get; } =
            commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

        public Task<IReadOnlyList<NaturalKeyLookupRow>> ResolveAsync(
            NaturalKeyLookupBatch batch,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult<IReadOnlyList<NaturalKeyLookupRow>>([]);
        }
    }

    private sealed class TestRelationalCommandExecutor : IRelationalCommandExecutor
    {
        public SqlDialect Dialect => SqlDialect.Pgsql;

        public Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestRelationalWriteSessionFactory : IRelationalWriteSessionFactory
    {
        public Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestSessionDocumentHydrator : ISessionDocumentHydrator
    {
        public Task<HydratedPage> HydrateAsync(
            DbConnection connection,
            DbTransaction transaction,
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestDocumentHydrator : IDocumentHydrator
    {
        public Task<HydratedPage> HydrateAsync(
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken ct
        )
        {
            throw new NotSupportedException();
        }
    }
}
