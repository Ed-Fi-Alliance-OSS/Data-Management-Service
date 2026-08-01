// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("DocumentCacheAdministrativeMutex")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheAdministrativeMutex
{
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private MssqlDocumentCacheAdministrativeMutex _mutex = null!;

    [SetUp]
    public async Task SetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
        );

        _database = await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        _mutex = new MssqlDocumentCacheAdministrativeMutex(
            NullLogger<MssqlDocumentCacheAdministrativeMutex>.Instance
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_acquires_the_public_session_application_lock_identity()
    {
        await using IDocumentCacheAdministrativeMutexLease lease = await _mutex.AcquireAsync(
            ConnectionInput(_database.ConnectionString)
        );

        string lockMode = await ExecuteScalarAsync<string>(
            lease.Connection,
            """
            SELECT APPLOCK_MODE(
                N'public',
                N'EdFi.DMS.DocumentProjection.Administration.v1',
                N'Session'
            );
            """
        );

        lockMode.Should().Be("Exclusive");
    }

    [Test]
    public async Task It_serializes_alias_connections_to_the_same_database()
    {
        IDocumentCacheAdministrativeMutexLease? firstLease = await _mutex.AcquireAsync(
            ConnectionInput(_database.ConnectionString)
        );
        try
        {
            Task<IDocumentCacheAdministrativeMutexLease> blockedAcquire = _mutex.AcquireAsync(
                ConnectionInput(
                    AliasConnectionString(_database.ConnectionString, "document-cache-mutex-alias")
                )
            );

            Task completedTask = await Task.WhenAny(
                blockedAcquire,
                Task.Delay(TimeSpan.FromMilliseconds(250))
            );
            completedTask.Should().NotBe(blockedAcquire);

            await firstLease.DisposeAsync();
            firstLease = null;

            await using IDocumentCacheAdministrativeMutexLease secondLease = await blockedAcquire.WaitAsync(
                TimeSpan.FromSeconds(5)
            );
            secondLease.IsSessionOpen.Should().BeTrue();
        }
        finally
        {
            if (firstLease is not null)
            {
                await firstLease.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task It_allows_independent_databases_to_acquire_without_waiting()
    {
        await using MssqlGeneratedDdlTestDatabase independentDatabase =
            await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        await using IDocumentCacheAdministrativeMutexLease firstLease = await _mutex.AcquireAsync(
            ConnectionInput(_database.ConnectionString)
        );

        await using IDocumentCacheAdministrativeMutexLease secondLease = await _mutex
            .AcquireAsync(ConnectionInput(independentDatabase.ConnectionString))
            .WaitAsync(TimeSpan.FromSeconds(5));

        secondLease.IsSessionOpen.Should().BeTrue();
    }

    [Test]
    public async Task It_keeps_one_physical_session_across_short_transactions()
    {
        await using IDocumentCacheAdministrativeMutexLease lease = await _mutex.AcquireAsync(
            ConnectionInput(_database.ConnectionString)
        );
        int sessionId = await ExecuteScalarAsync<int>(lease.Connection, "SELECT @@SPID;");

        await using (IRelationalWriteSession session = await lease.BeginTransactionAsync())
        {
            await ExecuteSessionNonQueryAsync(
                session,
                """
                CREATE TABLE #MutexSessionProbe ([Id] int NOT NULL);
                INSERT INTO #MutexSessionProbe ([Id]) VALUES (1);
                """
            );
            await session.CommitAsync();
        }

        await using (IRelationalWriteSession session = await lease.BeginTransactionAsync())
        {
            int observedSessionId = await ExecuteSessionScalarAsync<int>(session, "SELECT @@SPID;");
            int rowCount = await ExecuteSessionScalarAsync<int>(
                session,
                "SELECT COUNT(*) FROM #MutexSessionProbe;"
            );

            observedSessionId.Should().Be(sessionId);
            rowCount.Should().Be(1);
            await session.CommitAsync();
        }
    }

    [Test]
    public async Task It_does_not_reconnect_after_mutex_session_loss()
    {
        await using IDocumentCacheAdministrativeMutexLease lease = await _mutex.AcquireAsync(
            ConnectionInput(_database.ConnectionString)
        );
        await lease.Connection.CloseAsync();

        Func<Task> beginTransactionAsync = async () =>
        {
            await using IRelationalWriteSession session = await lease.BeginTransactionAsync();
        };

        await beginTransactionAsync
            .Should()
            .ThrowAsync<DocumentCacheAdministrativeMutexSessionLostException>();
        lease.IsSessionOpen.Should().BeFalse();
    }

    private static DocumentCacheTargetConnectionInput ConnectionInput(string connectionString) =>
        new(RelationalProviderToken.SqlServer, connectionString);

    private static string AliasConnectionString(string connectionString, string applicationName)
    {
        SqlConnectionStringBuilder builder = new(connectionString) { ApplicationName = applicationName };

        return builder.ConnectionString;
    }

    private static async Task ExecuteSessionNonQueryAsync(IRelationalWriteSession session, string commandText)
    {
        await using DbCommand command = session.Connection.CreateCommand();
        command.Transaction = session.Transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ExecuteSessionScalarAsync<T>(
        IRelationalWriteSession session,
        string commandText
    )
    {
        await using DbCommand command = session.Connection.CreateCommand();
        command.Transaction = session.Transaction;
        command.CommandText = commandText;

        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    private static async Task<T> ExecuteScalarAsync<T>(DbConnection connection, string commandText)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = commandText;

        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }
}
