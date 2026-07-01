// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

public sealed record MssqlForeignKeyMetadata(
    string ConstraintName,
    string[] Columns,
    string ReferencedSchema,
    string ReferencedTable,
    string[] ReferencedColumns,
    string DeleteAction,
    string UpdateAction
);

internal sealed record MssqlProvisioningTimingContext(
    string FixtureSignature,
    string GeneratedDdlHash,
    string LeaseStrategy,
    string CallerMemberName,
    string CallerFilePath,
    int CallerLineNumber
);

public sealed partial class MssqlGeneratedDdlTestDatabase : IAsyncDisposable
{
    private const int DefaultCommandTimeoutSeconds = 300;
    private const string ProvisionMaxConcurrencyVariable = "MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY";
    private static readonly (string Schema, string Table)[] _generatedDdlBaselineTables =
    [
        ("dms", "EffectiveSchema"),
        ("dms", "ResourceKey"),
        ("dms", "SchemaComponent"),
    ];
    private static readonly MssqlDatabaseResetPlan _dynamicResetPlan = MssqlDatabaseResetPlan.Dynamic(
        _generatedDdlBaselineTables
    );
    private static readonly Lazy<SemaphoreSlim?> _generatedDdlProvisionSemaphore = new(
        CreateGeneratedDdlProvisionSemaphore
    );

    private MssqlGeneratedDdlTestDatabase(
        string databaseName,
        string connectionString,
        string fixtureSignature = "",
        string generatedDdlHash = "",
        string leaseStrategy = MssqlProvisioningTimingRecorder.DirectLeaseStrategy,
        MssqlDatabaseResetPlan? resetPlan = null
    )
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        FixtureSignature = fixtureSignature;
        GeneratedDdlHash = generatedDdlHash;
        LeaseStrategy = leaseStrategy;
        ResetPlan = resetPlan ?? _dynamicResetPlan;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    private string FixtureSignature { get; }

    private string GeneratedDdlHash { get; }

    private string LeaseStrategy { get; }

    internal MssqlDatabaseResetPlan ResetPlan { get; private set; }

    public static Task<MssqlGeneratedDdlTestDatabase> CreateEmptyAsync(
        string fixtureSignature = "",
        string generatedDdlHash = "",
        string leaseStrategy = MssqlProvisioningTimingRecorder.DirectLeaseStrategy,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0
    )
    {
        var context = new MssqlProvisioningTimingContext(
            fixtureSignature,
            generatedDdlHash,
            leaseStrategy,
            callerMemberName,
            callerFilePath,
            callerLineNumber
        );

        return CreateEmptyAsync(context);
    }

