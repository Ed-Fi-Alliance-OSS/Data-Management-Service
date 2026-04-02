// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
        scope.ServiceProvider.GetService<IRelationalWriteTargetLookupResolver>().Should().BeNull();
        factory.Should().BeOfType<TestReferenceResolverAdapterFactory>();
        adapter.Should().BeOfType<TestReferenceResolverAdapter>();
    }

    [Test]
    public void It_registers_the_relational_access_seam_for_dialect_composition()
    {
        var services = new ServiceCollection();

        services.AddReferenceResolver<
            ExecutorBackedReferenceResolverAdapterFactory,
            TestRelationalCommandExecutor,
            TestRelationalWriteSessionFactory
        >();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        var commandExecutor = scope.ServiceProvider.GetRequiredService<IRelationalCommandExecutor>();
        var writeSessionFactory = scope.ServiceProvider.GetRequiredService<IRelationalWriteSessionFactory>();
        var writeFlattener = scope.ServiceProvider.GetRequiredService<IRelationalWriteFlattener>();
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
        writeFlattener.Should().BeOfType<RelationalWriteFlattener>();
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
}
