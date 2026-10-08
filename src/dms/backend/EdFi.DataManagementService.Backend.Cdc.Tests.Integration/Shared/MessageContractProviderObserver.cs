// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

/// <summary>Read-only capture observations plus the administrative heartbeat used for source fences.
/// The caller supplies an admitted binding and a host-reachable connection factory; no setup or teardown.</summary>
internal sealed class MessageContractProviderObserver(
    CoreCdc.CdcBinding binding,
    Func<DbConnection> createConnection
)
{
    private readonly CdcProviderArtifactNames _artifacts = CdcDeploymentRequest.GetProviderArtifactNames(
        binding
    );

    public async Task AssertCaptureInventoryAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        token = timeout.Token;
        if (binding.Provider == CoreCdc.CdcProvider.Postgresql)
        {
            string inventory = await ReadScalarAsync(
                """
                SELECT string_agg(schemaname || '.' || tablename, ',' ORDER BY tablename)
                FROM pg_publication_tables WHERE pubname = @publication;
                """,
                token,
                _artifacts.Postgresql!.PublicationName.Value
            );
            inventory.Should().Be("dms.CdcHeartbeat,dms.Document,dms.DocumentCache");
            string fullIdentity = await ReadScalarAsync(
                """
                SELECT string_agg(relname, ',' ORDER BY relname) FROM pg_class
                WHERE oid IN ('dms."Document"'::regclass, 'dms."DocumentCache"'::regclass) AND relreplident = 'f';
                """,
                token
            );
            fullIdentity.Should().Be("Document");
        }
        else
        {
            string inventory = await ReadScalarAsync(
                """
                SELECT STRING_AGG(OBJECT_SCHEMA_NAME(source_object_id) + '.' + OBJECT_NAME(source_object_id), ',')
                    WITHIN GROUP (ORDER BY OBJECT_NAME(source_object_id)) FROM cdc.change_tables;
                """,
                token
            );
            inventory.Should().Be("dms.CdcHeartbeat,dms.Document,dms.DocumentCache");
            string captures = await ReadScalarAsync(
                "SELECT STRING_AGG(capture_instance, ',') WITHIN GROUP (ORDER BY capture_instance) FROM cdc.change_tables;",
                token
            );
            (
                captures
                == string.Join(
                    ',',
                    _artifacts
                        .SqlServer!.CaptureInstanceNames.Values.Select(n => n.Value)
                        .Order(StringComparer.Ordinal)
                )
            )
                .Should()
                .BeTrue("capture instances must belong to the admitted binding (names redacted)");
        }
    }

    public async Task<CoreCdc.CdcPostgresqlWalPosition> CapturePostgresqlWalAsync(CancellationToken token)
    {
        string wal = await ReadScalarAsync("SELECT pg_current_wal_lsn()::text;", token);
        var parsed = CoreCdc.CdcPostgresqlProviderPosition.ParseWalLsn(wal);
        parsed.Succeeded.Should().BeTrue();
        return parsed.Position!.Value;
    }

    public Task AdvanceHeartbeatAsync(CancellationToken token) =>
        WithProviderConnectionAsync(
            async connection =>
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText =
                    binding.Provider == CoreCdc.CdcProvider.Postgresql
                        ? """
                        UPDATE "dms"."CdcHeartbeat" SET "HeartbeatSequence" = "HeartbeatSequence" + 1,
                            "HeartbeatAt" = clock_timestamp() WHERE "HeartbeatId" = 1;
                        """
                        : "UPDATE [dms].[CdcHeartbeat] SET [HeartbeatSequence] = [HeartbeatSequence] + 1, [HeartbeatAt] = SYSUTCDATETIME() WHERE [HeartbeatId] = 1;";
                (await command.ExecuteNonQueryAsync(token)).Should().Be(1);
            },
            token
        );

    /// <summary>A retained heartbeat after the preceding writes supplies an actual function-mode
    /// after-image barrier. Reading its source table or Kafka ends alone is insufficient.</summary>
    public async Task<MessageContractSqlServerPosition> CaptureSqlServerHeartbeatBarrierAsync(
        CancellationToken token
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        token = timeout.Token;
        long sequence = long.Parse(
            await ReadScalarAsync(
                "SELECT [HeartbeatSequence] FROM [dms].[CdcHeartbeat] WHERE [HeartbeatId] = 1",
                token
            ),
            CultureInfo.InvariantCulture
        );
        await AdvanceHeartbeatAsync(token);
        string capture = _artifacts.SqlServer!.CaptureInstanceNames[CdcSourceTableKind.CdcHeartbeat].Value;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            MessageContractSqlServerPosition position = new("", "", 2);
            await WithProviderConnectionAsync(
                async connection =>
                {
                    await using DbCommand command = connection.CreateCommand();
                    command.CommandText = $"""
                    DECLARE @from_lsn binary(10) = sys.fn_cdc_get_min_lsn(@capture);
                    DECLARE @to_lsn binary(10) = sys.fn_cdc_get_max_lsn();
                    IF @from_lsn <> 0x00000000000000000000 AND @to_lsn >= @from_lsn
                    SELECT TOP (1) [__$start_lsn], [__$seqval]
                    FROM cdc.[fn_cdc_get_all_changes_{capture.Replace(
                        "]",
                        "]]",
                        StringComparison.Ordinal
                    )}](@from_lsn, @to_lsn, N'all')
                    WHERE [__$operation] = 4 AND [HeartbeatSequence] > @sequence
                    ORDER BY [__$start_lsn], [__$seqval];
                    """;
                    DbParameter captureParameter = command.CreateParameter();
                    captureParameter.ParameterName = "capture";
                    captureParameter.Value = capture;
                    command.Parameters.Add(captureParameter);
                    DbParameter sequenceParameter = command.CreateParameter();
                    sequenceParameter.ParameterName = "sequence";
                    sequenceParameter.Value = sequence;
                    command.Parameters.Add(sequenceParameter);
                    await using DbDataReader reader = await command.ExecuteReaderAsync(token);
                    if (await reader.ReadAsync(token))
                    {
                        position = new(FormatLsn((byte[])reader[0]), FormatLsn((byte[])reader[1]), 2);
                    }
                },
                token
            );
            if (position.CommitLsn.Length > 0)
            {
                return position;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }
        throw new AssertionException(
            "SQL Server heartbeat after-image capture timed out; capture job/function prerequisite unavailable. Details redacted."
        );

        static string FormatLsn(byte[] bytes)
        {
            var parsed = CoreCdc.CdcSqlServerProviderPositionParser.NormalizeTenByteLsn(
                bytes,
                "$.heartbeatLsn"
            );
            parsed.Succeeded.Should().BeTrue();
            return parsed.Lsn!.Value.ToString();
        }
    }

    private async Task<string> ReadScalarAsync(string sql, CancellationToken token, string publication = "")
    {
        string value = "";
        await WithProviderConnectionAsync(
            async connection =>
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = sql;
                if (publication.Length > 0)
                {
                    DbParameter parameter = command.CreateParameter();
                    parameter.ParameterName = "publication";
                    parameter.Value = publication;
                    command.Parameters.Add(parameter);
                }
                value =
                    Convert.ToString(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture)
                    ?? "";
            },
            token
        );
        return value;
    }

    private async Task WithProviderConnectionAsync(Func<DbConnection, Task> action, CancellationToken token)
    {
        try
        {
            await using DbConnection connection = createConnection();
            await connection.OpenAsync(token);
            await action(connection);
        }
        catch (DbException)
        {
            throw new InvalidOperationException(
                "CDC message contract provider observation failed. Details redacted."
            );
        }
    }
}