    internal static MssqlGeneratedDdlTestDatabase AttachExisting(
        string databaseName,
        string fixtureSignature = "",
        string generatedDdlHash = "",
        string leaseStrategy = MssqlProvisioningTimingRecorder.DirectLeaseStrategy,
        MssqlDatabaseResetPlan? resetPlan = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            throw new InvalidOperationException(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        return new(
            databaseName,
            MssqlTestDatabaseHelper.BuildConnectionString(databaseName),
            fixtureSignature,
            generatedDdlHash,
            leaseStrategy,
            resetPlan
        );
    }

    private static Task<MssqlGeneratedDdlTestDatabase> CreateEmptyAsync(
        MssqlProvisioningTimingContext context
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var databaseName = "";
        var outcome = "Succeeded";

        try
        {
            if (!MssqlTestDatabaseHelper.IsConfigured())
            {
                throw new InvalidOperationException(
                    "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
                );
            }

            databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
            var connectionString = MssqlTestDatabaseHelper.BuildConnectionString(databaseName);

            MssqlTestDatabaseHelper.CreateGeneratedDdlDatabase(
                databaseName,
                useExplicitFileSizing: IsDirectLeaseStrategy(context.LeaseStrategy)
            );

            return Task.FromResult(
                new MssqlGeneratedDdlTestDatabase(
                    databaseName,
                    connectionString,
                    context.FixtureSignature,
                    context.GeneratedDdlHash,
                    context.LeaseStrategy
                )
            );
        }
        catch
        {
            outcome = "Failed";
            if (!string.IsNullOrWhiteSpace(databaseName))
            {
                MssqlTestDatabaseHelper.DropDatabaseIfExists(databaseName);
            }

            throw;
        }
        finally
        {
            stopwatch.Stop();
            MssqlProvisioningTimingRecorder.Record(
                outcome,
                stopwatch.Elapsed,
                databaseName,
                DefaultCommandTimeoutSeconds,
                context.FixtureSignature,
                context.GeneratedDdlHash,
                "create-empty-database",
                context.LeaseStrategy,
                context.CallerMemberName,
                context.CallerFilePath,
                context.CallerLineNumber
            );
        }
    }

    public static async Task<MssqlGeneratedDdlTestDatabase> CreateProvisionedAsync(
        string generatedDdl,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds,
        string fixtureSignature = "",
        string generatedDdlHash = "",
        string leaseStrategy = MssqlProvisioningTimingRecorder.DirectLeaseStrategy,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDdl);

        var resolvedGeneratedDdlHash = string.IsNullOrWhiteSpace(generatedDdlHash)
            ? MssqlProvisioningTimingRecorder.ComputeGeneratedDdlHash(generatedDdl)
            : generatedDdlHash;
        var context = new MssqlProvisioningTimingContext(
            fixtureSignature,
            resolvedGeneratedDdlHash,
            leaseStrategy,
            callerMemberName,
            callerFilePath,
            callerLineNumber
        );

        return await CreateProvisionedAsync(generatedDdl, commandTimeoutSeconds, context);
    }

    internal static async Task<MssqlGeneratedDdlTestDatabase> CreateProvisionedAsync(
        string generatedDdl,
        int commandTimeoutSeconds,
        MssqlProvisioningTimingContext context
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDdl);

        var stopwatch = Stopwatch.StartNew();
        MssqlGeneratedDdlTestDatabase? database = null;
        var databaseName = "";
        var outcome = "Succeeded";

