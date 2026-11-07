// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The target SQL Server of the provisioning integration lane, resolved from
/// <c>ConnectionStrings__MssqlDmsAdmin</c>. The lane skips when the variable is absent; it will fail closed
/// under GitHub Actions once the CI lane exists (spec 9, Step 5.2). Databases and logins the tests build are
/// named <c>dms1438_&lt;purpose&gt;_&lt;12 hex&gt;</c>; databases are dropped after any session on them is rolled
/// back, logins after their sessions are gone.
/// </summary>
internal static partial class MssqlProvisioningTestServer
{
    public const string Category = "DmsProvisioning";

    public const string AdminEnvironmentVariableName = "ConnectionStrings__MssqlDmsAdmin";

    public const string FixtureDirectory = "Fixtures/DataStoreProvisioning";

    /// <summary>The password of every login the tests create.</summary>
    public const string LoginPassword = "Dms1438-Login-Pa55word!";

    public static string? AdminConnectionString =>
        Environment.GetEnvironmentVariable(AdminEnvironmentVariableName);

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(AdminConnectionString);

    public static void RequireConfiguredOrSkip()
    {
        if (!IsConfigured)
        {
            Assert.Ignore(
                $"{AdminEnvironmentVariableName} is not set; the DMS provisioning SQL Server lane is skipped."
            );
        }
    }

    public static MssqlAdminConnection AdminConnection() =>
        new(AdminConnectionString!, TimeSpan.FromSeconds(30));

    /// <summary>The lane's admin connection authenticating as <paramref name="login"/> with <see cref="LoginPassword"/>; server and options are kept.</summary>
    public static MssqlAdminConnection AdminConnectionAs(string login) =>
        new(
            new SqlConnectionStringBuilder(AdminConnectionString!)
            {
                IntegratedSecurity = false,
                UserID = login,
                Password = LoginPassword,
            }.ConnectionString,
            TimeSpan.FromSeconds(30)
        );

    public static MssqlDmsTargetEngine Engine() => new(AdminConnection());

    public static MssqlDmsTargetEngine EngineAs(string login) => new(AdminConnectionAs(login));

    public static DmsDatabaseName DatabaseNamed(string value) =>
        DmsDatabaseName.TryCreate(value, out DmsDatabaseName name)
            ? name
            : throw new InvalidOperationException("The name does not satisfy the grammar.");

