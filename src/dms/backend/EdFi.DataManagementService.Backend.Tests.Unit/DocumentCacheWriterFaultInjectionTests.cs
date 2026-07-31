// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheWriterFaultInjection")]
public class Given_DocumentCacheWriterFaultInjection
{
    [Test]
    public void It_defines_only_the_story_required_transaction_hook_points()
    {
        Enum.GetNames<DocumentCacheWriterFaultInjectionHook>()
            .Should()
            .Equal(
                nameof(
                    DocumentCacheWriterFaultInjectionHook.AfterMainStateLockAndClassificationBeforeCacheDml
                ),
                nameof(DocumentCacheWriterFaultInjectionHook.AfterCacheDmlBeforeAcknowledgement),
                nameof(DocumentCacheWriterFaultInjectionHook.AfterAcknowledgementBeforeCommit),
                nameof(DocumentCacheWriterFaultInjectionHook.AfterCacheAheadLatchUpdateBeforeIncidentCommit)
            );

        Enum.GetNames<DocumentCacheWriterFaultInjectionHook>()
            .Should()
            .NotContain(name => name.Contains("AfterCommit", StringComparison.Ordinal));
    }

    [Test]
    public void It_carries_only_bounded_sanitized_context_labels()
    {
        var targetKey = new DocumentCacheProjectionTargetKey(
            $"tenant-with-line-break\n{new string('x', 200)}",
            new DataStoreId(7)
        );

        var context = new DocumentCacheWriterFaultInjectionContext(
            DocumentCacheWriterFaultInjectionHook.AfterCacheDmlBeforeAcknowledgement,
            RelationalProviderToken.Postgresql,
            targetKey,
            DocumentCacheWriterPurpose.DurableWorkProjection,
            DocumentCacheLifecycleState.Tracking,
            cacheAheadRecoveryRequired: false,
            DocumentCacheWriterOutcome.CandidateWrittenAcknowledged,
            cacheDmlRowCount: 1,
            acknowledgementRowCount: null,
            cacheAheadLatchRowCount: null
        );

        context.Provider.Should().Be(RelationalProviderToken.PostgresqlValue);
        context.TargetKey.Should().NotContain("\n");
        context.TargetKey.Length.Should().BeLessThanOrEqualTo(128);
        context.Purpose.Should().Be(DocumentCacheWriterPurpose.DurableWorkProjection);
        context.LifecycleState.Should().Be(DocumentCacheLifecycleState.Tracking);
        context.Outcome.Should().Be(DocumentCacheWriterOutcome.CandidateWrittenAcknowledged);
        context.CacheDmlRowCount.Should().Be(1);

        typeof(DocumentCacheWriterFaultInjectionContext)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Should()
            .NotContain(propertyName =>
                propertyName.Contains("DocumentId", StringComparison.Ordinal)
                || propertyName.Contains("DocumentUuid", StringComparison.Ordinal)
                || propertyName.Contains("DocumentJson", StringComparison.Ordinal)
                || propertyName.Contains("Authorization", StringComparison.Ordinal)
                || propertyName.Contains("RequestBody", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_defaults_to_a_no_op_observer_that_does_not_alter_writer_control()
    {
        RecordingDbConnection connection = new();
        RecordingDbTransaction transaction = new(connection);
        DocumentCacheWriterFaultInjectionControl control = new(connection, transaction);

        await NoOpTransactionFaultInjectionObserver.Instance.ObserveAsync(
            CreateContext(DocumentCacheWriterFaultInjectionHook.AfterAcknowledgementBeforeCommit),
            control,
            CancellationToken.None
        );

        connection.CloseCount.Should().Be(0);
        transaction.RollbackCount.Should().Be(0);
    }

    [Test]
    public async Task It_allows_tests_to_close_the_connection_or_force_transaction_rollback()
    {
        RecordingDbConnection connection = new();
        RecordingDbTransaction transaction = new(connection);
        DocumentCacheWriterFaultInjectionControl control = new(connection, transaction);

        await control.CloseConnectionAsync(CancellationToken.None);
        await control.RollbackTransactionAsync(CancellationToken.None);

        connection.CloseCount.Should().Be(1);
        transaction.RollbackCount.Should().Be(1);
    }

    [Test]
    public void ServiceRegistration_registers_the_default_observer_as_no_op()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(FakeItEasy.A.Fake<IReadableProfileProjector>());

        services.AddReferenceResolver<
            ExecutorBackedReferenceResolverAdapterFactory,
            TestRelationalCommandExecutor,
            TestRelationalWriteSessionFactory,
            TestDocumentHydrator,
            TestSessionDocumentHydrator
        >();

        ServiceDescriptor descriptor = services.Single(descriptor =>
            descriptor.ServiceType == typeof(ITransactionFaultInjectionObserver)
        );

        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationInstance.Should().BeSameAs(NoOpTransactionFaultInjectionObserver.Instance);

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<ITransactionFaultInjectionObserver>()
            .Should()
            .BeSameAs(NoOpTransactionFaultInjectionObserver.Instance);
    }

    private static DocumentCacheWriterFaultInjectionContext CreateContext(
        DocumentCacheWriterFaultInjectionHook hook
    ) =>
        new(
            hook,
            RelationalProviderToken.Postgresql,
            new DocumentCacheProjectionTargetKey("tenant", new DataStoreId(7)),
            DocumentCacheWriterPurpose.DurableWorkProjection,
            DocumentCacheLifecycleState.Tracking,
            cacheAheadRecoveryRequired: false,
            DocumentCacheWriterOutcome.AlreadyCurrentAcknowledged
        );

    private static ServiceProvider BuildServiceProvider(IServiceCollection services)
    {
        services.TryAddSingleton<IDocumentLinkSlugResolver, NoLinkSlugResolver>();
        services.AddOptions<ResourceLinksOptions>();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private sealed class NoLinkSlugResolver : IDocumentLinkSlugResolver
    {
        public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId) =>
            throw new InvalidOperationException("NoLinkSlugResolver is unused in composition-surface tests.");
    }

    private sealed class ExecutorBackedReferenceResolverAdapterFactory(
        IRelationalCommandExecutor commandExecutor
    ) : IReferenceResolverAdapterFactory
    {
        public IRelationalCommandExecutor CommandExecutor { get; } =
            commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

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

    private sealed class RecordingDbConnection : DbConnection
    {
        public int CloseCount { get; private set; }

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "FaultInjectionTest";

        public override string DataSource => "FaultInjectionTest";

        public override string ServerVersion => "1";

        public override ConnectionState State =>
            CloseCount == 0 ? ConnectionState.Open : ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) { }

        public override void Close()
        {
            CloseCount++;
        }

        public override void Open() { }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class RecordingDbTransaction(RecordingDbConnection connection) : DbTransaction
    {
        public int RollbackCount { get; private set; }

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection DbConnection { get; } = connection;

        public override void Commit() { }

        public override void Rollback()
        {
            RollbackCount++;
        }
    }
}
