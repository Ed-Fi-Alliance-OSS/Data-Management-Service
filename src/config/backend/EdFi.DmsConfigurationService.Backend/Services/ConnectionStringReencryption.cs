// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using static EdFi.DmsConfigurationService.Backend.Services.ConnectionStringCipherText;

namespace EdFi.DmsConfigurationService.Backend.Services;

public enum ConnectionStringReencryptionEngine
{
    Postgresql,
    Mssql,
}

/// <summary>
/// Non-secret counts for one table: legacy values rewritten as envelopes (for a check, legacy values
/// the pass would rewrite), and envelopes that authenticated and were left as they are.
/// </summary>
public sealed record ConnectionStringReencryptionCounts(string Table, int Converted, int AlreadyCurrent);

/// <summary>
/// A value the pass could not migrate. The message names the table and row and carries no key,
/// cipher text, plain text, or exception text.
/// </summary>
public sealed class ConnectionStringReencryptionException(string message) : Exception(message);

/// <summary>
/// Rewrites every legacy AES-CBC connection string in DataStore and DataStoreDerivative, across all
/// tenants, as an AES-GCM envelope, and authenticates every envelope already there. Encryption is
/// <see cref="ConnectionStringEncryptionService"/>'s; legacy values are read whatever
/// <see cref="DatabaseOptions.AllowLegacyConnectionStrings"/> is, and decoded strictly as UTF-8.
///
/// An offline maintenance operation: every Configuration Service writer must be stopped. It runs in
/// one transaction across both tables, so any failure, or a cancellation before the final commit
/// begins, leaves the database as it was.
/// Rows are read in batches of bounded size, in Id order, and locked as they are read, so no other
/// writer can change a row between its read and the commit. Each rewrite still compares the
/// original bytes, as a guard rather than a detector. A rerun rewrites nothing.
///
/// <see cref="CheckAsync"/> is the read-only form: it verifies every value the same way and counts the
/// legacy ones, but takes no update locks and writes nothing, so it can run while the Configuration
/// Service is running.
///
/// A legacy value carries no integrity check, so the pass cannot tell whether one was altered before
/// it ran: it migrates what it reads.
/// </summary>
public sealed class ConnectionStringReencryption
{
    private static readonly string[] _tables = ["DataStore", "DataStoreDerivative"];

    private readonly ConnectionStringReencryptionEngine _engine;
    private readonly int _batchSize;
    private readonly ConnectionStringEncryptionService _encryptionService;

    /// <summary>
    /// Applies the Configuration Service's own key rules before anything is read.
    /// </summary>
    public ConnectionStringReencryption(
        DatabaseOptions databaseOptions,
        ConnectionStringReencryptionEngine engine,
        int batchSize = 500
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        ValidateOptionsResult validation = new DatabaseOptionsValidator().Validate(
            Options.DefaultName,
            databaseOptions
        );
        if (validation.Failed)
        {
            throw new ConnectionStringReencryptionException(validation.FailureMessage);
        }

        _engine = engine;
        _batchSize = batchSize;
        _encryptionService = new ConnectionStringEncryptionService(Options.Create(databaseOptions));
    }

    /// <summary>
    /// Called after each row is rewritten, with its table and Id, so a test can act at a known point
    /// in the pass.
    /// </summary>
    internal Action<string, int>? RowRewritten { get; init; }

