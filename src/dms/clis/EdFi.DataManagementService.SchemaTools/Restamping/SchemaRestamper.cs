// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Runtime.ExceptionServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Restamping;

public sealed class SchemaRestamper : ISchemaRestamper
{
    private readonly ILogger _logger;
    private readonly Func<SqlDialect, string, DbConnection> _connectionFactory;

    public SchemaRestamper(ILogger logger)
        : this(logger, CreateConnection) { }

    public SchemaRestamper(ILogger logger, Func<SqlDialect, string, DbConnection> connectionFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<SchemaRestampResult> RestampAsync(
        SqlDialect dialect,
        string connectionString,
        int commandTimeoutSeconds,
        EffectiveSchemaInfo target,
        bool migrationCompleted,
        CancellationToken cancellationToken
    )
    {
        ValidateArguments(dialect, connectionString, commandTimeoutSeconds, target);

        DbConnection connection = null!;
        DbTransaction transaction = null!;
        var commitAttempted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            connection = _connectionFactory(dialect, connectionString);
            await connection.OpenAsync(cancellationToken);
            transaction = await connection.BeginTransactionAsync(cancellationToken);

            foreach (var lockSql in SchemaRestampSql.LockCommands(dialect))
            {
                await LockAsync(
                    connection,
                    transaction,
                    dialect,
                    lockSql,
                    commandTimeoutSeconds,
                    cancellationToken
                );
            }

            var snapshot = await ReadSnapshotAsync(
                connection,
                transaction,
                dialect,
                commandTimeoutSeconds,
                cancellationToken
            );
            SchemaRestampValidator.ValidateOrThrow(snapshot, target, _logger);
            var oldHash = snapshot.Fingerprints[0].EffectiveSchemaHash;
            var changed = !string.Equals(oldHash, target.EffectiveSchemaHash, StringComparison.Ordinal);
            if (changed && !migrationCompleted)
            {
                throw new SchemaRestampException(
                    SchemaRestampFailure.ConfirmationRequired,
                    "The migration-completed confirmation is required before re-stamping."
                );
            }

            if (changed)
            {
                var deleted = await ExecuteAsync(
                    connection,
                    transaction,
                    SchemaRestampSql.DeleteComponents(dialect),
                    commandTimeoutSeconds,
                    cancellationToken,
                    ("oldHash", oldHash)
                );
                if (deleted != snapshot.SchemaComponents.Count)
                {
                    throw InvalidState("dms.SchemaComponent changed during re-stamp.");
                }

                var updated = await ExecuteAsync(
                    connection,
                    transaction,
                    SchemaRestampSql.UpdateFingerprint(dialect),
                    commandTimeoutSeconds,
                    cancellationToken,
                    ("singletonId", (short)1),
                    ("oldHash", oldHash),
                    ("targetHash", target.EffectiveSchemaHash)
                );
                if (updated != 1)
                {
                    throw InvalidState("dms.EffectiveSchema changed during re-stamp.");
                }

                foreach (var payload in snapshot.SchemaComponents.Select(component => component.Payload))
                {
                    var inserted = await ExecuteAsync(
                        connection,
                        transaction,
                        SchemaRestampSql.InsertComponent(dialect),
                        commandTimeoutSeconds,
                        cancellationToken,
                        ("targetHash", target.EffectiveSchemaHash),
                        ("endpoint", payload.ProjectEndpointName),
                        ("projectName", payload.ProjectName),
                        ("projectVersion", payload.ProjectVersion),
                        ("isExtension", payload.IsExtensionProject)
                    );
                    if (inserted != 1)
                    {
                        throw InvalidState("dms.SchemaComponent insertion did not affect one row.");
                    }
                }
            }

            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
            return new SchemaRestampResult(
                changed,
                oldHash,
                target.EffectiveSchemaHash,
                snapshot.SchemaComponents.Count
            );
        }
        catch (Exception exception)
        {
            if (!commitAttempted && transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                    // The original failure remains the operator diagnostic.
                }
            }

            if (commitAttempted)
            {
                throw new SchemaRestampException(
                    SchemaRestampFailure.CommitOutcomeUnknown,
                    "The re-stamp commit outcome is unknown. Inspect the database and rerun with identical inputs while services remain offline."
                );
            }

            if (exception is SchemaRestampException restampException)
            {
                throw restampException;
            }

            throw Classify(exception, transaction is null, cancellationToken);
        }
        finally
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.DisposeAsync();
                }
                catch (Exception)
                {
                    // Disposal cannot replace the transaction result.
                }
            }
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync();
                }
                catch (Exception)
                {
                    // Disposal cannot replace the transaction result.
                }
            }
        }
    }

    private static void ValidateArguments(
        SqlDialect dialect,
        string connectionString,
        int commandTimeoutSeconds,
        EffectiveSchemaInfo target
    )
    {
        if (dialect is not (SqlDialect.Pgsql or SqlDialect.Mssql))
        {
            throw new SchemaRestampException(SchemaRestampFailure.Validation, "Unsupported SQL dialect.");
        }
        if (commandTimeoutSeconds <= 0)
        {
            throw new SchemaRestampException(
                SchemaRestampFailure.Validation,
                "Command timeout must be positive."
            );
        }
        if (target is null || string.IsNullOrWhiteSpace(connectionString))
        {
            throw new SchemaRestampException(
                SchemaRestampFailure.Validation,
                "Target schema and connection string are required."
            );
        }
        try
        {
            var namedDatabase = dialect switch
            {
                SqlDialect.Pgsql => new NpgsqlConnectionStringBuilder(connectionString) is var pg
                    && pg.ContainsKey("Database")
                    && !string.IsNullOrWhiteSpace(pg.Database),
                SqlDialect.Mssql => new SqlConnectionStringBuilder(connectionString) is var ms
                    && (ms.ContainsKey("Initial Catalog") || ms.ContainsKey("Database"))
                    && !string.IsNullOrWhiteSpace(ms.InitialCatalog),
                _ => false,
            };
            if (!namedDatabase)
            {
                throw new SchemaRestampException(
                    SchemaRestampFailure.Validation,
                    "An explicit target database name is required."
                );
            }
        }
        catch (ArgumentException)
        {
            throw new SchemaRestampException(
                SchemaRestampFailure.Validation,
                "The target connection string is invalid."
            );
        }
    }

    private static DbConnection CreateConnection(SqlDialect dialect, string connectionString) =>
        dialect == SqlDialect.Pgsql
            ? new NpgsqlConnection(connectionString)
            : new SqlConnection(connectionString);

    private static async Task LockAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlDialect dialect,
        string sql,
        int timeout,
        CancellationToken cancellationToken
    )
    {
        var command = CreateCommand(connection, transaction, sql, timeout);
        await WithReaderAsync(
            command,
            cancellationToken,
            async reader =>
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    _ = reader.GetValue(0);
                }
            },
            executeWithoutReader: dialect == SqlDialect.Pgsql
        );
    }

    private static async Task<SchemaRestampSnapshot> ReadSnapshotAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlDialect dialect,
        int timeout,
        CancellationToken cancellationToken
    )
    {
        List<SchemaRestampFingerprint> fingerprints = [];
        await WithReaderAsync(
            CreateCommand(connection, transaction, SchemaRestampSql.ReadFingerprint(dialect), timeout),
            cancellationToken,
            async reader =>
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    fingerprints.Add(
                        new SchemaRestampFingerprint(
                            Required<short>(reader, 0, "dms.EffectiveSchema"),
                            Required<string>(reader, 1, "dms.EffectiveSchema"),
                            Required<string>(reader, 2, "dms.EffectiveSchema"),
                            Required<short>(reader, 3, "dms.EffectiveSchema"),
                            Required<byte[]>(reader, 4, "dms.EffectiveSchema")
                        )
                    );
                }
            }
        );

        List<ResourceKeyRow> resourceKeys = [];
        await WithReaderAsync(
            CreateCommand(connection, transaction, SchemaRestampSql.ReadResourceKeys(dialect), timeout),
            cancellationToken,
            async reader =>
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    resourceKeys.Add(
                        new ResourceKeyRow(
                            Required<short>(reader, 0, "dms.ResourceKey"),
                            Required<string>(reader, 1, "dms.ResourceKey"),
                            Required<string>(reader, 2, "dms.ResourceKey"),
                            Required<string>(reader, 3, "dms.ResourceKey")
                        )
                    );
                }
            }
        );

        List<SchemaRestampComponent> components = [];
        await WithReaderAsync(
            CreateCommand(connection, transaction, SchemaRestampSql.ReadComponents(dialect), timeout),
            cancellationToken,
            async reader =>
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    components.Add(
                        new SchemaRestampComponent(
                            Required<string>(reader, 0, "dms.SchemaComponent"),
                            new SchemaComponentRow(
                                Required<string>(reader, 1, "dms.SchemaComponent"),
                                Required<string>(reader, 2, "dms.SchemaComponent"),
                                Required<string>(reader, 3, "dms.SchemaComponent"),
                                Required<bool>(reader, 4, "dms.SchemaComponent")
                            )
                        )
                    );
                }
            }
        );
        return new SchemaRestampSnapshot(fingerprints, resourceKeys, components);
    }

    private static T Required<T>(DbDataReader reader, int ordinal, string table)
    {
        if (reader.GetValue(ordinal) is T value)
        {
            return value;
        }
        throw InvalidState($"{table} contains a missing or invalid required field.");
    }

    private static async Task<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        int timeout,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters
    )
    {
        var command = CreateCommand(connection, transaction, sql, timeout);
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return await WithAsyncResource(
            command,
            currentCommand => currentCommand.ExecuteNonQueryAsync(cancellationToken)
        );
    }

    private static async Task WithReaderAsync(
        DbCommand command,
        CancellationToken cancellationToken,
        Func<DbDataReader, Task> read,
        bool executeWithoutReader = false
    )
    {
        await WithAsyncResource(
            command,
            async currentCommand =>
            {
                if (executeWithoutReader)
                {
                    await currentCommand.ExecuteNonQueryAsync(cancellationToken);
                    return true;
                }

                var reader = await currentCommand.ExecuteReaderAsync(cancellationToken);
                await WithAsyncResource(
                    reader,
                    async currentReader =>
                    {
                        await read(currentReader);
                        return true;
                    }
                );
                return true;
            }
        );
    }

    private static async Task<TResult> WithAsyncResource<TResource, TResult>(
        TResource resource,
        Func<TResource, Task<TResult>> operation
    )
        where TResource : IAsyncDisposable
    {
        TResult result;
        try
        {
            result = await operation(resource);
        }
        catch (Exception exception)
        {
            try
            {
                await resource.DisposeAsync();
            }
            catch
            {
                // Keep cleanup from replacing the operation's primary failure.
            }

            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }

        await resource.DisposeAsync();
        return result;
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        int timeout
    )
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = timeout;
        command.CommandText = sql;
        return command;
    }

    private static SchemaRestampException InvalidState(string message) =>
        new(SchemaRestampFailure.Validation, message);

    private static SchemaRestampException Classify(
        Exception exception,
        bool beforeTransaction,
        CancellationToken cancellationToken
    )
    {
        if (exception is OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            return new SchemaRestampException(
                SchemaRestampFailure.Cancelled,
                "The re-stamp was cancelled before commit."
            );
        }
        if (exception is PostgresException pg)
        {
            return pg.SqlState switch
            {
                "3D000" or "42P01" or "42703" => new(
                    SchemaRestampFailure.Validation,
                    "The target database is missing required re-stamp metadata."
                ),
                "42501" => new(
                    SchemaRestampFailure.PermissionDenied,
                    "The target database denied required re-stamp privileges."
                ),
                "57014" => new(SchemaRestampFailure.Timeout, "The re-stamp database command timed out."),
                _ => new(SchemaRestampFailure.TransactionFailed, "The re-stamp database transaction failed."),
            };
        }
        if (exception is NpgsqlException && HasInnerTimeoutException(exception))
        {
            return new SchemaRestampException(
                SchemaRestampFailure.Timeout,
                "The re-stamp database command timed out."
            );
        }
        if (exception is SqlException ms)
        {
            return ms.Number switch
            {
                4060 or 911 or 208 or 207 => new(
                    SchemaRestampFailure.Validation,
                    "The target database is missing required re-stamp metadata."
                ),
                229 or 262 or 297 => new(
                    SchemaRestampFailure.PermissionDenied,
                    "The target database denied required re-stamp privileges."
                ),
                -2 or 1222 => new(SchemaRestampFailure.Timeout, "The re-stamp database command timed out."),
                _ => new(SchemaRestampFailure.TransactionFailed, "The re-stamp database transaction failed."),
            };
        }
        if (exception is TimeoutException)
        {
            return new SchemaRestampException(
                SchemaRestampFailure.Timeout,
                "The re-stamp database command timed out."
            );
        }
        if (beforeTransaction)
        {
            return new SchemaRestampException(
                SchemaRestampFailure.Connection,
                "Could not connect to the target database."
            );
        }
        return new SchemaRestampException(
            SchemaRestampFailure.TransactionFailed,
            "The re-stamp database transaction failed."
        );
    }

    private static bool HasInnerTimeoutException(Exception exception)
    {
        for (
            Exception? current = exception.InnerException;
            current is not null;
            current = current.InnerException
        )
        {
            if (current is TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}
