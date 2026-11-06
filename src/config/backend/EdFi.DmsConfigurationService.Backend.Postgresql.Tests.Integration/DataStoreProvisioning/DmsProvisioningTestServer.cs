// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The target PostgreSQL server of the provisioning integration lane, resolved from
/// <c>ConnectionStrings__PostgresqlDmsAdmin</c>. The lane skips when the variable is absent; it will fail
/// closed under GitHub Actions once the CI lane exists (spec 9, Step 5.2). Databases the tests build are
/// named <c>dms1438_&lt;purpose&gt;_&lt;12 hex&gt;</c> and dropped with <c>WITH (FORCE)</c>; the cluster role
/// <c>edfi_dms_enqueue_owner</c> is created locked down when missing and left in place.
/// </summary>
internal static class DmsProvisioningTestServer
{
    public const string Category = "DmsProvisioning";

    public const string AdminEnvironmentVariableName = "ConnectionStrings__PostgresqlDmsAdmin";

    public const string FixtureDirectory = "Fixtures/DataStoreProvisioning";

    private const string EnsureEnqueueOwnerRole = """
        DO $$
        BEGIN
            IF pg_catalog.to_regrole('edfi_dms_enqueue_owner') IS NULL THEN
                CREATE ROLE "edfi_dms_enqueue_owner" WITH NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            END IF;
        END $$;
        """;

    public static string? AdminConnectionString =>
        Environment.GetEnvironmentVariable(AdminEnvironmentVariableName);

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(AdminConnectionString);

    public static void RequireConfiguredOrSkip()
    {
        if (!IsConfigured)
        {
            Assert.Ignore(
                $"{AdminEnvironmentVariableName} is not set; the DMS provisioning PostgreSQL lane is skipped."
            );
        }
    }

    public static PostgresqlAdminConnection AdminConnection() =>
        new(AdminConnectionString!, TimeSpan.FromSeconds(30));

    /// <summary>An engine over the lane's admin connection; the lock settings default to the 4.3 defaults on the system clock.</summary>
    public static PostgresqlDmsTargetEngine Engine(
        TimeSpan? lockAcquireTimeout = null,
        TimeSpan? lockHeartbeatInterval = null,
        TimeProvider? timeProvider = null
    ) =>
        new(
            AdminConnection(),
            lockAcquireTimeout ?? TimeSpan.FromSeconds(5),
            lockHeartbeatInterval ?? TimeSpan.FromSeconds(5),
            timeProvider ?? TimeProvider.System,
            NullLogger.Instance
        );

