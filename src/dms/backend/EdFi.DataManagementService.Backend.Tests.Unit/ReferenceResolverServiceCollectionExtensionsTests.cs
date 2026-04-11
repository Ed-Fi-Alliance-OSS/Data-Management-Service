// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_ReferenceResolver_Service_Collection_Extensions
{
    [Test]
    public void It_registers_the_shared_resolver_composition_surface()
    {
        var services = new ServiceCollection();

        services.AddReferenceResolver<TestReferenceResolverAdapterFactory>();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IReferenceResolver>();
        var factory = scope.ServiceProvider.GetRequiredService<IReferenceResolverAdapterFactory>();
        var adapter = scope.ServiceProvider.GetRequiredService<IReferenceResolverAdapter>();

        resolver.Should().BeOfType<ReferenceResolver>();
        scope.ServiceProvider.GetService<IDescriptorWriteHandler>().Should().BeNull();
        scope.ServiceProvider.GetService<IRelationalWriteTargetLookupService>().Should().BeNull();
        scope.ServiceProvider.GetService<IRelationalWriteTargetLookupResolver>().Should().BeNull();
        factory.Should().BeOfType<TestReferenceResolverAdapterFactory>();
        adapter.Should().BeOfType<TestReferenceResolverAdapter>();
    }

    [Test]
    public void It_registers_the_relational_access_seam_for_dialect_composition()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddReferenceResolver<
            ExecutorBackedReferenceResolverAdapterFactory,
            TestRelationalCommandExecutor,
            TestRelationalWriteSessionFactory,
            TestDocumentHydrator,
            TestSessionDocumentHydrator
        >();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        var commandExecutor = scope.ServiceProvider.GetRequiredService<IRelationalCommandExecutor>();
        var writeSessionFactory = scope.ServiceProvider.GetRequiredService<IRelationalWriteSessionFactory>();
        var documentHydrator = scope.ServiceProvider.GetRequiredService<IDocumentHydrator>();
        var writeFlattener = scope.ServiceProvider.GetRequiredService<IRelationalWriteFlattener>();
        var sessionDocumentHydrator = scope.ServiceProvider.GetRequiredService<ISessionDocumentHydrator>();
        var readMaterializer = scope.ServiceProvider.GetRequiredService<IRelationalReadMaterializer>();
        var readTargetLookupService =
            scope.ServiceProvider.GetRequiredService<IRelationalReadTargetLookupService>();
        var currentStateLoader =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteCurrentStateLoader>();
        var writeFreshnessChecker =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteFreshnessChecker>();
        var noProfileMergeSynthesizer =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteNoProfileMergeSynthesizer>();
        var noProfilePersister =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteNoProfilePersister>();
        var writeExceptionClassifier =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteExceptionClassifier>();
        var descriptorWriteHandler = scope.ServiceProvider.GetRequiredService<IDescriptorWriteHandler>();
        var targetLookupService =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteTargetLookupService>();
        var targetLookupResolver =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteTargetLookupResolver>();
        var writeExecutor = scope.ServiceProvider.GetRequiredService<IRelationalWriteExecutor>();
        var factory = scope
            .ServiceProvider.GetRequiredService<IReferenceResolverAdapterFactory>()
            .Should()
            .BeOfType<ExecutorBackedReferenceResolverAdapterFactory>()
            .Subject;
        var adapter = scope
            .ServiceProvider.GetRequiredService<IReferenceResolverAdapter>()
            .Should()
            .BeOfType<ExecutorBackedReferenceResolverAdapter>()
            .Subject;

        commandExecutor.Should().BeOfType<TestRelationalCommandExecutor>();
        writeSessionFactory.Should().BeOfType<TestRelationalWriteSessionFactory>();
        documentHydrator.Should().BeOfType<TestDocumentHydrator>();
        writeFlattener.Should().BeOfType<RelationalWriteFlattener>();
        sessionDocumentHydrator.Should().BeOfType<TestSessionDocumentHydrator>();
        readMaterializer.Should().BeOfType<RelationalReadMaterializer>();
        readTargetLookupService.Should().BeOfType<RelationalReadTargetLookupService>();
        currentStateLoader.Should().BeOfType<RelationalWriteCurrentStateLoader>();
        writeFreshnessChecker.Should().BeOfType<RelationalWriteFreshnessChecker>();
        noProfileMergeSynthesizer.Should().BeOfType<RelationalWriteNoProfileMergeSynthesizer>();
        noProfilePersister.Should().BeOfType<RelationalWriteNoProfilePersister>();
        writeExceptionClassifier.Should().BeOfType<NoOpRelationalWriteExceptionClassifier>();
        descriptorWriteHandler.Should().BeOfType<DescriptorWriteHandler>();
        targetLookupService.Should().BeOfType<RelationalWriteTargetLookupService>();
        targetLookupResolver.Should().BeOfType<RelationalWriteTargetLookupResolver>();
        writeExecutor.Should().BeOfType<DefaultRelationalWriteExecutor>();
        factory.CommandExecutor.Should().BeSameAs(commandExecutor);
        adapter.CommandExecutor.Should().BeSameAs(commandExecutor);
    }

    private static ServiceProvider BuildServiceProvider(IServiceCollection services)
    {
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private sealed class TestReferenceResolverAdapterFactory : IReferenceResolverAdapterFactory
    {
        public IReferenceResolverAdapter CreateAdapter() => new TestReferenceResolverAdapter();

        public IReferenceResolverAdapter CreateSessionAdapter(
            DbConnection connection,
            DbTransaction transaction
        ) => new TestReferenceResolverAdapter();
    }

    private sealed class TestReferenceResolverAdapter : IReferenceResolverAdapter
    {
        public Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
            ReferenceLookupRequest request,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult<IReadOnlyList<ReferenceLookupResult>>([]);
        }
    }

    private sealed class ExecutorBackedReferenceResolverAdapterFactory(
        IRelationalCommandExecutor commandExecutor
    ) : IReferenceResolverAdapterFactory
    {
        public IRelationalCommandExecutor CommandExecutor { get; } =
            commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

        public IReferenceResolverAdapter CreateAdapter()
        {
            return new ExecutorBackedReferenceResolverAdapter(CommandExecutor);
        }

        public IReferenceResolverAdapter CreateSessionAdapter(
            DbConnection connection,
            DbTransaction transaction
        )
        {
            return new ExecutorBackedReferenceResolverAdapter(
                new SessionRelationalCommandExecutor(connection, transaction)
            );
        }
    }

    private sealed class ExecutorBackedReferenceResolverAdapter(IRelationalCommandExecutor commandExecutor)
        : IReferenceResolverAdapter
    {
        public IRelationalCommandExecutor CommandExecutor { get; } =
            commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

        public Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
            ReferenceLookupRequest request,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult<IReadOnlyList<ReferenceLookupResult>>([]);
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
            CancellationToken ct
        )
        {
            throw new NotSupportedException();
        }
    }
}
