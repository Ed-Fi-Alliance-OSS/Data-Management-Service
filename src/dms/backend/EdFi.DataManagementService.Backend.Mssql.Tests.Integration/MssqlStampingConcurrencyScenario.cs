// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal static class MssqlStampingConcurrencyScenario
{
    public static async Task RunAsync(
        MssqlGeneratedDdlTestDatabase database,
        string ownerTable,
        long firstDocumentId,
        long secondDocumentId,
        long neighborDocumentId,
        string firstWriteSql,
        string secondWriteSql
    )
    {
        // This is only a failure watchdog. Ordering comes from completed lock commands and the
        // write gate, never a timer. Cancel the other writer immediately if either command fails.
        using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = watchdog.Token;
        await database.ExecuteNonQueryAsync("ALTER DATABASE CURRENT SET RECURSIVE_TRIGGERS ON;");
        try
        {
            await using var firstConnection = new SqlConnection(database.ConnectionString);
            await using var secondConnection = new SqlConnection(database.ConnectionString);
            await firstConnection.OpenAsync(token);
            await secondConnection.OpenAsync(token);
            await using var firstTransaction = (SqlTransaction)
                await firstConnection.BeginTransactionAsync(token);
            await using var secondTransaction = (SqlTransaction)
                await secondConnection.BeginTransactionAsync(token);

            var watermark = await database.ExecuteScalarAsync<long>("SELECT [dms].[GetMaxChangeVersion]();");
            var beforeNeighbor = await ReadCommittedStampAsync(neighborDocumentId);
            var firstBefore = await ReadStampAsync(firstTransaction, firstDocumentId);
            var secondBefore = await ReadStampAsync(secondTransaction, secondDocumentId);

            // Both transactions own disjoint owner and existing work keys before either trigger
            // runs. A maintenance scan into the other owner/work row encounters its preheld lock,
            // even if that writer has not started its UPDATE yet. XLOCK + HOLDLOCK retains the
            // locks even on engines with optimized locking.
            var firstSession = await LockOwnerAndWorkAsync(firstTransaction, firstDocumentId);
            var secondSession = await LockOwnerAndWorkAsync(secondTransaction, secondDocumentId);
            firstSession.Should().NotBe(secondSession);
            await TestContext.Out.WriteLineAsync(
                $"Both writers locked their owner/work keys: {firstSession}, {secondSession}."
            );

            var beforeWrites = await database.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME();");
            TaskCompletionSource writeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstWrite = WriteAsync(firstTransaction, firstDocumentId, firstWriteSql, writeGate.Task);
            var secondWrite = WriteAsync(secondTransaction, secondDocumentId, secondWriteSql, writeGate.Task);
            writeGate.SetResult();
            var stamped = await Task.WhenAll(firstWrite, secondWrite);
            await TestContext.Out.WriteLineAsync(
                "Both writers completed stamping, stamp-only maintenance, and commit."
            );
            var afterWrites = await database.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME();");
            foreach (var stamp in stamped)
            {
                stamp.Timestamp.Should().BeOnOrAfter(beforeWrites).And.BeOnOrBefore(afterWrites);
                stamp.LastEnqueuedAt.Should().BeOnOrAfter(beforeWrites).And.BeOnOrBefore(afterWrites);
            }

            stamped
                .Select(stamp => stamp.DocumentVersion)
                .Should()
                .BeEquivalentTo(new[] { watermark + 1, watermark + 2 });
            AssertChangedStamp(stamped[0], firstBefore);
            AssertChangedStamp(stamped[1], secondBefore);

            (await ReadCommittedStampAsync(firstDocumentId)).Should().Be(stamped[0]);
            (await ReadCommittedStampAsync(secondDocumentId)).Should().Be(stamped[1]);
            (await ReadCommittedStampAsync(neighborDocumentId)).Should().Be(beforeNeighbor);
            (await database.ExecuteScalarAsync<long>("SELECT [dms].[GetMaxChangeVersion]();"))
                .Should()
                .Be(watermark + 2);
            (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM [dms].[DocumentProjectionWork];"))
                .Should()
                .Be(3);
            await TestContext.Out.WriteLineAsync(
                $"Committed document versions {stamped[0].DocumentVersion}, {stamped[1].DocumentVersion}; neighbor and work metadata verified."
            );

            async Task<int> LockOwnerAndWorkAsync(SqlTransaction transaction, long documentId)
            {
                await using var command = new SqlCommand(
                    $"""
                    SELECT [DocumentId], [ContentLastModifiedAt] FROM {ownerTable} WITH (XLOCK, ROWLOCK, HOLDLOCK, FORCESEEK)
                    WHERE [DocumentId] = @documentId;
                    SELECT [DocumentId] FROM [dms].[DocumentProjectionWork] WITH (XLOCK, ROWLOCK, HOLDLOCK, FORCESEEK)
                    WHERE [DocumentId] = @documentId;
                    SELECT @@SPID;
                    """,
                    transaction.Connection,
                    transaction
                );
                command.Parameters.AddWithValue("@documentId", documentId);
                await using var reader = await command.ExecuteReaderAsync(token);
                (await reader.ReadAsync(token)).Should().BeTrue();
                reader.GetInt64(0).Should().Be(documentId);
                (await reader.NextResultAsync(token)).Should().BeTrue();
                (await reader.ReadAsync(token)).Should().BeTrue();
                reader.GetInt64(0).Should().Be(documentId);
                (await reader.NextResultAsync(token)).Should().BeTrue();
                (await reader.ReadAsync(token)).Should().BeTrue();
                return Convert.ToInt32(reader.GetValue(0));
            }

            async Task<Stamp> WriteAsync(SqlTransaction transaction, long documentId, string sql, Task gate)
            {
                try
                {
                    await gate.WaitAsync(token);
                    await using var command = new SqlCommand(sql, transaction.Connection, transaction);
                    command.CommandTimeout = 120;
                    command.Parameters.AddWithValue("@documentId", documentId);
                    (await command.ExecuteNonQueryAsync(token)).Should().BeGreaterThan(0);
                    var afterWrite = await ReadStampAsync(transaction, documentId);
                    command.CommandText = $"""
                        UPDATE {ownerTable}
                        SET [ContentVersion] = [ContentVersion], [ContentLastModifiedAt] = [ContentLastModifiedAt]
                        WHERE [DocumentId] = @documentId;
                        """;
                    await command.ExecuteNonQueryAsync(token);
                    (await ReadStampAsync(transaction, documentId)).Should().Be(afterWrite);
                    // Allow harmless serialization on Document. Requiring both UPDATEs to finish
                    // before either commits would manufacture an application-level wait cycle
                    // when SQL Server chooses a small-table document scan. The preheld owner/work
                    // locks still expose the cross-owner scan deadlock these cases guard against.
                    await transaction.CommitAsync(token);
                    return afterWrite;
                }
                catch
                {
                    await watchdog.CancelAsync();
                    throw;
                }
            }

            async Task<Stamp> ReadCommittedStampAsync(long documentId)
            {
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(token);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
                return await ReadStampAsync(transaction, documentId);
            }

            async Task<Stamp> ReadStampAsync(SqlTransaction transaction, long documentId)
            {
                await using var command = new SqlCommand(
                    $"""
                    SELECT doc.[ContentVersion], owner.[ContentVersion], owner.[ContentLastModifiedAt],
                           work.[RequiredContentVersion], work.[FirstEnqueuedAt], work.[LastEnqueuedAt]
                    FROM [dms].[Document] doc
                    JOIN {ownerTable} owner ON owner.[DocumentId] = doc.[DocumentId]
                    JOIN [dms].[DocumentProjectionWork] work ON work.[DocumentId] = doc.[DocumentId]
                    WHERE doc.[DocumentId] = @documentId;
                    """,
                    transaction.Connection,
                    transaction
                );
                command.Parameters.AddWithValue("@documentId", documentId);
                await using var reader = await command.ExecuteReaderAsync(token);
                (await reader.ReadAsync(token)).Should().BeTrue();
                var result = new Stamp(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetDateTime(2),
                    reader.GetInt64(3),
                    reader.GetDateTime(4),
                    reader.GetDateTime(5)
                );
                (await reader.ReadAsync(token)).Should().BeFalse();
                return result;
            }
        }
        finally
        {
            // Transactions/connections are disposed before resetting the database option, including
            // on cancellation or a deadlock victim. No failing writer leaves its peer waiting.
            await database.ExecuteNonQueryAsync("ALTER DATABASE CURRENT SET RECURSIVE_TRIGGERS OFF;");
        }
    }

    private static void AssertChangedStamp(Stamp after, Stamp before)
    {
        after.DocumentVersion.Should().BeGreaterThan(before.DocumentVersion);
        after.OwnerVersion.Should().Be(after.DocumentVersion);
        after.RequiredVersion.Should().Be(after.DocumentVersion);
        after.Timestamp.Should().BeAfter(before.Timestamp);
        after.FirstEnqueuedAt.Should().Be(before.FirstEnqueuedAt);
        after.LastEnqueuedAt.Should().BeOnOrAfter(before.LastEnqueuedAt);
    }

    private sealed record Stamp(
        long DocumentVersion,
        long OwnerVersion,
        DateTime Timestamp,
        long RequiredVersion,
        DateTime FirstEnqueuedAt,
        DateTime LastEnqueuedAt
    );
}
