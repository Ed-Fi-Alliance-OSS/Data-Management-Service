// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// What the education-organization projection API scenarios need to know about the engine under
/// test: its catalog provider token, the statements that damage a test database in a chosen way,
/// and the session probes that observe a blocked or abandoned read.
/// </summary>
internal sealed class EducationOrganizationProjectionEngine
{
    private readonly Func<string, DbConnection> _connect;
    private readonly Func<string, string, string> _withApplicationName;
    private readonly Func<string, string> _withUnresolvableHost;
    private readonly Func<DbConnection, Func<DbConnection>, Task<IExclusiveSchoolLock>> _lockSchools;

    private EducationOrganizationProjectionEngine(
        string name,
        RelationalProviderToken ownToken,
        RelationalProviderToken otherToken,
        Func<string, DbConnection> connect,
        Func<string, string, string> withApplicationName,
        Func<string, string> withUnresolvableHost,
        Func<DbConnection, Func<DbConnection>, Task<IExclusiveSchoolLock>> lockSchools,
        string dropSchoolShortNameSql,
        string droppedColumnProviderCode,
        string mismatchFingerprintSql,
        string malformFingerprintSql,
        string removeFingerprintSql,
        string quote
    )
    {
        Name = name;
        OwnToken = ownToken;
        OtherToken = otherToken;
        _connect = connect;
        _withApplicationName = withApplicationName;
        _withUnresolvableHost = withUnresolvableHost;
        _lockSchools = lockSchools;
        DropSchoolShortNameSql = dropSchoolShortNameSql;
        DroppedColumnProviderCode = droppedColumnProviderCode;
        MismatchFingerprintSql = mismatchFingerprintSql;
        MalformFingerprintSql = malformFingerprintSql;
        RemoveFingerprintSql = removeFingerprintSql;
        Quote = quote;
    }

    /// <summary>A hash of the right shape that no deployment computes; it must never be echoed.</summary>
    public const string HostileHash = "0badc0de0badc0de0badc0de0badc0de0badc0de0badc0de0badc0de0badc0de";

    public string Name { get; }

    /// <summary>The catalog token this deployment's dialect serves.</summary>
    public RelationalProviderToken OwnToken { get; }

    /// <summary>The catalog token of the other dialect.</summary>
    public RelationalProviderToken OtherToken { get; }

    public string DropSchoolShortNameSql { get; }

    /// <summary>The provider's code for the dropped column, which the read logs as its permanent reason.</summary>
    public string DroppedColumnProviderCode { get; }

    public string MismatchFingerprintSql { get; }

    public string MalformFingerprintSql { get; }

    public string RemoveFingerprintSql { get; }

    /// <summary>Quotes an identifier.</summary>
    private string Quote { get; }

    public string Table(string schema, string table) => $"{Q(schema)}.{Q(table)}";

    public string Q(string identifier) => Quote == "\"" ? $"\"{identifier}\"" : $"[{identifier}]";

    /// <summary>The same database under a different connection string, so a fingerprint verdict is cached apart.</summary>
    public string WithApplicationName(string connectionString, string applicationName) =>
        _withApplicationName(connectionString, applicationName);

    /// <summary>The same connection string naming a host that cannot be resolved.</summary>
    public string WithUnresolvableHost(string connectionString) => _withUnresolvableHost(connectionString);

    public async Task ExecuteAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using DbConnection connection = _connect(connectionString);
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Opens a transaction holding an exclusive lock on the School table until disposed, so a
    /// projection read waits on it.
    /// </summary>
    public async Task<IExclusiveSchoolLock> LockSchoolsAsync(string connectionString)
    {
        DbConnection connection = _connect(connectionString);
        await connection.OpenAsync();
        // Probes open their own sessions from the original text: an opened SqlConnection no longer
        // reports its password.
        return await _lockSchools(connection, () => _connect(connectionString));
    }