    public static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, FixtureDirectory, fileName);

    public static string FixtureText(string fileName) =>
        File.ReadAllText(FixturePath(fileName)).TrimEnd('\n');

    /// <summary>Runs <paramref name="sql"/>, split into batches at lines holding only <c>GO</c>, on the maintenance database.</summary>
    public static Task ExecuteOnServerAsync(string sql) =>
        ExecuteInAsync(MssqlAdminConnection.MaintenanceDatabaseName, sql);

    /// <summary>Reads one value on the maintenance database.</summary>
    public static async Task<T> ScalarOnServerAsync<T>(
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using SqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
        return await ReadScalarAsync<T>(connection, sql, parameters);
    }

    /// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> passes; the server observes a closed session eventually, not instantly.</summary>
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

    public static string UniqueName(string purpose) =>
        $"dms1438_{purpose}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";

    /// <summary>Creates an empty database and, when given, runs <paramref name="script"/> in it.</summary>
    public static async Task<TestDatabase> CreateDatabaseAsync(string purpose, string? script = null)
    {
        string name = UniqueName(purpose);
        await ExecuteOnServerAsync($"CREATE DATABASE {MssqlIdentifiers.Quote(name)};");
        TestDatabase database = new(name);
        if (script is not null)
        {
            await database.ExecuteAsync(script);
        }

        return database;
    }

    /// <summary>A database built from the committed DMS-shaped fixture.</summary>
    public static async Task<TestDatabase> CreateDmsShapedDatabaseAsync(string purpose) =>
        await CreateDatabaseAsync(
            purpose,
            await File.ReadAllTextAsync(FixturePath("dms-shaped-database.sql"))
        );

    /// <summary>A SQL login with <see cref="LoginPassword"/> and no server role; dropped with <see cref="DropLoginAsync"/>.</summary>
    public static async Task<string> CreateLoginAsync(string purpose)
    {
        string name = UniqueName(purpose);
        await ExecuteOnServerAsync(
            $"CREATE LOGIN {MssqlIdentifiers.Quote(name)} WITH PASSWORD = {MssqlIdentifiers.Literal(LoginPassword)}, CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;"
        );
        return name;
    }

    /// <summary>Drops <paramref name="login"/> once its sessions are gone (a login with a session cannot be dropped).</summary>
    public static async Task DropLoginAsync(string login)
    {
        await WaitUntilAsync(
            async () => await SessionCountOfLoginAsync(login) == 0,
            TimeSpan.FromSeconds(20)
        );
        await ExecuteOnServerAsync(
            $"IF SUSER_ID({MssqlIdentifiers.Literal(login)}) IS NOT NULL DROP LOGIN {MssqlIdentifiers.Quote(login)};"
        );
    }

    public static Task<int> SessionCountOfLoginAsync(string login) =>
        ScalarOnServerAsync<int>(
            "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name = @login;",
            ("@login", login)
        );

    /// <summary>
    /// A server-side path in the instance's default backup directory for <paramref name="fileName"/>. The tests
    /// that put a database into <c>RESTORING</c> write their backups there with <c>INIT</c> under fixed names, so
    /// each run overwrites the last one's files instead of adding more (SQL Server on Linux refuses
    /// <c>/dev/null</c> as a backup device).
    /// </summary>
    public static async Task<string> BackupPathAsync(string fileName)
    {
        string directory = await ScalarOnServerAsync<string>(
            "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultBackupPath'));"
        );
        char separator = directory.Contains('\\') ? '\\' : '/';
        return directory.TrimEnd('/', '\\') + separator + fileName;
    }

    private static async Task ExecuteInAsync(string database, string sql)
    {
        await using SqlConnection connection = await AdminConnection()
            .OpenAsync(database, "dms1438-test", default);
        foreach (string batch in Batches(sql))
        {
            await using SqlCommand command = new(batch, connection) { CommandTimeout = 60 };
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<T> ReadScalarAsync<T>(
        SqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using SqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("The server query returned no row.");
        }

        return await reader.GetFieldValueAsync<T>(0);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparator();

    private static IEnumerable<string> Batches(string sql) =>
        BatchSeparator()
            .Split(sql.Replace("\r\n", "\n", StringComparison.Ordinal))
            .Where(batch => !string.IsNullOrWhiteSpace(batch));

    /// <summary>A database the tests own: executes statements in it, reads its catalog row, and drops it on dispose.</summary>
    public sealed class TestDatabase(string name) : IAsyncDisposable
    {
        public string Name { get; } = name;

        public DmsDatabaseName DatabaseName => DatabaseNamed(Name);

        public Task ExecuteAsync(string sql) => ExecuteInAsync(Name, sql);

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using SqlConnection connection = await AdminConnection()
                .OpenAsync(Name, "dms1438-test", default);
            return await ReadScalarAsync<T>(connection, sql);
        }

        /// <summary>The database's <c>database_id</c>, primary-file <c>physical_name</c>, and <c>create_date</c> as the catalog holds them now.</summary>
        public async Task<DmsInstanceIdentity.Mssql> CatalogInstanceAsync()
        {
            await using SqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
            await using SqlCommand command = new(
                "SELECT d.database_id, (SELECT f.physical_name FROM sys.master_files f WHERE f.database_id = d.database_id AND f.file_id = 1), d.create_date FROM sys.databases d WHERE d.name = @name;",
                connection
            );
            command.Parameters.AddWithValue("@name", Name);
            await using SqlDataReader reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException("The test database is not in the catalog.");
            }

            DateTime createDate = await reader.GetFieldValueAsync<DateTime>(2);
            return new DmsInstanceIdentity.Mssql(
                reader.GetInt32(0),
                reader.GetString(1),
                new DateTimeOffset(DateTime.SpecifyKind(createDate, DateTimeKind.Utc), TimeSpan.Zero)
            );
        }

        /// <summary>Sessions on this database with the engine's session application name.</summary>
        public Task<int> EngineSessionCountAsync() =>
            ScalarOnServerAsync<int>(
                "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE database_id = DB_ID(@name) AND program_name = @app;",
                ("@name", Name),
                ("@app", MssqlAdminConnection.SessionApplicationName)
            );

        /// <summary>Renames the database and follows it, so disposal drops it under the new name.</summary>
        public async Task<TestDatabase> RenameAsync(string newName)
        {
            await ExecuteOnServerAsync(
                $"ALTER DATABASE {MssqlIdentifiers.Quote(Name)} MODIFY NAME = {MssqlIdentifiers.Quote(newName)};"
            );
            return new TestDatabase(newName);
        }

        /// <summary>
        /// Brings an offline database back online (so its files are removed with it), rolls back any session
        /// on an online one, then drops it; a restoring database is dropped as it is.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            string literal = MssqlIdentifiers.Literal(Name);
            string quoted = MssqlIdentifiers.Quote(Name);
            await ExecuteOnServerAsync(
                $"""
                IF DB_ID({literal}) IS NOT NULL
                BEGIN
                    IF (SELECT state_desc FROM sys.databases WHERE name = {literal}) = N'OFFLINE'
                        ALTER DATABASE {quoted} SET ONLINE;
                    IF (SELECT state_desc FROM sys.databases WHERE name = {literal}) <> N'RESTORING'
                        ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE {quoted};
                END
                """
            );
        }
    }
}
