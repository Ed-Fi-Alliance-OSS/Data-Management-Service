// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

/// <summary>Read-only, single-document observations. Supply the resolved host connection string for
/// the admitted database, never the static E2E admin/reset database. No domain writes are exposed.</summary>
internal sealed class CdcDocumentObserver(CdcProvider provider, string connectionString)
{
    internal DbConnection CreateConnection() =>
        provider switch
        {
            CdcProvider.Postgresql => new NpgsqlConnection(connectionString),
            CdcProvider.SqlServer => new SqlConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

    public Task<IReadOnlyList<CdcSourceDocument>> ReadSourceAsync(Guid uuid, CancellationToken token) =>
        ReadAsync(
            """
            SELECT d."DocumentId", d."DocumentUuid", d."ResourceKeyId", d."ContentVersion",
                   d."ContentLastModifiedAt", es."EffectiveSchemaHash",
                   rk."ProjectName", rk."ResourceName", rk."ResourceVersion"
            FROM "dms"."Document" d
            JOIN "dms"."ResourceKey" rk ON rk."ResourceKeyId" = d."ResourceKeyId"
            CROSS JOIN "dms"."EffectiveSchema" es
            WHERE d."DocumentUuid" = @id;
            """,
            uuid,
            reader => new CdcSourceDocument(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetInt16(2),
                reader.GetInt64(3),
                ReadTimestamp(reader.GetValue(4)),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)
            ),
            token
        );

    // Read by the retained DocumentId so deletion checks can detect orphan work/cache too.
    public Task<IReadOnlyList<CdcProjectionWork>> ReadWorkAsync(long documentId, CancellationToken token) =>
        ReadAsync(
            """
            SELECT "RequiredContentVersion", "FirstEnqueuedAt", "LastEnqueuedAt"
            FROM "dms"."DocumentProjectionWork" WHERE "DocumentId" = @id;
            """,
            documentId,
            reader => new CdcProjectionWork(
                reader.GetInt64(0),
                ReadTimestamp(reader.GetValue(1)),
                ReadTimestamp(reader.GetValue(2))
            ),
            token
        );

    public Task<IReadOnlyList<CdcCacheDocument>> ReadCacheAsync(long documentId, CancellationToken token) =>
        ReadAsync(
            """
            SELECT "DocumentUuid", "ContentVersion", "StreamEtag", "LastModifiedAt",
                   "ProjectName", "ResourceName", "ResourceVersion", "DocumentJson"
            FROM "dms"."DocumentCache" WHERE "DocumentId" = @id;
            """,
            documentId,
            reader => new CdcCacheDocument(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetString(2),
                ReadTimestamp(reader.GetValue(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                JsonNode.Parse(reader.GetString(7))!.AsObject()
            ),
            token
        );

    private async Task<IReadOnlyList<T>> ReadAsync<T>(
        string sql,
        object id,
        Func<DbDataReader, T> read,
        CancellationToken token
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using DbConnection connection = CreateConnection();
        await connection.OpenAsync(deadline.Token);
        await using DbCommand command = connection.CreateCommand();
        command.CommandTimeout = 30;
        // All quoted identifiers are fixed test SQL, with no quoted literals or caller SQL.
        command.CommandText =
            provider == CdcProvider.SqlServer
                ? System.Text.RegularExpressions.Regex.Replace(sql, "\"([^\"]+)\"", "[$1]")
                : sql;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        await using DbDataReader reader = await command.ExecuteReaderAsync(deadline.Token);
        List<T> rows = [];
        while (await reader.ReadAsync(deadline.Token))
        {
            if (rows.Count != 0)
            {
                throw new InvalidOperationException("A single-document observation returned multiple rows.");
            }
            rows.Add(read(reader));
        }
        return rows;
    }

    internal static DateTimeOffset ReadTimestamp(object value) =>
        value switch
        {
            DateTimeOffset offset => offset.ToUniversalTime(),
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("Unexpected database timestamp type."),
        };
}

internal sealed record CdcSourceDocument(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion,
    DateTimeOffset ContentLastModifiedAt,
    string EffectiveSchemaHash,
    string ProjectName,
    string ResourceName,
    string ResourceVersion
);

internal sealed record CdcProjectionWork(
    long RequiredContentVersion,
    DateTimeOffset FirstEnqueuedAt,
    DateTimeOffset LastEnqueuedAt
);

internal sealed record CdcCacheDocument(
    Guid DocumentUuid,
    long ContentVersion,
    string StreamEtag,
    DateTimeOffset LastModifiedAt,
    string ProjectName,
    string ResourceName,
    string ResourceVersion,
    JsonObject DocumentJson
)
{
    // Do not accidentally export document payloads through record diagnostics.
    public override string ToString() =>
        $"Cache document {DocumentUuid:D}, version {ContentVersion.ToString(CultureInfo.InvariantCulture)}";
}
