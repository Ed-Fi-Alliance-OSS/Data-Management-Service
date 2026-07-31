// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

internal sealed class CdcPostgresqlHeartbeatPublicationProvider : ICdcProviderSetupProvider
{
    private static readonly ISqlDialect _dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);

    private static readonly IReadOnlyList<CdcSourceTableKind> _publicationTableOrder =
    [
        CdcSourceTableKind.DocumentCache,
        CdcSourceTableKind.Document,
        CdcSourceTableKind.CdcHeartbeat,
    ];

    public CdcProvider Provider => CdcProvider.Postgresql;

    public IReadOnlyList<CdcProviderSetupStep> BuildSetupSteps(CdcProviderSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var postgresqlNames =
            request.ArtifactNames.Postgresql
            ?? throw new InvalidOperationException("PostgreSQL artifact names were not supplied.");

        return
        [
            new CdcProviderSetupStep(
                CdcProviderArtifactKind.HeartbeatTable,
                SafeName(DmsTableNames.CdcHeartbeat),
                canCreateInInitialSetup: true,
                ExecuteHeartbeatTableAsync
            ),
            new CdcProviderSetupStep(
                CdcProviderArtifactKind.SourceTable,
                new CdcSafeName("postgresql_cdc_source_inventory"),
                canCreateInInitialSetup: false,
                ExecuteSourceInventoryAsync
            ),
            new CdcProviderSetupStep(
                CdcProviderArtifactKind.PostgresqlReplicaIdentity,
                SafeName(DmsTableNames.Document),
                canCreateInInitialSetup: true,
                ExecuteReplicaIdentityAsync
            ),
            new CdcProviderSetupStep(
                CdcProviderArtifactKind.PostgresqlPublication,
                postgresqlNames.PublicationName,
                canCreateInInitialSetup: true,
                ExecutePublicationAsync
            ),
        ];
    }

    private static async Task<CdcProviderSetupStepResult> ExecuteHeartbeatTableAsync(
        CdcProviderSetupStepContext context,
        CancellationToken cancellationToken
    )
    {
        if (
            !TryGetExecutor(
                context,
                CdcProviderArtifactKind.HeartbeatTable,
                out var executor,
                out var failure
            )
        )
        {
            return failure;
        }

        try
        {
            var heartbeatTableExists = await TableExistsAsync(
                    executor,
                    DmsTableNames.CdcHeartbeat,
                    cancellationToken
                )
                .ConfigureAwait(false);
            var state = CdcProviderArtifactState.Matched;

            if (!heartbeatTableExists)
            {
                if (context.Mode == CdcProviderSetupStepMode.ExactMatchOnly)
                {
                    return ArtifactOnly(
                        CdcProviderArtifactKind.HeartbeatTable,
                        SafeName(DmsTableNames.CdcHeartbeat),
                        CdcProviderArtifactState.Missing,
                        new Dictionary<string, string> { ["table"] = "missing" }
                    );
                }

                await executor
                    .ExecuteNonQueryAsync(CreateHeartbeatTableSql(context.Request), cancellationToken)
                    .ConfigureAwait(false);
                state = CdcProviderArtifactState.Created;
            }

            var shape = await InspectHeartbeatTableShapeAsync(executor, cancellationToken)
                .ConfigureAwait(false);
            if (!shape.IsExactMatch)
            {
                return ArtifactOnly(
                    CdcProviderArtifactKind.HeartbeatTable,
                    SafeName(DmsTableNames.CdcHeartbeat),
                    CdcProviderArtifactState.Mismatched,
                    shape.ObservedValues
                );
            }

            var singleton = await InspectHeartbeatSingletonAsync(executor, cancellationToken)
                .ConfigureAwait(false);
            if (
                singleton.SingletonRowCount == 0
                && context.Mode == CdcProviderSetupStepMode.CreateOrExactMatch
            )
            {
                await executor
                    .ExecuteNonQueryAsync(InsertHeartbeatSingletonSql(context.Request), cancellationToken)
                    .ConfigureAwait(false);
                state = CdcProviderArtifactState.Created;
                singleton = await InspectHeartbeatSingletonAsync(executor, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!singleton.IsExactMatch)
            {
                return ArtifactOnly(
                    CdcProviderArtifactKind.HeartbeatTable,
                    SafeName(DmsTableNames.CdcHeartbeat),
                    CdcProviderArtifactState.Mismatched,
                    shape
                        .ObservedValues.Concat(singleton.ObservedValues)
                        .ToDictionary(pair => pair.Key, pair => pair.Value)
                );
            }

            var heartbeatActionQuery = BuildHeartbeatActionQuery(context.Request);

            return new CdcProviderSetupStepResult(
                artifactInventory:
                [
                    new CdcProviderArtifactObservation(
                        CdcProviderArtifactKind.HeartbeatTable,
                        SafeName(DmsTableNames.CdcHeartbeat),
                        state,
                        shape
                            .ObservedValues.Concat(singleton.ObservedValues)
                            .ToDictionary(pair => pair.Key, pair => pair.Value)
                    ),
                ],
                heartbeatActionQuery: heartbeatActionQuery
            );
        }
        catch (DbException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.HeartbeatTable,
                SafeName(DmsTableNames.CdcHeartbeat),
                exception
            );
        }
        catch (InvalidOperationException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.HeartbeatTable,
                SafeName(DmsTableNames.CdcHeartbeat),
                exception
            );
        }
    }

    private static async Task<CdcProviderSetupStepResult> ExecuteSourceInventoryAsync(
        CdcProviderSetupStepContext context,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetExecutor(context, CdcProviderArtifactKind.SourceTable, out var executor, out var failure))
        {
            return failure;
        }

        try
        {
            var liveInventory = await ReadLiveSourceInventoryAsync(
                    executor,
                    context.Request.ExpectedSourceInventory,
                    cancellationToken
                )
                .ConfigureAwait(false);

            return new CdcProviderSetupStepResult(sourceTableInventory: liveInventory);
        }
        catch (DbException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.SourceTable,
                new CdcSafeName("postgresql_cdc_source_inventory"),
                exception
            );
        }
        catch (InvalidOperationException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.SourceTable,
                new CdcSafeName("postgresql_cdc_source_inventory"),
                exception
            );
        }
    }

    private static async Task<CdcProviderSetupStepResult> ExecuteReplicaIdentityAsync(
        CdcProviderSetupStepContext context,
        CancellationToken cancellationToken
    )
    {
        if (
            !TryGetExecutor(
                context,
                CdcProviderArtifactKind.PostgresqlReplicaIdentity,
                out var executor,
                out var failure
            )
        )
        {
            return failure;
        }

        try
        {
            var relReplicaIdentity = await ReadDocumentReplicaIdentityAsync(executor, cancellationToken)
                .ConfigureAwait(false);

            if (relReplicaIdentity is null)
            {
                return ArtifactOnly(
                    CdcProviderArtifactKind.PostgresqlReplicaIdentity,
                    SafeName(DmsTableNames.Document),
                    CdcProviderArtifactState.Missing,
                    new Dictionary<string, string> { ["replica_identity"] = "missing" }
                );
            }

            if (relReplicaIdentity == "f")
            {
                return ArtifactOnly(
                    CdcProviderArtifactKind.PostgresqlReplicaIdentity,
                    SafeName(DmsTableNames.Document),
                    CdcProviderArtifactState.Matched,
                    ReplicaIdentityObservedValues(relReplicaIdentity)
                );
            }

            if (context.Mode == CdcProviderSetupStepMode.ExactMatchOnly)
            {
                return ArtifactOnly(
                    CdcProviderArtifactKind.PostgresqlReplicaIdentity,
                    SafeName(DmsTableNames.Document),
                    CdcProviderArtifactState.Mismatched,
                    ReplicaIdentityObservedValues(relReplicaIdentity)
                );
            }

            await executor
                .ExecuteNonQueryAsync(
                    $"ALTER TABLE {_dialect.QualifyTable(DmsTableNames.Document)} REPLICA IDENTITY FULL;",
                    cancellationToken
                )
                .ConfigureAwait(false);

            relReplicaIdentity = await ReadDocumentReplicaIdentityAsync(executor, cancellationToken)
                .ConfigureAwait(false);

            return ArtifactOnly(
                CdcProviderArtifactKind.PostgresqlReplicaIdentity,
                SafeName(DmsTableNames.Document),
                relReplicaIdentity == "f"
                    ? CdcProviderArtifactState.Created
                    : CdcProviderArtifactState.Mismatched,
                ReplicaIdentityObservedValues(relReplicaIdentity)
            );
        }
        catch (DbException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.PostgresqlReplicaIdentity,
                SafeName(DmsTableNames.Document),
                exception
            );
        }
        catch (InvalidOperationException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.PostgresqlReplicaIdentity,
                SafeName(DmsTableNames.Document),
                exception
            );
        }
    }

    private static async Task<CdcProviderSetupStepResult> ExecutePublicationAsync(
        CdcProviderSetupStepContext context,
        CancellationToken cancellationToken
    )
    {
        if (
            !TryGetExecutor(
                context,
                CdcProviderArtifactKind.PostgresqlPublication,
                out var executor,
                out var failure
            )
        )
        {
            return failure;
        }

        var publicationName = context.Request.ArtifactNames.Postgresql!.PublicationName;

        try
        {
            var supportsPublishViaPartitionRoot = await SupportsPublishViaPartitionRootAsync(
                    executor,
                    cancellationToken
                )
                .ConfigureAwait(false);
            var publication = await InspectPublicationAsync(
                    executor,
                    context.Request,
                    supportsPublishViaPartitionRoot,
                    cancellationToken
                )
                .ConfigureAwait(false);
            var state = CdcProviderArtifactState.Matched;

            if (!publication.Exists)
            {
                if (context.Mode == CdcProviderSetupStepMode.ExactMatchOnly)
                {
                    return ArtifactOnly(
                        CdcProviderArtifactKind.PostgresqlPublication,
                        publicationName,
                        CdcProviderArtifactState.Missing,
                        new Dictionary<string, string> { ["publication"] = "missing" }
                    );
                }

                await executor
                    .ExecuteNonQueryAsync(
                        CreatePublicationSql(context.Request, supportsPublishViaPartitionRoot),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                state = CdcProviderArtifactState.Created;
                publication = await InspectPublicationAsync(
                        executor,
                        context.Request,
                        supportsPublishViaPartitionRoot,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            if (!publication.IsExactMatch)
            {
                return ArtifactOnly(
                    CdcProviderArtifactKind.PostgresqlPublication,
                    publicationName,
                    CdcProviderArtifactState.Mismatched,
                    publication.ObservedValues
                );
            }

            return new CdcProviderSetupStepResult(
                artifactInventory:
                [
                    new CdcProviderArtifactObservation(
                        CdcProviderArtifactKind.PostgresqlPublication,
                        publicationName,
                        state,
                        publication.ObservedValues
                    ),
                ],
                expectedMessageKeyColumns:
                [
                    new CdcExpectedMessageKeyColumns(
                        CdcSourceTableKind.Document,
                        [new DbColumnName("DocumentUuid")]
                    ),
                    new CdcExpectedMessageKeyColumns(
                        CdcSourceTableKind.DocumentCache,
                        [new DbColumnName("DocumentUuid")]
                    ),
                ]
            );
        }
        catch (DbException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.PostgresqlPublication,
                publicationName,
                exception
            );
        }
        catch (InvalidOperationException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.PostgresqlPublication,
                publicationName,
                exception
            );
        }
    }

    internal static CdcHeartbeatActionQuery BuildHeartbeatActionQuery(CdcProviderSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var heartbeat = SourceTable(request, CdcSourceTableKind.CdcHeartbeat);
        var heartbeatId = SourceColumn(heartbeat, "HeartbeatId");
        var heartbeatSequence = SourceColumn(heartbeat, "HeartbeatSequence");
        var heartbeatAt = SourceColumn(heartbeat, "HeartbeatAt");
        var sql =
            $"UPDATE {heartbeat.EmittedQuotedTableName} SET {heartbeatSequence.EmittedQuotedColumnName} = {heartbeatSequence.EmittedQuotedColumnName} + 1, {heartbeatAt.EmittedQuotedColumnName} = now() WHERE {heartbeatId.EmittedQuotedColumnName} = 1";

        return new CdcHeartbeatActionQuery(sql, Sha256(sql));
    }

    internal static string CreatePublicationSql(
        CdcProviderSetupRequest request,
        bool supportsPublishViaPartitionRoot
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var publicationName = request.ArtifactNames.Postgresql!.PublicationName;
        var publicationOptions = supportsPublishViaPartitionRoot
            ? "WITH (publish = 'insert, update, delete', publish_via_partition_root = false)"
            : "WITH (publish = 'insert, update, delete')";
        var tableList = string.Join(
            ", ",
            _publicationTableOrder.Select(kind => SourceTable(request, kind).EmittedQuotedTableName)
        );

        return $"CREATE PUBLICATION {_dialect.QuoteIdentifier(publicationName.Value)} FOR TABLE {tableList} {publicationOptions};";
    }

    private static string CreateHeartbeatTableSql(CdcProviderSetupRequest request)
    {
        var heartbeat = SourceTable(request, CdcSourceTableKind.CdcHeartbeat);
        var columns = heartbeat.Columns.ToDictionary(column => column.ColumnName.Value);

        string ColumnDefinition(string columnName)
        {
            var column = columns[columnName];
            return $"{column.EmittedQuotedColumnName} {column.ProviderDataType} NOT NULL";
        }

        return $"""
            CREATE TABLE IF NOT EXISTS {heartbeat.EmittedQuotedTableName}
            (
                {ColumnDefinition("HeartbeatId")},
                {ColumnDefinition("HeartbeatSequence")},
                {ColumnDefinition("HeartbeatAt")},
                CONSTRAINT {_dialect.QuoteIdentifier("PK_CdcHeartbeat")} PRIMARY KEY ({columns[
                "HeartbeatId"
            ].EmittedQuotedColumnName}),
                CONSTRAINT {_dialect.QuoteIdentifier("CK_CdcHeartbeat_Singleton")} CHECK ({columns[
                "HeartbeatId"
            ].EmittedQuotedColumnName} = 1),
                CONSTRAINT {_dialect.QuoteIdentifier("CK_CdcHeartbeat_Sequence")} CHECK ({columns[
                "HeartbeatSequence"
            ].EmittedQuotedColumnName} >= 0)
            );

            {InsertHeartbeatSingletonSql(request)}
            """;
    }

    private static string InsertHeartbeatSingletonSql(CdcProviderSetupRequest request)
    {
        var heartbeat = SourceTable(request, CdcSourceTableKind.CdcHeartbeat);
        var heartbeatId = SourceColumn(heartbeat, "HeartbeatId");
        var heartbeatSequence = SourceColumn(heartbeat, "HeartbeatSequence");
        var heartbeatAt = SourceColumn(heartbeat, "HeartbeatAt");

        return $"""
            INSERT INTO {heartbeat.EmittedQuotedTableName} ({heartbeatId.EmittedQuotedColumnName}, {heartbeatSequence.EmittedQuotedColumnName}, {heartbeatAt.EmittedQuotedColumnName})
            VALUES (1, 0, now())
            ON CONFLICT ({heartbeatId.EmittedQuotedColumnName}) DO NOTHING;
            """;
    }

    private static async Task<bool> TableExistsAsync(
        ICdcProviderDatabaseExecutor executor,
        DbTableName table,
        CancellationToken cancellationToken
    )
    {
        var rows = await executor.QueryAsync(TableExistsSql(table), cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 && ReadBool(rows[0], "table_exists");
    }

    private static string TableExistsSql(DbTableName table) =>
        $"""
            /* cdc:postgresql:table-exists */
            SELECT (to_regclass('{EscapeSqlLiteral(
                _dialect.QualifyTable(table)
            )}') IS NOT NULL)::text AS table_exists;
            """;

    private static async Task<IReadOnlyList<CdcSourceTableInventory>> ReadLiveSourceInventoryAsync(
        ICdcProviderDatabaseExecutor executor,
        IReadOnlyList<CdcSourceTableInventory> expectedSourceInventory,
        CancellationToken cancellationToken
    )
    {
        var rows = await executor
            .QueryAsync(SourceInventorySql(expectedSourceInventory), cancellationToken)
            .ConfigureAwait(false);

        List<CdcSourceTableInventory> inventory = [];
        foreach (var expectedTable in expectedSourceInventory)
        {
            var columnRows = rows.Where(row =>
                    ReadRequired(row, "table_schema") == expectedTable.TableName.Schema.Value
                    && ReadRequired(row, "table_name") == expectedTable.TableName.Name
                )
                .OrderBy(row => ReadInt32(row, "ordinal"))
                .ToArray();

            if (columnRows.Length == 0)
            {
                continue;
            }

            inventory.Add(
                new CdcSourceTableInventory(
                    expectedTable.TableKind,
                    expectedTable.TableName,
                    expectedTable.EmittedQuotedTableName,
                    columnRows
                        .Select(row => new CdcSourceColumnInventory(
                            new DbColumnName(ReadRequired(row, "column_name")),
                            _dialect.QuoteIdentifier(ReadRequired(row, "column_name")),
                            ReadInt32(row, "ordinal"),
                            ReadRequired(row, "provider_data_type"),
                            ReadBool(row, "is_nullable")
                        ))
                        .ToArray()
                )
            );
        }

        return inventory;
    }

    private static string SourceInventorySql(IReadOnlyList<CdcSourceTableInventory> expectedSourceInventory)
    {
        var values = string.Join(
            ",\n    ",
            expectedSourceInventory.Select(
                (table, index) =>
                    $"({index + 1}, '{EscapeSqlLiteral(table.TableName.Schema.Value)}', '{EscapeSqlLiteral(table.TableName.Name)}')"
            )
        );

        return $"""
            /* cdc:postgresql:source-inventory */
            WITH expected_tables(table_order, table_schema, table_name) AS (
                VALUES
                {values}
            )
            SELECT
                columns.table_schema,
                columns.table_name,
                columns.column_name,
                columns.ordinal_position::text AS ordinal,
                CASE
                    WHEN columns.is_identity = 'YES'
                        AND columns.data_type = 'bigint'
                        THEN 'bigint GENERATED ALWAYS AS IDENTITY'
                    WHEN columns.data_type = 'character varying'
                        THEN 'varchar(' || columns.character_maximum_length::text || ')'
                    ELSE columns.data_type
                END AS provider_data_type,
                (columns.is_nullable = 'YES')::text AS is_nullable
            FROM expected_tables
            INNER JOIN information_schema.columns columns
                ON columns.table_schema = expected_tables.table_schema
                AND columns.table_name = expected_tables.table_name
            ORDER BY expected_tables.table_order, columns.ordinal_position;
            """;
    }

    private static async Task<HeartbeatTableShapeInspection> InspectHeartbeatTableShapeAsync(
        ICdcProviderDatabaseExecutor executor,
        CancellationToken cancellationToken
    )
    {
        var rows = await executor.QueryAsync(HeartbeatTableShapeSql, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return new HeartbeatTableShapeInspection(
                IsExactMatch: false,
                new Dictionary<string, string> { ["shape"] = "unavailable" }
            );
        }

        var row = rows[0];
        var primaryKeyMatches = ReadBool(row, "primary_key_matches");
        var singletonCheckMatches = ReadBool(row, "singleton_check_matches");
        var sequenceCheckMatches = ReadBool(row, "sequence_check_matches");

        return new HeartbeatTableShapeInspection(
            primaryKeyMatches && singletonCheckMatches && sequenceCheckMatches,
            new Dictionary<string, string>
            {
                ["primary_key"] = primaryKeyMatches ? "matched" : "mismatched",
                ["singleton_check"] = singletonCheckMatches ? "matched" : "mismatched",
                ["sequence_check"] = sequenceCheckMatches ? "matched" : "mismatched",
            }
        );
    }

    private const string HeartbeatTableShapeSql = """
        /* cdc:postgresql:heartbeat-shape */
        SELECT
            EXISTS (
                SELECT 1
                FROM pg_catalog.pg_constraint constraint_info
                INNER JOIN pg_catalog.pg_class table_info
                    ON table_info.oid = constraint_info.conrelid
                INNER JOIN pg_catalog.pg_namespace namespace_info
                    ON namespace_info.oid = table_info.relnamespace
                WHERE namespace_info.nspname = 'dms'
                AND table_info.relname = 'CdcHeartbeat'
                AND constraint_info.conname = 'PK_CdcHeartbeat'
                AND constraint_info.contype = 'p'
                AND (
                    SELECT pg_catalog.array_agg(attribute_info.attname::text ORDER BY key_column.ordinality)
                    FROM pg_catalog.unnest(constraint_info.conkey) WITH ORDINALITY AS key_column(attnum, ordinality)
                    INNER JOIN pg_catalog.pg_attribute attribute_info
                        ON attribute_info.attrelid = table_info.oid
                        AND attribute_info.attnum = key_column.attnum
                ) = ARRAY['HeartbeatId']
            )::text AS primary_key_matches,
            EXISTS (
                SELECT 1
                FROM pg_catalog.pg_constraint constraint_info
                INNER JOIN pg_catalog.pg_class table_info
                    ON table_info.oid = constraint_info.conrelid
                INNER JOIN pg_catalog.pg_namespace namespace_info
                    ON namespace_info.oid = table_info.relnamespace
                WHERE namespace_info.nspname = 'dms'
                AND table_info.relname = 'CdcHeartbeat'
                AND constraint_info.conname = 'CK_CdcHeartbeat_Singleton'
                AND constraint_info.contype = 'c'
                AND pg_catalog.pg_get_constraintdef(constraint_info.oid) LIKE '%"HeartbeatId" = 1%'
            )::text AS singleton_check_matches,
            EXISTS (
                SELECT 1
                FROM pg_catalog.pg_constraint constraint_info
                INNER JOIN pg_catalog.pg_class table_info
                    ON table_info.oid = constraint_info.conrelid
                INNER JOIN pg_catalog.pg_namespace namespace_info
                    ON namespace_info.oid = table_info.relnamespace
                WHERE namespace_info.nspname = 'dms'
                AND table_info.relname = 'CdcHeartbeat'
                AND constraint_info.conname = 'CK_CdcHeartbeat_Sequence'
                AND constraint_info.contype = 'c'
                AND pg_catalog.pg_get_constraintdef(constraint_info.oid) LIKE '%"HeartbeatSequence" >= 0%'
            )::text AS sequence_check_matches;
        """;

    private static async Task<HeartbeatSingletonInspection> InspectHeartbeatSingletonAsync(
        ICdcProviderDatabaseExecutor executor,
        CancellationToken cancellationToken
    )
    {
        var rows = await executor.QueryAsync(HeartbeatSingletonSql, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return new HeartbeatSingletonInspection(
                SingletonRowCount: 0,
                IsExactMatch: false,
                new Dictionary<string, string> { ["singleton"] = "unavailable" }
            );
        }

        var row = rows[0];
        var rowCount = ReadInt64(row, "row_count");
        var singletonRowCount = ReadInt32(row, "singleton_row_count");
        var extraRowCount = ReadInt64(row, "extra_row_count");
        var heartbeatSequence = ReadInt64(row, "heartbeat_sequence");
        var isExactMatch =
            rowCount == 1 && singletonRowCount == 1 && extraRowCount == 0 && heartbeatSequence >= 0;

        return new HeartbeatSingletonInspection(
            singletonRowCount,
            isExactMatch,
            new Dictionary<string, string>
            {
                ["row_count"] = rowCount.ToString(),
                ["singleton_row_count"] = singletonRowCount.ToString(),
                ["extra_row_count"] = extraRowCount.ToString(),
                ["heartbeat_sequence"] = heartbeatSequence.ToString(),
            }
        );
    }

    private const string HeartbeatSingletonSql = """
        /* cdc:postgresql:heartbeat-singleton */
        SELECT
            COUNT(*)::text AS row_count,
            COUNT(*) FILTER (WHERE "HeartbeatId" = 1)::text AS singleton_row_count,
            COUNT(*) FILTER (WHERE "HeartbeatId" <> 1)::text AS extra_row_count,
            COALESCE(MAX("HeartbeatSequence") FILTER (WHERE "HeartbeatId" = 1), -1)::text AS heartbeat_sequence
        FROM "dms"."CdcHeartbeat";
        """;

    private static async Task<string?> ReadDocumentReplicaIdentityAsync(
        ICdcProviderDatabaseExecutor executor,
        CancellationToken cancellationToken
    )
    {
        var rows = await executor
            .QueryAsync(DocumentReplicaIdentitySql, cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 0 ? null : ReadRequired(rows[0], "relreplident");
    }

    private const string DocumentReplicaIdentitySql = """
        /* cdc:postgresql:document-replica-identity */
        SELECT table_info.relreplident::text AS relreplident
        FROM pg_catalog.pg_class table_info
        INNER JOIN pg_catalog.pg_namespace namespace_info
            ON namespace_info.oid = table_info.relnamespace
        WHERE namespace_info.nspname = 'dms'
        AND table_info.relname = 'Document';
        """;

    private static async Task<bool> SupportsPublishViaPartitionRootAsync(
        ICdcProviderDatabaseExecutor executor,
        CancellationToken cancellationToken
    )
    {
        var rows = await executor.QueryAsync(ServerVersionSql, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return false;
        }

        return ReadInt32(rows[0], "server_version_num") >= 130000;
    }

    private const string ServerVersionSql = """
        /* cdc:postgresql:server-version */
        SHOW server_version_num;
        """;

    private static async Task<PublicationInspection> InspectPublicationAsync(
        ICdcProviderDatabaseExecutor executor,
        CdcProviderSetupRequest request,
        bool supportsPublishViaPartitionRoot,
        CancellationToken cancellationToken
    )
    {
        var publicationName = request.ArtifactNames.Postgresql!.PublicationName;
        var propertyRows = await executor
            .QueryAsync(
                PublicationPropertiesSql(publicationName, supportsPublishViaPartitionRoot),
                cancellationToken
            )
            .ConfigureAwait(false);

        if (propertyRows.Count == 0)
        {
            return new PublicationInspection(
                Exists: false,
                IsExactMatch: false,
                new Dictionary<string, string> { ["publication"] = "missing" }
            );
        }

        var tableRows = await executor
            .QueryAsync(PublicationTablesSql(publicationName), cancellationToken)
            .ConfigureAwait(false);

        var expectedTables = _publicationTableOrder
            .Select(kind => SourceTable(request, kind))
            .Select(table => $"{table.TableName.Schema.Value}.{table.TableName.Name}")
            .ToArray();
        var observedTables = tableRows
            .Select(row => $"{ReadRequired(row, "schema_name")}.{ReadRequired(row, "table_name")}")
            .ToArray();
        var observedTableSet = observedTables.ToHashSet(StringComparer.Ordinal);
        var expectedTableSet = expectedTables.ToHashSet(StringComparer.Ordinal);

        var properties = propertyRows[0];
        var publishesInsert = ReadBool(properties, "publishes_insert");
        var publishesUpdate = ReadBool(properties, "publishes_update");
        var publishesDelete = ReadBool(properties, "publishes_delete");
        var publishesTruncate = ReadBool(properties, "publishes_truncate");
        var publishesAllTables = ReadBool(properties, "publishes_all_tables");
        var publishViaPartitionRoot = ReadRequired(properties, "publish_via_partition_root");
        var allColumns = tableRows.All(row => ReadBool(row, "publishes_all_columns"));
        var noRowFilters = tableRows.All(row => ReadBool(row, "row_filter_absent"));
        var exactTables =
            observedTables.Length == expectedTables.Length && observedTableSet.SetEquals(expectedTableSet);
        var exactProperties =
            publishesInsert
            && publishesUpdate
            && publishesDelete
            && !publishesTruncate
            && !publishesAllTables
            && (
                publishViaPartitionRoot == "unsupported"
                || string.Equals(publishViaPartitionRoot, "false", StringComparison.OrdinalIgnoreCase)
            )
            && allColumns
            && noRowFilters;

        return new PublicationInspection(
            Exists: true,
            exactTables && exactProperties,
            new Dictionary<string, string>
            {
                ["tables"] = string.Join(",", observedTables.Order(StringComparer.Ordinal)),
                ["expected_tables"] = string.Join(",", expectedTables),
                ["publish"] = $"{publishesInsert},{publishesUpdate},{publishesDelete}",
                ["publishes_truncate"] = publishesTruncate.ToString(),
                ["publishes_all_tables"] = publishesAllTables.ToString(),
                ["publish_via_partition_root"] = publishViaPartitionRoot,
                ["row_filters"] = noRowFilters ? "absent" : "present",
                ["column_lists"] = allColumns ? "absent" : "present",
            }
        );
    }

    private static string PublicationPropertiesSql(
        CdcSafeName publicationName,
        bool supportsPublishViaPartitionRoot
    )
    {
        var publishViaPartitionRootProjection = supportsPublishViaPartitionRoot
            ? "publication.pubviaroot::text AS publish_via_partition_root"
            : "'unsupported' AS publish_via_partition_root";

        return $"""
            /* cdc:postgresql:publication-properties */
            SELECT
                publication.pubinsert::text AS publishes_insert,
                publication.pubupdate::text AS publishes_update,
                publication.pubdelete::text AS publishes_delete,
                publication.pubtruncate::text AS publishes_truncate,
                publication.puballtables::text AS publishes_all_tables,
                {publishViaPartitionRootProjection}
            FROM pg_catalog.pg_publication publication
            WHERE publication.pubname = '{EscapeSqlLiteral(publicationName.Value)}';
            """;
    }

    private static string PublicationTablesSql(CdcSafeName publicationName) =>
        $"""
            /* cdc:postgresql:publication-tables */
            SELECT
                namespace_info.nspname AS schema_name,
                table_info.relname AS table_name,
                (publication_table.prattrs IS NULL)::text AS publishes_all_columns,
                (publication_table.prqual IS NULL)::text AS row_filter_absent
            FROM pg_catalog.pg_publication_rel publication_table
            INNER JOIN pg_catalog.pg_publication publication
                ON publication.oid = publication_table.prpubid
            INNER JOIN pg_catalog.pg_class table_info
                ON table_info.oid = publication_table.prrelid
            INNER JOIN pg_catalog.pg_namespace namespace_info
                ON namespace_info.oid = table_info.relnamespace
            WHERE publication.pubname = '{EscapeSqlLiteral(publicationName.Value)}'
            ORDER BY namespace_info.nspname, table_info.relname;
            """;

    private static bool TryGetExecutor(
        CdcProviderSetupStepContext context,
        CdcProviderArtifactKind artifactKind,
        out ICdcProviderDatabaseExecutor executor,
        out CdcProviderSetupStepResult failure
    )
    {
        if (context.Request.DatabaseExecutor is { } databaseExecutor)
        {
            executor = databaseExecutor;
            failure = new CdcProviderSetupStepResult();
            return true;
        }

        executor = null!;
        failure = new CdcProviderSetupStepResult(
            diagnostics:
            [
                new CdcProviderDiagnostic(
                    Code: "CDC_PROVIDER_DATABASE_EXECUTOR_MISSING",
                    Category: CdcProviderDiagnosticCategory.SetupPrincipalFailure,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.SetupPrincipal,
                    ArtifactKind: artifactKind,
                    SafeName: new CdcSafeName("postgresql_setup_connection"),
                    ExpectedValue: "database-executor",
                    ObservedValue: "missing",
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                ),
            ]
        );
        return false;
    }

    private static CdcProviderSetupStepResult SetupPrincipalFailure(
        CdcProviderArtifactKind artifactKind,
        CdcSafeName safeName,
        Exception exception
    ) =>
        new(
            diagnostics:
            [
                new CdcProviderDiagnostic(
                    Code: "CDC_POSTGRESQL_SETUP_PRINCIPAL_FAILURE",
                    Category: CdcProviderDiagnosticCategory.SetupPrincipalFailure,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.SetupPrincipal,
                    ArtifactKind: artifactKind,
                    SafeName: safeName,
                    ExpectedValue: "setup-operation-succeeded",
                    ObservedValue: "provider-error",
                    ProviderErrorClass: exception.GetType().Name,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                ),
            ]
        );

    private static CdcProviderSetupStepResult ArtifactOnly(
        CdcProviderArtifactKind artifactKind,
        CdcSafeName safeName,
        CdcProviderArtifactState state,
        IReadOnlyDictionary<string, string> observedValues
    ) =>
        new(
            artifactInventory:
            [
                new CdcProviderArtifactObservation(artifactKind, safeName, state, observedValues),
            ]
        );

    private static IReadOnlyDictionary<string, string> ReplicaIdentityObservedValues(
        string? relReplicaIdentity
    ) =>
        new Dictionary<string, string>
        {
            ["replica_identity"] = ReplicaIdentityDisplayName(relReplicaIdentity),
            ["required_replica_identity"] = "FULL",
        };

    private static string ReplicaIdentityDisplayName(string? relReplicaIdentity) =>
        relReplicaIdentity switch
        {
            "d" => "DEFAULT",
            "n" => "NOTHING",
            "f" => "FULL",
            "i" => "INDEX",
            null => "missing",
            _ => relReplicaIdentity,
        };

    private static CdcSourceTableInventory SourceTable(
        CdcProviderSetupRequest request,
        CdcSourceTableKind tableKind
    ) => request.ExpectedSourceInventory.Single(table => table.TableKind == tableKind);

    private static CdcSourceColumnInventory SourceColumn(CdcSourceTableInventory table, string columnName) =>
        table.Columns.Single(column => column.ColumnName.Value == columnName);

    private static CdcSafeName SafeName(DbTableName table) =>
        new($"{SafeText(table.Schema.Value)}.{SafeText(table.Name)}");

    private static string SafeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(
                char.IsLetterOrDigit(character) || character == '_' || character == '.' ? character : '_'
            );
        }

        return builder.ToString();
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ReadRequired(IReadOnlyDictionary<string, string?> row, string columnName) =>
        row.TryGetValue(columnName, out var value) && value is not null
            ? value
            : throw new InvalidOperationException($"Expected PostgreSQL result column '{columnName}'.");

    private static bool ReadBool(IReadOnlyDictionary<string, string?> row, string columnName)
    {
        var value = ReadRequired(row, columnName);
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return value switch
        {
            "t" or "1" => true,
            "f" or "0" => false,
            _ => throw new InvalidOperationException(
                $"Expected PostgreSQL result column '{columnName}' to contain a boolean value."
            ),
        };
    }

    private static int ReadInt32(IReadOnlyDictionary<string, string?> row, string columnName) =>
        int.Parse(ReadRequired(row, columnName));

    private static long ReadInt64(IReadOnlyDictionary<string, string?> row, string columnName) =>
        long.Parse(ReadRequired(row, columnName));

    private sealed record HeartbeatTableShapeInspection(
        bool IsExactMatch,
        IReadOnlyDictionary<string, string> ObservedValues
    );

    private sealed record HeartbeatSingletonInspection(
        int SingletonRowCount,
        bool IsExactMatch,
        IReadOnlyDictionary<string, string> ObservedValues
    );

    private sealed record PublicationInspection(
        bool Exists,
        bool IsExactMatch,
        IReadOnlyDictionary<string, string> ObservedValues
    );
}