        try
        {
            await using var provisionSlot = await AcquireGeneratedDdlProvisionSlotAsync();

            database = await CreateEmptyAsync(context);
            databaseName = database.DatabaseName;
            await database.ApplyGeneratedDdlAsync(generatedDdl, commandTimeoutSeconds, context);
            return database;
        }
        catch
        {
            outcome = "Failed";
            if (database is not null)
            {
                await database.DisposeAsync();
            }

            throw;
        }
        finally
        {
            stopwatch.Stop();
            MssqlProvisioningTimingRecorder.Record(
                outcome,
                stopwatch.Elapsed,
                databaseName,
                commandTimeoutSeconds,
                context.FixtureSignature,
                context.GeneratedDdlHash,
                "create-provisioned",
                context.LeaseStrategy,
                context.CallerMemberName,
                context.CallerFilePath,
                context.CallerLineNumber
            );
        }
    }

    public async Task ApplyGeneratedDdlAsync(
        string generatedDdl,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds,
        string fixtureSignature = "",
        string generatedDdlHash = "",
        string leaseStrategy = "",
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDdl);

        var resolvedFixtureSignature = string.IsNullOrWhiteSpace(fixtureSignature)
            ? FixtureSignature
            : fixtureSignature;
        var resolvedGeneratedDdlHash = string.IsNullOrWhiteSpace(generatedDdlHash)
            ? MssqlProvisioningTimingRecorder.ComputeGeneratedDdlHash(generatedDdl)
            : generatedDdlHash;
        var resolvedLeaseStrategy = string.IsNullOrWhiteSpace(leaseStrategy) ? LeaseStrategy : leaseStrategy;
        var context = new MssqlProvisioningTimingContext(
            resolvedFixtureSignature,
            resolvedGeneratedDdlHash,
            resolvedLeaseStrategy,
            callerMemberName,
            callerFilePath,
            callerLineNumber
        );

        await ApplyGeneratedDdlAsync(generatedDdl, commandTimeoutSeconds, context);
    }

    private async Task ApplyGeneratedDdlAsync(
        string generatedDdl,
        int commandTimeoutSeconds,
        MssqlProvisioningTimingContext context
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = "Succeeded";

        try
        {
            await using SqlConnection connection = new(ConnectionString);
            await connection.OpenAsync();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            var batches = SplitOnGoBatchSeparator(generatedDdl).ToArray();

            try
            {
                for (var batchIndex = 0; batchIndex < batches.Length; batchIndex++)
                {
                    await ExecuteGeneratedDdlBatchAsync(
                        connection,
                        transaction,
                        batches[batchIndex],
                        batchIndex + 1,
                        batches.Length,
                        commandTimeoutSeconds,
                        context
                    );
                }

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            await RefreshResetPlanAsync(commandTimeoutSeconds);
        }
        catch
        {
            outcome = "Failed";
            throw;
        }
        finally
        {
            stopwatch.Stop();
            MssqlProvisioningTimingRecorder.Record(
                outcome,
                stopwatch.Elapsed,
                DatabaseName,
                commandTimeoutSeconds,
                context.FixtureSignature,
                context.GeneratedDdlHash,
                "apply-generated-ddl",
                context.LeaseStrategy,
                context.CallerMemberName,
                context.CallerFilePath,
                context.CallerLineNumber
            );
        }
    }

    public async Task ResetAsync(
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = "Succeeded";

        try
        {
            await using SqlConnection connection = new(ConnectionString);
            await connection.OpenAsync();
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = ResetPlan.Sql;
            command.CommandTimeout = commandTimeoutSeconds;
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            outcome = "Failed";
            throw;
        }
        finally
        {
            stopwatch.Stop();
            MssqlProvisioningTimingRecorder.Record(
                outcome,
                stopwatch.Elapsed,
                DatabaseName,
                commandTimeoutSeconds,
                FixtureSignature,
                GeneratedDdlHash,
                "reset-database",
                LeaseStrategy,
                callerMemberName,
                callerFilePath,
                callerLineNumber
            );
        }
    }

    internal async Task RefreshResetPlanAsync(int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        ResetPlan = await MssqlDatabaseResetSql.BuildPrecomputedAsync(
            ConnectionString,
            commandTimeoutSeconds,
            _generatedDdlBaselineTables
        );
    }

    public async Task<bool> SequenceExistsAsync(string schema, string sequenceName)
    {
        const string sql = """
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM sys.sequences sequences
                    INNER JOIN sys.schemas schemas
                        ON schemas.schema_id = sequences.schema_id
                    WHERE schemas.name = @schema
                      AND sequences.name = @sequenceName
                )
                THEN CAST(1 AS bit)
                ELSE CAST(0 AS bit)
            END;
            """;

        return await ExecuteScalarAsync<bool>(
            sql,
            new SqlParameter("@schema", schema),
            new SqlParameter("@sequenceName", sequenceName)
        );
    }

    public async Task<string?> GetColumnDefaultAsync(string schema, string tableName, string columnName)
    {
        const string sql = """
            SELECT default_constraints.definition
            FROM sys.default_constraints default_constraints
            INNER JOIN sys.columns columns
                ON columns.default_object_id = default_constraints.object_id
            INNER JOIN sys.tables tables
                ON tables.object_id = columns.object_id
            INNER JOIN sys.schemas schemas
                ON schemas.schema_id = tables.schema_id
            WHERE schemas.name = @schema
              AND tables.name = @tableName
              AND columns.name = @columnName;
            """;

        return await ExecuteScalarOrDefaultAsync<string>(
            sql,
            new SqlParameter("@schema", schema),
            new SqlParameter("@tableName", tableName),
            new SqlParameter("@columnName", columnName)
        );
    }

    public async Task<IReadOnlyList<MssqlForeignKeyMetadata>> GetForeignKeyMetadataAsync(
        string schema,
        string tableName
    )
    {
        const string sql = """
            SELECT
                foreign_keys.name AS ConstraintName,
                foreign_key_columns.constraint_column_id AS ColumnOrdinal,
                source_columns.name AS ColumnName,
                referenced_schemas.name AS ReferencedSchema,
                referenced_tables.name AS ReferencedTable,
                referenced_columns.name AS ReferencedColumnName,
                foreign_keys.delete_referential_action_desc AS DeleteAction,
                foreign_keys.update_referential_action_desc AS UpdateAction
            FROM sys.foreign_keys foreign_keys
            INNER JOIN sys.foreign_key_columns foreign_key_columns
                ON foreign_key_columns.constraint_object_id = foreign_keys.object_id
            INNER JOIN sys.tables tables
                ON tables.object_id = foreign_keys.parent_object_id
            INNER JOIN sys.schemas schemas
                ON schemas.schema_id = tables.schema_id
            INNER JOIN sys.columns source_columns
                ON source_columns.object_id = tables.object_id
               AND source_columns.column_id = foreign_key_columns.parent_column_id
            INNER JOIN sys.tables referenced_tables
                ON referenced_tables.object_id = foreign_keys.referenced_object_id
            INNER JOIN sys.schemas referenced_schemas
                ON referenced_schemas.schema_id = referenced_tables.schema_id
            INNER JOIN sys.columns referenced_columns
                ON referenced_columns.object_id = referenced_tables.object_id
               AND referenced_columns.column_id = foreign_key_columns.referenced_column_id
            WHERE schemas.name = @schema
              AND tables.name = @tableName
            ORDER BY foreign_keys.name, foreign_key_columns.constraint_column_id;
            """;

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange([
            new SqlParameter("@schema", schema),
            new SqlParameter("@tableName", tableName),
        ]);

        await using var reader = await command.ExecuteReaderAsync();
        Dictionary<string, ForeignKeyMetadataBuilder> builders = new(StringComparer.Ordinal);
        List<string> orderedConstraintNames = [];

        while (await reader.ReadAsync())
        {
            var constraintName = reader.GetString(0);

            if (!builders.TryGetValue(constraintName, out var builder))
            {
                builder = new ForeignKeyMetadataBuilder(
                    constraintName,
                    reader.GetString(3),
                    reader.GetString(4),
                    NormalizeReferentialAction(reader.GetString(6)),
                    NormalizeReferentialAction(reader.GetString(7))
                );
                builders[constraintName] = builder;
                orderedConstraintNames.Add(constraintName);
            }

            builder.Columns.Add(reader.GetString(2));
            builder.ReferencedColumns.Add(reader.GetString(5));
        }

        return orderedConstraintNames.Select(constraintName => builders[constraintName].Build()).ToArray();
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryRowsAsync(
        string sql,
        params SqlParameter[] parameters
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = DefaultCommandTimeoutSeconds;
        command.Parameters.AddRange(parameters);

        List<IReadOnlyDictionary<string, object?>> rows = [];
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            Dictionary<string, object?> row = new(StringComparer.Ordinal);

            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                row[reader.GetName(ordinal)] = await reader.IsDBNullAsync(ordinal)
                    ? null
                    : reader.GetValue(ordinal);
            }

            rows.Add(row);
        }

        return rows;
    }

    public async Task<int> ExecuteNonQueryAsync(string sql, params SqlParameter[] parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        // dms.Document is referenced by ON DELETE CASCADE FKs from every resource root
        // table, so even a single-row delete compiles a plan spanning the full cascade
        // graph — on cold CI runners that can exceed the 30s driver default.
        command.CommandTimeout = DefaultCommandTimeoutSeconds;
        command.Parameters.AddRange(parameters);

        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ExecuteScalarAsync<T>(string sql, params SqlParameter[] parameters)
    {
        var result = await ExecuteScalarOrDefaultAsync<T>(sql, parameters);
        return result is not null
            ? result
            : throw new InvalidOperationException(
                $"Expected scalar result for SQL but received null.\n{sql}"
            );
    }

    public async Task<T?> ExecuteScalarOrDefaultAsync<T>(string sql, params SqlParameter[] parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = DefaultCommandTimeoutSeconds;
        command.Parameters.AddRange(parameters);

        var result = await command.ExecuteScalarAsync();

        if (result is null || result is DBNull)
        {
            return default;
        }

        if (result is T typedResult)
        {
            return typedResult;
        }

        return (T?)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = "Succeeded";

        try
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(DatabaseName);
        }
        catch
        {
            outcome = "Failed";
            throw;
        }
        finally
        {
            stopwatch.Stop();
            MssqlProvisioningTimingRecorder.Record(
                outcome,
                stopwatch.Elapsed,
                DatabaseName,
                DefaultCommandTimeoutSeconds,
                FixtureSignature,
                GeneratedDdlHash,
                "drop-database",
                LeaseStrategy,
                nameof(DisposeAsync),
                "",
                0
            );
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<string> SplitOnGoBatchSeparator(string sql) =>
        GoBatchSeparatorPattern().Split(sql).Select(batch => batch.Trim()).Where(batch => batch.Length > 0);

    private async Task ExecuteGeneratedDdlBatchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string batch,
        int batchOrdinal,
        int batchCount,
        int commandTimeoutSeconds,
        MssqlProvisioningTimingContext context
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = "Succeeded";

        try
        {
            await using SqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = batch;
            command.CommandTimeout = commandTimeoutSeconds;
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            outcome = "Failed";
            throw;
        }
        finally
        {
            stopwatch.Stop();
            MssqlProvisioningTimingRecorder.Record(
                outcome,
                stopwatch.Elapsed,
                DatabaseName,
                commandTimeoutSeconds,
                context.FixtureSignature,
                context.GeneratedDdlHash,
                "apply-generated-ddl-batch",
                context.LeaseStrategy,
                context.CallerMemberName,
                context.CallerFilePath,
                context.CallerLineNumber,
                isDiagnostic: true,
                detail: "generated-ddl-batch",
                batchOrdinal: batchOrdinal,
                batchCount: batchCount,
                batchHash: MssqlProvisioningTimingRecorder.ComputeGeneratedDdlHash(batch)
            );
        }
    }

    private static bool IsDirectLeaseStrategy(string leaseStrategy) =>
        leaseStrategy.Equals(MssqlProvisioningTimingRecorder.DirectLeaseStrategy, StringComparison.Ordinal);

    private static async ValueTask<GeneratedDdlProvisionSemaphoreLease> AcquireGeneratedDdlProvisionSlotAsync()
    {
        var semaphore = _generatedDdlProvisionSemaphore.Value;
        if (semaphore is null)
        {
            return new(null);
        }

        await semaphore.WaitAsync();
        return new(semaphore);
    }

    private static SemaphoreSlim? CreateGeneratedDdlProvisionSemaphore()
    {
        var value = Environment.GetEnvironmentVariable(ProvisionMaxConcurrencyVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var maxConcurrency)
            || maxConcurrency < 1
        )
        {
            throw new InvalidOperationException(
                $"{ProvisionMaxConcurrencyVariable} must be a positive integer when set."
            );
        }

        return new(maxConcurrency, maxConcurrency);
    }

    private static string NormalizeReferentialAction(string value)
    {
        return value.Replace('_', ' ');
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoBatchSeparatorPattern();

    private sealed record ForeignKeyMetadataBuilder(
        string ConstraintName,
        string ReferencedSchema,
        string ReferencedTable,
        string DeleteAction,
        string UpdateAction
    )
    {
        public List<string> Columns { get; } = [];

        public List<string> ReferencedColumns { get; } = [];

        public MssqlForeignKeyMetadata Build()
        {
            return new(
                ConstraintName,
                [.. Columns],
                ReferencedSchema,
                ReferencedTable,
                [.. ReferencedColumns],
                DeleteAction,
                UpdateAction
            );
        }
    }

    private readonly struct GeneratedDdlProvisionSemaphoreLease(SemaphoreSlim? semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore?.Release();
            return ValueTask.CompletedTask;
        }
    }
}