    /// <summary>
    /// Runs the pass on an open connection and commits only if every value migrated or verified.
    /// </summary>
    public Task<IReadOnlyList<ConnectionStringReencryptionCounts>> RunAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    ) => RunAsync(connection, rewrite: true, cancellationToken);

    /// <summary>
    /// Verifies every value as the pass would and counts the legacy ones, changing nothing. Throws
    /// <see cref="ConnectionStringReencryptionException"/> for a value the pass could not migrate. A
    /// value written while the check runs may be missed.
    /// </summary>
    public Task<IReadOnlyList<ConnectionStringReencryptionCounts>> CheckAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    ) => RunAsync(connection, rewrite: false, cancellationToken);

    private async Task<IReadOnlyList<ConnectionStringReencryptionCounts>> RunAsync(
        DbConnection connection,
        bool rewrite,
        CancellationToken cancellationToken
    )
    {
        await using DbTransaction transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken
        );

        try
        {
            List<ConnectionStringReencryptionCounts> counts = [];
            foreach (string table in _tables)
            {
                counts.Add(
                    await MigrateTableAsync(connection, transaction, table, rewrite, cancellationToken)
                );
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (rewrite)
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
            else
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            return counts;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<ConnectionStringReencryptionCounts> MigrateTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        bool rewrite,
        CancellationToken cancellationToken
    )
    {
        int converted = 0;
        int alreadyCurrent = 0;

        // No lower bound on the first batch: Id has no positive constraint, so zero and negative
        // values are rows like any other.
        int? afterId = null;

        while (true)
        {
            List<(int Id, byte[] Value)> batch = await ReadBatchAsync(
                connection,
                transaction,
                table,
                afterId,
                rewrite,
                cancellationToken
            );
            if (batch.Count == 0)
            {
                break;
            }

            foreach ((int id, byte[] value) in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                byte[]? replacement = Reencrypt(table, id, value);
                if (replacement is null)
                {
                    alreadyCurrent++;
                }
                else if (!rewrite)
                {
                    converted++;
                }
                else
                {
                    await ReplaceAsync(
                        connection,
                        transaction,
                        table,
                        id,
                        value,
                        replacement,
                        cancellationToken
                    );
                    converted++;
                    RowRewritten?.Invoke(table, id);
                }

                afterId = id;
            }
        }

        return new ConnectionStringReencryptionCounts(table, converted, alreadyCurrent);
    }

    /// <summary>
    /// Returns the envelope to store, or null when the value is already an envelope that authenticates.
    /// </summary>
    private byte[]? Reencrypt(string table, int id, byte[] value)
    {
        if (IsEnvelopeCandidate(value))
        {
            try
            {
                _encryptionService.Decrypt(value);
                return null;
            }
            catch (AuthenticationTagMismatchException)
            {
                throw Failure(
                    table,
                    id,
                    "its envelope failed authentication: it was altered, or encrypted with a different key"
                );
            }
            catch (Exception ex) when (ex is CryptographicException or DecoderFallbackException)
            {
                throw Failure(table, id, "its envelope is malformed, not recognized, or unreadable");
            }
        }

        if (!HasLegacyLength(value.Length))
        {
            throw Failure(table, id, "its value is neither an envelope nor legacy AES-CBC cipher text");
        }

        string plainText;
        try
        {
            plainText = _encryptionService.DecryptLegacyStrictly(value);
        }
        catch (CryptographicException)
        {
            throw Failure(table, id, "its legacy value could not be decrypted with the configured key");
        }
        catch (DecoderFallbackException)
        {
            // A wrong key can leave valid padding by chance, and its output is then rarely UTF-8. This
            // is a guard, not authentication.
            throw Failure(
                table,
                id,
                "its legacy value does not decrypt to UTF-8 text with the configured key"
            );
        }

        if (plainText.Length == 0)
        {
            throw Failure(table, id, "its legacy value decrypts to an empty connection string");
        }

        byte[] replacement = _encryptionService.Encrypt(plainText)!;
        if (_encryptionService.Decrypt(replacement) != plainText)
        {
            throw Failure(table, id, "its re-encrypted value did not verify");
        }

        return replacement;
    }

    private static ConnectionStringReencryptionException Failure(string table, int id, string reason) =>
        new($"Connection string re-encryption failed for {table} Id {id}: {reason}. No changes were made.");

    private async Task<List<(int Id, byte[] Value)>> ReadBatchAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        int? afterId,
        bool lockRows,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        string postgresqlLock = lockRows ? " FOR UPDATE" : "";
        string mssqlLock = lockRows ? " WITH (UPDLOCK, ROWLOCK)" : "";
        string afterIdCondition = _engine switch
        {
            _ when afterId is null => "",
            ConnectionStringReencryptionEngine.Postgresql => " AND \"Id\" > @AfterId",
            _ => " AND Id > @AfterId",
        };
        command.CommandText = _engine switch
        {
            ConnectionStringReencryptionEngine.Postgresql => $"""
                SELECT "Id", "ConnectionString" FROM "dmscs"."{table}"
                WHERE "ConnectionString" IS NOT NULL{afterIdCondition}
                ORDER BY "Id" LIMIT @BatchSize{postgresqlLock}
                """,
            _ => $"""
                SELECT TOP (@BatchSize) Id, ConnectionString FROM dmscs.{table}{mssqlLock}
                WHERE ConnectionString IS NOT NULL{afterIdCondition}
                ORDER BY Id
                """,
        };
        if (afterId is not null)
        {
            AddParameter(command, "@AfterId", DbType.Int32, afterId.Value);
        }
        AddParameter(command, "@BatchSize", DbType.Int32, _batchSize);

        List<(int Id, byte[] Value)> batch = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            batch.Add((reader.GetInt32(0), (byte[])reader.GetValue(1)));
        }

        return batch;
    }

    private async Task ReplaceAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        int id,
        byte[] original,
        byte[] replacement,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _engine switch
        {
            ConnectionStringReencryptionEngine.Postgresql => $"""
                UPDATE "dmscs"."{table}" SET "ConnectionString" = @Replacement
                WHERE "Id" = @Id AND "ConnectionString" = @Original
                """,
            _ => $"""
                UPDATE dmscs.{table} SET ConnectionString = @Replacement
                WHERE Id = @Id AND ConnectionString = @Original
                """,
        };
        AddParameter(command, "@Replacement", DbType.Binary, replacement);
        AddParameter(command, "@Id", DbType.Int32, id);
        AddParameter(command, "@Original", DbType.Binary, original);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Failure(table, id, "the row changed while the pass was running");
        }
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