    public static EducationOrganizationProjectionEngine Postgresql { get; } =
        new(
            "postgresql",
            RelationalProviderToken.Postgresql,
            RelationalProviderToken.SqlServer,
            static connectionString => new NpgsqlConnection(connectionString),
            static (connectionString, applicationName) =>
                new NpgsqlConnectionStringBuilder(connectionString)
                {
                    ApplicationName = applicationName,
                }.ConnectionString,
            static connectionString =>
                new NpgsqlConnectionStringBuilder(connectionString)
                {
                    Host = $"projection-unresolvable-{Guid.NewGuid():N}.invalid",
                    Timeout = 5,
                }.ConnectionString,
            static async (connection, connectProbe) =>
            {
                DbTransaction transaction = await connection.BeginTransactionAsync();
                await using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """LOCK TABLE "edfi"."School" IN ACCESS EXCLUSIVE MODE;""";
                    await command.ExecuteNonQueryAsync();
                }

                return new SchoolLock(
                    connection,
                    transaction,
                    connectProbe,
                    blockedReadsSql: """
                    SELECT count(*)::int FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock'
                      AND query LIKE '%StateEducationAgencyReference%';
                    """,
                    openTransactionsSql: """
                    SELECT count(*)::int FROM pg_stat_activity
                    WHERE datname = current_database() AND xact_start IS NOT NULL
                      AND pid <> pg_backend_pid() AND pid <> @blocker;
                    """,
                    blockerIdSql: "SELECT pg_backend_pid();"
                );
            },
            dropSchoolShortNameSql: """ALTER TABLE "edfi"."School" DROP COLUMN "ShortNameOfInstitution" CASCADE;""",
            droppedColumnProviderCode: "42703",
            mismatchFingerprintSql: $"""
            DELETE FROM "dms"."SchemaComponent";
            UPDATE "dms"."EffectiveSchema" SET "EffectiveSchemaHash" = '{HostileHash}';
            """,
            malformFingerprintSql: """
            UPDATE "dms"."EffectiveSchema" SET "EffectiveSchemaHash" = 'NOT-A-LOWERCASE-HEX-HASH';
            """,
            removeFingerprintSql: """DELETE FROM "dms"."EffectiveSchema";""",
            quote: "\""
        );

    public static EducationOrganizationProjectionEngine Mssql { get; } =
        new(
            "mssql",
            RelationalProviderToken.SqlServer,
            RelationalProviderToken.Postgresql,
            static connectionString => new SqlConnection(connectionString),
            static (connectionString, applicationName) =>
                new SqlConnectionStringBuilder(connectionString)
                {
                    ApplicationName = applicationName,
                }.ConnectionString,
            static connectionString =>
                new SqlConnectionStringBuilder(connectionString)
                {
                    DataSource = $"projection-unresolvable-{Guid.NewGuid():N}.invalid",
                    ConnectTimeout = 5,
                    ConnectRetryCount = 0,
                }.ConnectionString,
            static async (connection, connectProbe) =>
            {
                DbTransaction transaction = await connection.BeginTransactionAsync();
                await using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "SELECT COUNT(*) FROM [edfi].[School] WITH (TABLOCKX, HOLDLOCK);";
                    await command.ExecuteScalarAsync();
                }

                return new SchoolLock(
                    connection,
                    transaction,
                    connectProbe,
                    blockedReadsSql: "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @blocker;",
                    openTransactionsSql: """
                    SELECT COUNT(*) FROM sys.dm_tran_session_transactions st
                    JOIN sys.dm_exec_sessions s ON s.session_id = st.session_id
                    WHERE s.database_id = DB_ID() AND st.session_id NOT IN (@blocker, @@SPID);
                    """,
                    blockerIdSql: "SELECT @@SPID;"
                );
            },
            dropSchoolShortNameSql: "ALTER TABLE [edfi].[School] DROP COLUMN [ShortNameOfInstitution];",
            droppedColumnProviderCode: "207",
            mismatchFingerprintSql: $"""
            DELETE FROM [dms].[SchemaComponent];
            UPDATE [dms].[EffectiveSchema] SET [EffectiveSchemaHash] = '{HostileHash}';
            """,
            malformFingerprintSql: """
            UPDATE [dms].[EffectiveSchema] SET [EffectiveSchemaHash] = 'NOT-A-LOWERCASE-HEX-HASH';
            """,
            removeFingerprintSql: "DELETE FROM [dms].[EffectiveSchema];",
            quote: "["
        );

    /// <summary>
    /// The lock-holding transaction and probes run from a second connection: projection reads waiting
    /// on the lock, and transactions other than the lock's still open in the database.
    /// </summary>
    private sealed class SchoolLock(
        DbConnection connection,
        DbTransaction transaction,
        Func<DbConnection> connectProbe,
        string blockedReadsSql,
        string openTransactionsSql,
        string blockerIdSql
    ) : IExclusiveSchoolLock
    {
        private int? _blocker;

        public Task<int> BlockedReadsAsync() => ProbeAsync(blockedReadsSql);

        public Task<int> OpenTransactionsAsync() => ProbeAsync(openTransactionsSql);

        private async Task<int> ProbeAsync(string sql)
        {
            int blocker = _blocker ??= await BlockerAsync();
            await using DbConnection probe = connectProbe();
            await probe.OpenAsync();
            await using DbCommand command = probe.CreateCommand();
            command.CommandText = sql;
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "blocker";
            parameter.Value = blocker;
            command.Parameters.Add(parameter);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private async Task<int> BlockerAsync()
        {
            await using DbCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = blockerIdSql;
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

/// <summary>An open transaction holding an exclusive lock on the School table, released on dispose.</summary>
internal interface IExclusiveSchoolLock : IAsyncDisposable
{
    /// <summary>Projection reads currently waiting on the lock.</summary>
    Task<int> BlockedReadsAsync();

    /// <summary>Transactions open in the database other than the lock's own.</summary>
    Task<int> OpenTransactionsAsync();
}