    public static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, FixtureDirectory, fileName);

    public static string FixtureText(string fileName) =>
        File.ReadAllText(FixturePath(fileName)).TrimEnd('\n');

    /// <summary>Runs <paramref name="sql"/> on the maintenance database.</summary>
    public static async Task ExecuteOnServerAsync(string sql)
    {
        await using NpgsqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static Task EnsureEnqueueOwnerRoleAsync() => ExecuteOnServerAsync(EnsureEnqueueOwnerRole);

    /// <summary>Whether <paramref name="pid"/> holds the granted session-level advisory lock for <paramref name="key"/>, looked up as the 7.6 operator procedure does.</summary>
    public static Task<bool> AdvisoryLockGrantedAsync(long pid, long key) =>
        ScalarOnServerAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks l WHERE l.locktype = 'advisory' AND l.pid = @pid AND l.objsubid = 1 AND l.granted AND l.database = (SELECT d.oid FROM pg_catalog.pg_database d WHERE d.datname = 'postgres') AND l.classid::bigint = @hi AND l.objid::bigint = @lo);",
            ("pid", (int)pid),
            ("hi", (long)(uint)((ulong)key >> 32)),
            ("lo", (long)(uint)(ulong)key)
        );

    public static Task<bool> SessionExistsAsync(long pid) =>
        ScalarOnServerAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity a WHERE a.pid = @pid);",
            ("pid", (int)pid)
        );

    /// <summary>Sessions carrying <paramref name="applicationName"/>.</summary>
    public static Task<int> SessionCountNamedAsync(string applicationName) =>
        ScalarOnServerAsync<int>(
            "SELECT COUNT(*)::int FROM pg_catalog.pg_stat_activity a WHERE a.application_name = @app;",
            ("app", applicationName)
        );

    /// <summary>The pid of the session carrying <paramref name="applicationName"/>, or 0 when there is none.</summary>
    public static Task<int> PidNamedAsync(string applicationName) =>
        ScalarOnServerAsync<int>(
            "SELECT COALESCE((SELECT a.pid FROM pg_catalog.pg_stat_activity a WHERE a.application_name = @app LIMIT 1), 0);",
            ("app", applicationName)
        );

    /// <summary>The session's wait event as <c>type:event</c> (<c>Lock:advisory</c> while waiting on an advisory lock); empty when it is not waiting or does not exist.</summary>
    public static Task<string> WaitEventOfAsync(long pid) =>
        ScalarOnServerAsync<string>(
            "SELECT COALESCE((SELECT a.wait_event_type || ':' || a.wait_event FROM pg_catalog.pg_stat_activity a WHERE a.pid = @pid), '');",
            ("pid", (int)pid)
        );

    public static Task<bool> TerminateBackendAsync(long pid) =>
        ScalarOnServerAsync<bool>("SELECT pg_catalog.pg_terminate_backend(@pid);", ("pid", (int)pid));

    /// <summary>Reads one value on the maintenance database.</summary>
    public static async Task<T> ScalarOnServerAsync<T>(
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using NpgsqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("The server query returned no row.");
        }

        return await reader.GetFieldValueAsync<T>(0);
    }

    /// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> passes; the server observes a closed backend eventually, not instantly.</summary>
    public static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return true;
    }

    /// <summary>Creates an empty database and, when given, replays <paramref name="script"/> into it.</summary>
    public static async Task<TestDatabase> CreateDatabaseAsync(string purpose, string? script = null)
    {
        string name = $"dms1438_{purpose}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";
        await ExecuteOnServerAsync($"CREATE DATABASE {PostgresqlIdentifiers.Quote(name)};");
        TestDatabase database = new(name);
        if (script is not null)
        {
            await database.ExecuteAsync(script);
        }

        return database;
    }

    /// <summary>A database built from the committed DMS-shaped fixture; the enqueue-owner role is ensured first.</summary>
    public static async Task<TestDatabase> CreateDmsShapedDatabaseAsync(string purpose)
    {
        await EnsureEnqueueOwnerRoleAsync();
        return await CreateDatabaseAsync(
            purpose,
            await File.ReadAllTextAsync(FixturePath("dms-shaped-database.sql"))
        );
    }

    /// <summary>Creates a locked-down role for a drift test; dropped with <see cref="DropRoleAsync"/> after the databases that reference it.</summary>
    public static async Task<string> CreateRoleAsync(string purpose)
    {
        string name = $"dms1438_{purpose}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";
        await ExecuteOnServerAsync($"CREATE ROLE {PostgresqlIdentifiers.Quote(name)} WITH NOLOGIN;");
        return name;
    }

    public static Task DropRoleAsync(string name) =>
        ExecuteOnServerAsync($"DROP ROLE IF EXISTS {PostgresqlIdentifiers.Quote(name)};");

    /// <summary>A database the tests own: executes statements on it, reads its catalog, and drops it on dispose.</summary>
    public sealed class TestDatabase(string name) : IAsyncDisposable
    {
        public string Name { get; } = name;

        public DmsDatabaseName DatabaseName =>
            DmsDatabaseName.TryCreate(Name, out DmsDatabaseName databaseName)
                ? databaseName
                : throw new InvalidOperationException("The test database name does not satisfy the grammar.");

        public async Task ExecuteAsync(string sql)
        {
            await using NpgsqlConnection connection = await OpenAsync();
            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using NpgsqlConnection connection = await OpenAsync();
            await using NpgsqlCommand command = new(sql, connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException("The scalar query returned no row.");
            }

            return await reader.GetFieldValueAsync<T>(0);
        }

        /// <summary>The database's <c>pg_database.oid</c> and comment as the catalog holds them now.</summary>
        public async Task<DmsInstanceIdentity.Postgresql> CatalogInstanceAsync()
        {
            await using NpgsqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
            await using NpgsqlCommand command = new(
                "SELECT d.oid, COALESCE(pg_catalog.shobj_description(d.oid, 'pg_database'), '') FROM pg_catalog.pg_database d WHERE d.datname = @name;",
                connection
            );
            command.Parameters.AddWithValue("name", Name);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException("The test database is not in the catalog.");
            }

            uint oid = await reader.GetFieldValueAsync<uint>(0);
            return new DmsInstanceIdentity.Postgresql(oid, reader.GetString(1));
        }

        /// <summary>Sessions on this database with the engine's session application name.</summary>
        public async Task<int> EngineSessionCountAsync()
        {
            await using NpgsqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
            await using NpgsqlCommand command = new(
                "SELECT COUNT(*)::int FROM pg_catalog.pg_stat_activity WHERE datname = @name AND application_name = @app;",
                connection
            );
            command.Parameters.AddWithValue("name", Name);
            command.Parameters.AddWithValue("app", PostgresqlAdminConnection.SessionApplicationName);
            return (int)(await command.ExecuteScalarAsync() ?? 0);
        }

        public async ValueTask DisposeAsync() =>
            await ExecuteOnServerAsync(
                $"DROP DATABASE IF EXISTS {PostgresqlIdentifiers.Quote(Name)} WITH (FORCE);"
            );

        private Task<NpgsqlConnection> OpenAsync() =>
            AdminConnection().OpenAsync(Name, "dms1438-test", default);
    }
}