internal static class MssqlProvisioningTimingRecorder
{
    public const string DirectLeaseStrategy = "direct";

    private const string TimingsPathVariable = "MSSQL_FIXTURE_TIMINGS_PATH";
    private const string ShardVariable = "MSSQL_TEST_SHARD";
    private static readonly object _lock = new();

    public static string ComputeGeneratedDdlHash(string generatedDdl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDdl);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generatedDdl)));
    }

    public static void Record(
        string outcome,
        TimeSpan duration,
        string databaseName,
        int commandTimeoutSeconds,
        string fixtureSignature,
        string generatedDdlHash,
        string phase,
        string leaseStrategy,
        string callerMemberName,
        string callerFilePath,
        int callerLineNumber,
        bool isDiagnostic = false,
        string detail = "",
        int? batchOrdinal = null,
        int? batchCount = null,
        string batchHash = ""
    )
    {
        var timingsPath = Environment.GetEnvironmentVariable(TimingsPathVariable);
        if (string.IsNullOrWhiteSpace(timingsPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(timingsPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string[] fields =
        [
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            outcome,
            duration.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture),
            databaseName,
            commandTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            fixtureSignature,
            generatedDdlHash,
            phase,
            leaseStrategy,
            ResolveShard(timingsPath),
            ResolveTestWorkerId(),
            callerMemberName,
            callerFilePath,
            callerLineNumber.ToString(CultureInfo.InvariantCulture),
            isDiagnostic ? "true" : "false",
            detail,
            batchOrdinal?.ToString(CultureInfo.InvariantCulture) ?? "",
            batchCount?.ToString(CultureInfo.InvariantCulture) ?? "",
            batchHash,
        ];

        lock (_lock)
        {
            var writeHeader = !File.Exists(fullPath);
            using var writer = new StreamWriter(fullPath, append: true, Encoding.UTF8);
            if (writeHeader)
            {
                writer.WriteLine(
                    "TimestampUtc,Outcome,DurationSeconds,DatabaseName,CommandTimeoutSeconds,FixtureSignature,GeneratedDdlHash,Phase,LeaseStrategy,Shard,TestWorkerId,CallerMemberName,CallerFilePath,CallerLineNumber,IsDiagnostic,Detail,BatchOrdinal,BatchCount,BatchHash"
                );
            }

            writer.WriteLine(string.Join(",", fields.Select(EscapeCsv)));
        }
    }

    private static string ResolveShard(string timingsPath)
    {
        var shard = Environment.GetEnvironmentVariable(ShardVariable);
        if (!string.IsNullOrWhiteSpace(shard))
        {
            return shard;
        }

        var match = Regex.Match(timingsPath, @"mssql-shard-(?<shard>[^\\/]+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups["shard"].Value;
        }

        return timingsPath.Contains("mssql-api", StringComparison.OrdinalIgnoreCase) ? "api" : "";
    }

    private static string ResolveTestWorkerId()
    {
        foreach (var variableName in new[] { "NUNIT_WORKER_ID", "TEST_WORKER_ID" })
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        try
        {
            var testContextType = Type.GetType("NUnit.Framework.TestContext, NUnit.Framework");
            var currentContextProperty = testContextType?.GetProperty(
                "CurrentContext",
                BindingFlags.Public | BindingFlags.Static
            );
            var currentContext = currentContextProperty?.GetValue(null);
            var workerIdProperty = currentContext
                ?.GetType()
                .GetProperty("WorkerId", BindingFlags.Public | BindingFlags.Instance);

            return workerIdProperty?.GetValue(currentContext)?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
