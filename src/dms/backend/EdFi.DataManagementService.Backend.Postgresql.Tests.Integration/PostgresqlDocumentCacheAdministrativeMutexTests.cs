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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("DocumentCacheAdministrativeMutex")]
public class Given_A_Postgresql_DocumentCacheAdministrativeMutex
{
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSourceCache _dataSourceCache = null!;
    private PostgresqlDocumentCacheAdministrativeMutex _mutex = null!;

    [SetUp]
    public async Task SetUp()
    {
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        _dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        _mutex = new PostgresqlDocumentCacheAdministrativeMutex(
            _dataSourceCache,
            NullLogger<PostgresqlDocumentCacheAdministrativeMutex>.Instance
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        _dataSourceCache?.Dispose();

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_acquires_the_database_scoped_advisory_lock_identity()
    {
        await using IDocumentCacheAdministrativeMutexLease lease = await _mutex.AcquireAsync(
            ConnectionInput(_database.ConnectionString)
        );

        long matchingLockCount = await ExecuteScalarAsync<long>(
            lease.Connection,
            """
            SELECT COUNT(*)
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND database = (
                  SELECT database.oid
                  FROM pg_database AS database
                  WHERE database.datname = current_database()
              )
              AND classid = 811646948::oid
              AND objid = (
                  SELECT database.oid
                  FROM pg_database AS database
                  WHERE database.datname = current_database()
              )
              AND mode = 'ExclusiveLock'
              AND granted
              AND pid = pg_backend_pid();
            """
        );

        matchingLockCount.Should().Be(1);
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
        await using PostgresqlGeneratedDdlTestDatabase independentDatabase =
            await PostgresqlGeneratedDdlTestDatabase.CreateEmptyAsync();
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
        int backendPid = await ExecuteScalarAsync<int>(lease.Connection, "SELECT pg_backend_pid();");

        await using (IRelationalWriteSession session = await lease.BeginTransactionAsync())
        {
            await ExecuteSessionNonQueryAsync(
                session,
                """
                CREATE TEMP TABLE mutex_session_probe ("Id" integer NOT NULL) ON COMMIT PRESERVE ROWS;
                INSERT INTO mutex_session_probe ("Id") VALUES (1);
                """
            );
            await session.CommitAsync();
        }

        await using (IRelationalWriteSession session = await lease.BeginTransactionAsync())
        {
            int observedBackendPid = await ExecuteSessionScalarAsync<int>(
                session,
                "SELECT pg_backend_pid();"
            );
            int rowCount = await ExecuteSessionScalarAsync<int>(
                session,
                """SELECT COUNT(*) FROM mutex_session_probe;"""
            );

            observedBackendPid.Should().Be(backendPid);
            rowCount.Should().Be(1);
            await session.CommitAsync();
        }
    }

    [Test]
    public async Task It_releases_the_advisory_lock_when_the_owning_backend_session_is_terminated()
    {
        await using IDocumentCacheAdministrativeMutexLease lease = await _mutex.AcquireAsync(
            ConnectionInput(_database.ConnectionString)
        );
        int backendPid = await ExecuteScalarAsync<int>(lease.Connection, "SELECT pg_backend_pid();");
        bool terminated = await _database.ExecuteScalarAsync<bool>(
            "SELECT pg_terminate_backend(@backendPid);",
            new NpgsqlParameter("backendPid", backendPid)
        );

        terminated.Should().BeTrue();

        await using IDocumentCacheAdministrativeMutexLease replacementLease = await _mutex
            .AcquireAsync(ConnectionInput(_database.ConnectionString))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Func<Task> beginTransactionAsync = async () =>
        {
            await using IRelationalWriteSession session = await lease.BeginTransactionAsync();
        };

        await beginTransactionAsync
            .Should()
            .ThrowAsync<DocumentCacheAdministrativeMutexSessionLostException>();
        replacementLease.IsSessionOpen.Should().BeTrue();
        lease.IsSessionOpen.Should().BeFalse();
    }

    private static DocumentCacheTargetConnectionInput ConnectionInput(string connectionString) =>
        new(RelationalProviderToken.Postgresql, connectionString);

    private static string AliasConnectionString(string connectionString, string applicationName)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString)
        {
            Host = LoopbackHostAlias(new NpgsqlConnectionStringBuilder(connectionString).Host),
            ApplicationName = applicationName,
        };

        return builder.ConnectionString;
    }

    private static string LoopbackHostAlias(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InconclusiveException(
                "The PostgreSQL administrative mutex alias test requires a configured localhost or 127.0.0.1 host."
            );
        }

        return host.ToLowerInvariant() switch
        {
            "localhost" => "127.0.0.1",
            "127.0.0.1" => "localhost",
            _ => throw new InconclusiveException(
                "The PostgreSQL administrative mutex alias test requires a localhost or 127.0.0.1 host."
            ),
        };
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
