// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Common.NoProfileUpdateSemanticsScenarios;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("CanonicalWriteEnqueueRetry")]
public class Given_Postgresql_Canonical_Write_Enqueue_Retry
{
    private const int LockTimeoutMilliseconds = 100;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlBaselineDatabase _baseline = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private MappingSet _mappingSet = null!;
    private ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _baseline = await PostgresqlGeneratedDdlBaselineDatabase.CreateAsync(
            $"{nameof(Given_Postgresql_Canonical_Write_Enqueue_Retry)}:{_fixture.MappingSet.Key.EffectiveSchemaHash}",
            _fixture.GeneratedDdl
        );
    }

    [SetUp]
    public async Task SetUp()
    {
        _database = await _baseline.CreateIsolatedDatabaseAsync();
        _serviceProvider = CreateServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_baseline is not null)
        {
            await _baseline.DisposeAsync();
            _baseline = null!;
        }
    }

    [TestCase(CanonicalRepositoryWriteKind.PostAsUpdate)]
    [TestCase(CanonicalRepositoryWriteKind.Put)]
    public async Task It_maps_repository_enqueue_lock_timeout_to_retryable_write_conflict(
        CanonicalRepositoryWriteKind writeKind
    )
    {
        await SetTrackingLifecycleAsync();
        await ExecuteCreateAsync();
        ProjectionWorkState before = await ReadProjectionWorkStateAsync();

        await using NpgsqlConnection blockerConnection = new(_database.ConnectionString);
        await blockerConnection.OpenAsync();
        await using NpgsqlTransaction blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await LockProjectionWorkRowAsync(blockerConnection, blockerTransaction, before.DocumentId);

        try
        {
            object result = await ExecuteBlockedCanonicalWriteAsync(writeKind);

            switch (writeKind)
            {
                case CanonicalRepositoryWriteKind.PostAsUpdate:
                    result.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();
                    break;
                case CanonicalRepositoryWriteKind.Put:
                    result.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(writeKind), writeKind, null);
            }
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
        }

        ProjectionWorkState after = await ReadProjectionWorkStateAsync();
        after.ContentVersion.Should().Be(before.ContentVersion);
        after.RequiredContentVersion.Should().Be(before.RequiredContentVersion);
    }

    private async Task ExecuteCreateAsync()
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        UpsertResult createResult = await repository.UpsertDocument(
            new UpsertRequest(
                ResourceInfo: SchoolResourceInfo,
                DocumentInfo: CreateSchoolDocumentInfo(),
                MappingSet: _mappingSet,
                EdfiDoc: CreateRequestBody(),
                Headers: [],
                TraceId: new TraceId("pg-canonical-enqueue-retry-create"),
                DocumentUuid: SchoolDocumentUuid
            )
        );

        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<object> ExecuteBlockedCanonicalWriteAsync(CanonicalRepositoryWriteKind writeKind)
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return writeKind switch
        {
            CanonicalRepositoryWriteKind.PostAsUpdate => await repository.UpsertDocument(
                new UpsertRequest(
                    ResourceInfo: SchoolResourceInfo,
                    DocumentInfo: CreateSchoolDocumentInfo(),
                    MappingSet: _mappingSet,
                    EdfiDoc: UpdateRequestBody(),
                    Headers: [],
                    TraceId: new TraceId("pg-canonical-enqueue-retry-post"),
                    DocumentUuid: SchoolDocumentUuid
                )
            ),
            CanonicalRepositoryWriteKind.Put => await repository.UpdateDocumentById(
                new UpdateRequest(
                    ResourceInfo: SchoolResourceInfo,
                    DocumentInfo: CreateSchoolDocumentInfo(),
                    MappingSet: _mappingSet,
                    EdfiDoc: UpdateRequestBody(),
                    Headers: [],
                    TraceId: new TraceId("pg-canonical-enqueue-retry-put"),
                    DocumentUuid: SchoolDocumentUuid
                )
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(writeKind), writeKind, null),
        };
    }

    private async Task SetTrackingLifecycleAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = 'Tracking',
                "CacheAheadRecoveryRequired" = FALSE
            WHERE "StateId" = 1;
            """
        );
    }

    private async Task<ProjectionWorkState> ReadProjectionWorkStateAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT document."DocumentId",
                   document."ContentVersion",
                   work."RequiredContentVersion"
            FROM "dms"."Document" document
            INNER JOIN "dms"."DocumentProjectionWork" work
                ON work."DocumentId" = document."DocumentId"
            WHERE document."DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = SchoolDocumentUuid.Value }
        );

        rows.Should().ContainSingle();

        return new ProjectionWorkState(
            Convert.ToInt64(rows[0]["DocumentId"]),
            Convert.ToInt64(rows[0]["ContentVersion"]),
            Convert.ToInt64(rows[0]["RequiredContentVersion"])
        );
    }

    private static async Task LockProjectionWorkRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long documentId
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM "dms"."DocumentProjectionWork"
            WHERE "DocumentId" = @documentId
            FOR UPDATE;
            """;
        command.Parameters.Add(new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId });

        object? result = await command.ExecuteScalarAsync();
        result
            .Should()
            .NotBeNull("the seed repository insert should enqueue projection work before the lock is taken");
    }

    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new DeadlockRetrySettings());
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlBackendIntegrationTestServices();
        services.AddScoped<PostgresqlRelationalWriteSessionFactory>();
        services.Replace(
            ServiceDescriptor.Scoped<IRelationalWriteSessionFactory>(
                serviceProvider => new LockTimeoutWriteSessionFactory(
                    serviceProvider.GetRequiredService<PostgresqlRelationalWriteSessionFactory>(),
                    $"""
                    SET LOCAL lock_timeout = '{LockTimeoutMilliseconds}ms';
                    """
                )
            )
        );

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlCanonicalWriteEnqueueRetry",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private sealed class LockTimeoutWriteSessionFactory(
        IRelationalWriteSessionFactory inner,
        string setLockTimeoutCommandText
    ) : IRelationalWriteSessionFactory
    {
        private readonly IRelationalWriteSessionFactory _inner =
            inner ?? throw new ArgumentNullException(nameof(inner));

        private readonly string _setLockTimeoutCommandText =
            setLockTimeoutCommandText ?? throw new ArgumentNullException(nameof(setLockTimeoutCommandText));

        public async Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
        {
            IRelationalWriteSession session = await _inner
                .CreateAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await using DbCommand command = session.Connection.CreateCommand();
                command.Transaction = session.Transaction;
                command.CommandText = _setLockTimeoutCommandText;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                return session;
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private sealed record ProjectionWorkState(
        long DocumentId,
        long ContentVersion,
        long RequiredContentVersion
    );

    public enum CanonicalRepositoryWriteKind
    {
        PostAsUpdate,
        Put,
    }
}
