// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
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
                CdcProviderArtifactKind.SourceFingerprint,
                CdcSourceFingerprintMetadata.SafeArtifactName,
                canCreateInInitialSetup: false,
                ExecuteSourceFingerprintAsync
            ),
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
            new CdcProviderSetupStep(
                CdcProviderArtifactKind.PostgresqlReplicationSlot,
                postgresqlNames.ReplicationSlotName,
                canCreateInInitialSetup: true,
                ExecuteReplicationSlotAsync
            ),
            new CdcProviderSetupStep(
                CdcProviderArtifactKind.Grant,
                request.ConnectorPrincipal.SafePrincipalName,
                canCreateInInitialSetup: true,
                ExecuteConnectorPrincipalAccessAsync
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

    private static async Task<CdcProviderSetupStepResult> ExecuteSourceFingerprintAsync(
        CdcProviderSetupStepContext context,
        CancellationToken cancellationToken
    )
    {
        if (
            !TryGetExecutor(
                context,
                CdcProviderArtifactKind.SourceFingerprint,
                out var executor,
                out var failure
            )
        )
        {
            return failure;
        }

        return await CdcSourceFingerprintMetadata
            .ReadAsync(executor, SourceFingerprintSql, CdcProvider.Postgresql, cancellationToken)
            .ConfigureAwait(false);
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
                return new CdcProviderSetupStepResult(
                    artifactInventory:
                    [
                        new CdcProviderArtifactObservation(
                            CdcProviderArtifactKind.PostgresqlPublication,
                            publicationName,
                            CdcProviderArtifactState.Mismatched,
                            publication.ObservedValues
                        ),
                    ],
                    diagnostics: publication.Diagnostics
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

    private static async Task<CdcProviderSetupStepResult> ExecuteReplicationSlotAsync(
        CdcProviderSetupStepContext context,
        CancellationToken cancellationToken
    )
    {
        if (
            !TryGetExecutor(
                context,
                CdcProviderArtifactKind.PostgresqlReplicationSlot,
                out var executor,
                out var failure
            )
        )
        {
            return failure;
        }

        var replicationSlotName = context.Request.ArtifactNames.Postgresql!.ReplicationSlotName;

        try
        {
            var slot = await InspectReplicationSlotAsync(
                    executor,
                    replicationSlotName,
                    context.Mode,
                    cancellationToken
                )
                .ConfigureAwait(false);
            var state = CdcProviderArtifactState.Matched;
            var createdDuringCurrentCall = false;

            if (!slot.Exists)
            {
                if (context.Mode == CdcProviderSetupStepMode.ExactMatchOnly)
                {
                    return ReplicationSlotResult(
                        replicationSlotName,
                        CdcProviderArtifactState.Missing,
                        slot.ObservedValues,
                        slot.Classification,
                        diagnostics:
                        [
                            ProviderHistoryLossEvidence(
                                replicationSlotName,
                                "CDC_POSTGRESQL_REPLICATION_SLOT_MISSING",
                                expectedValue: "permanent-pgoutput-slot",
                                observedValue: "missing"
                            ),
                        ]
                    );
                }

                try
                {
                    await executor
                        .ExecuteNonQueryAsync(
                            CreateReplicationSlotSql(replicationSlotName),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
                catch (DbException exception)
                {
                    return SetupPrincipalFailure(
                        CdcProviderArtifactKind.PostgresqlReplicationSlot,
                        replicationSlotName,
                        exception
                    );
                }

                state = CdcProviderArtifactState.Created;
                createdDuringCurrentCall = true;
                slot = await InspectReplicationSlotAsync(
                        executor,
                        replicationSlotName,
                        context.Mode,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            if (!slot.IsExactMatch)
            {
                return ReplicationSlotResult(
                    replicationSlotName,
                    CdcProviderArtifactState.Mismatched,
                    slot.ObservedValues,
                    slot.Classification,
                    slot.Diagnostics
                );
            }

            if (context.Mode == CdcProviderSetupStepMode.CreateOrExactMatch && !createdDuringCurrentCall)
            {
                var proofDiagnostics = InitialReplicationSlotProofDiagnostics(
                    context.Request,
                    replicationSlotName,
                    slot
                );
                if (proofDiagnostics.Count > 0)
                {
                    return ReplicationSlotResult(
                        replicationSlotName,
                        CdcProviderArtifactState.Mismatched,
                        slot.ObservedValues,
                        proofDiagnostics
                            .FirstOrDefault(diagnostic =>
                                diagnostic.Category
                                    == CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence
                                || diagnostic.Category
                                    == CdcProviderDiagnosticCategory.ProviderHistoryUnavailable
                            )
                            ?.Classification
                            ?? CdcProviderRetryContinuityClassification.FailClosed,
                        proofDiagnostics
                    );
                }
            }

            var observedValues = createdDuringCurrentCall
                ? AddInitialSlotProofObservedValues(context.Request, replicationSlotName, slot)
                : slot.ObservedValues;

            return ReplicationSlotResult(replicationSlotName, state, observedValues, slot.Classification);
        }
        catch (DbException exception)
        {
            return ProviderHistoryUnavailable(
                replicationSlotName,
                "CDC_POSTGRESQL_REPLICATION_SLOT_HISTORY_UNAVAILABLE",
                providerErrorClass: exception.GetType().Name
            );
        }
        catch (InvalidOperationException exception)
        {
            return ProviderHistoryUnavailable(
                replicationSlotName,
                "CDC_POSTGRESQL_REPLICATION_SLOT_HISTORY_UNAVAILABLE",
                providerErrorClass: exception.GetType().Name
            );
        }
    }

    private static async Task<CdcProviderSetupStepResult> ExecuteConnectorPrincipalAccessAsync(
        CdcProviderSetupStepContext context,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetExecutor(context, CdcProviderArtifactKind.Grant, out var executor, out var failure))
        {
            return failure;
        }

        var connectorPrincipal = context.Request.ConnectorPrincipal.SafePrincipalName;

        try
        {
            var access = await InspectConnectorPrincipalAccessAsync(
                    executor,
                    context.Request,
                    cancellationToken
                )
                .ConfigureAwait(false);
            var state = CdcProviderArtifactState.Matched;

            if (
                access.IsGrantableMissingPrivilege
                && context.Mode == CdcProviderSetupStepMode.CreateOrExactMatch
            )
            {
                await executor
                    .ExecuteNonQueryAsync(GrantConnectorPrivilegesSql(context.Request), cancellationToken)
                    .ConfigureAwait(false);
                state = CdcProviderArtifactState.Created;
                access = await InspectConnectorPrincipalAccessAsync(
                        executor,
                        context.Request,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            if (!access.IsExactMatch)
            {
                return ConnectorPrincipalAccessResult(
                    connectorPrincipal,
                    CdcProviderArtifactState.Mismatched,
                    access
                );
            }

            var result = ConnectorPrincipalAccessResult(connectorPrincipal, state, access);

            if (context.Request.ConnectorPrincipalProbeFactory is null)
            {
                return result;
            }

            var probeResult = await context
                .Request.ConnectorPrincipalProbeFactory.ProbeAsync(context.Request, cancellationToken)
                .ConfigureAwait(false);

            return new CdcProviderSetupStepResult(
                artifactInventory: result.ArtifactInventory,
                grantInventory: result.GrantInventory.Concat(probeResult.GrantInventory).ToArray(),
                diagnostics: result.Diagnostics.Concat(probeResult.Diagnostics).ToArray()
            );
        }
        catch (DbException exception)
        {
            return SetupPrincipalFailure(CdcProviderArtifactKind.Grant, connectorPrincipal, exception);
        }
        catch (InvalidOperationException exception)
        {
            return SetupPrincipalFailure(CdcProviderArtifactKind.Grant, connectorPrincipal, exception);
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

    internal static string CreateReplicationSlotSql(CdcSafeName replicationSlotName)
    {
        return $"""
            /* cdc:postgresql:create-replication-slot */
            SELECT slot_name, lsn::text
            FROM pg_catalog.pg_create_logical_replication_slot('{EscapeSqlLiteral(
                replicationSlotName.Value
            )}', 'pgoutput');
            """;
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

    private const string SourceFingerprintSql = """
        /* cdc:postgresql:source-fingerprint */
        SELECT "SourceIdentity"::text AS source_identity
        FROM dms."DataStoreIdentity"
        WHERE "DataStoreIdentitySingletonId" = 1;
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
                new Dictionary<string, string> { ["publication"] = "missing" },
                Diagnostics: []
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

        var observedValues = new Dictionary<string, string>
        {
            ["tables"] = string.Join(",", observedTables.Order(StringComparer.Ordinal)),
            ["expected_tables"] = string.Join(",", expectedTables),
            ["publish"] = $"{publishesInsert},{publishesUpdate},{publishesDelete}",
            ["publishes_truncate"] = publishesTruncate.ToString(),
            ["publishes_all_tables"] = publishesAllTables.ToString(),
            ["publish_via_partition_root"] = publishViaPartitionRoot,
            ["row_filters"] = noRowFilters ? "absent" : "present",
            ["column_lists"] = allColumns ? "absent" : "present",
        };

        return new PublicationInspection(
            Exists: true,
            exactTables && exactProperties,
            observedValues,
            PublicationDiagnostics(publicationName, observedTableSet)
        );
    }

    private static IReadOnlyList<CdcProviderDiagnostic> PublicationDiagnostics(
        CdcSafeName publicationName,
        HashSet<string> observedTableSet
    )
    {
        if (!observedTableSet.Contains("dms.DocumentProjectionWork"))
        {
            return [];
        }

        return
        [
            new CdcProviderDiagnostic(
                Code: "CDC_POSTGRESQL_WORK_TABLE_PUBLICATION_FORBIDDEN",
                Category: CdcProviderDiagnosticCategory.WorkTableCaptureViolation,
                Severity: CdcProviderDiagnosticSeverity.Error,
                PrincipalKind: CdcPrincipalKind.None,
                ArtifactKind: CdcProviderArtifactKind.PostgresqlPublication,
                SafeName: publicationName,
                ExpectedValue: "dms.DocumentProjectionWork-not-published",
                ObservedValue: "published",
                ProviderErrorClass: null,
                Classification: CdcProviderRetryContinuityClassification.FailClosed
            ),
        ];
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

    private static async Task<ReplicationSlotInspection> InspectReplicationSlotAsync(
        ICdcProviderDatabaseExecutor executor,
        CdcSafeName replicationSlotName,
        CdcProviderSetupStepMode mode,
        CancellationToken cancellationToken
    )
    {
        var rows = await executor
            .QueryAsync(ReplicationSlotSql(replicationSlotName), cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return new ReplicationSlotInspection(
                Exists: false,
                IsExactMatch: false,
                ObservedValues: new Dictionary<string, string> { ["slot"] = "missing" },
                Classification: CdcProviderRetryContinuityClassification.SourceHistoryLost,
                Diagnostics: [],
                DatabaseIdentity: null,
                RestartLsn: null,
                ConfirmedFlushLsn: null
            );
        }

        var row = rows[0];
        var plugin = ReadRequired(row, "plugin");
        var slotType = ReadRequired(row, "slot_type");
        var database = ReadRequired(row, "database");
        var expectedDatabase = ReadRequired(row, "expected_database");
        var temporary = ReadBool(row, "temporary");
        var active = ReadBool(row, "active");
        var twoPhase = ReadRequired(row, "two_phase");
        var restartLsn = ReadRequired(row, "restart_lsn");
        var confirmedFlushLsn = ReadRequired(row, "confirmed_flush_lsn");
        var walStatus = ReadRequired(row, "wal_status");
        var invalidationReason = ReadRequired(row, "invalidation_reason");
        var activeAllowed = mode == CdcProviderSetupStepMode.ExactMatchOnly || !active;
        var retainedPositionsReadable =
            !string.IsNullOrWhiteSpace(restartLsn) && !string.IsNullOrWhiteSpace(confirmedFlushLsn);
        var noLostHistory =
            retainedPositionsReadable
            && !string.Equals(walStatus, "lost", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(invalidationReason);
        var exactShape =
            string.Equals(plugin, "pgoutput", StringComparison.Ordinal)
            && string.Equals(slotType, "logical", StringComparison.Ordinal)
            && string.Equals(database, expectedDatabase, StringComparison.Ordinal)
            && !temporary
            && (
                string.Equals(twoPhase, "unsupported", StringComparison.Ordinal)
                || string.Equals(twoPhase, "false", StringComparison.OrdinalIgnoreCase)
            );

        var observedValues = new Dictionary<string, string>
        {
            ["plugin"] = SafeText(plugin),
            ["slot_type"] = SafeText(slotType),
            ["database"] = SafeText(database),
            ["expected_database"] = SafeText(expectedDatabase),
            ["temporary"] = temporary.ToString(),
            ["active"] = active.ToString(),
            ["two_phase"] = SafeText(twoPhase),
            ["restart_lsn"] = SafeText(restartLsn),
            ["confirmed_flush_lsn"] = SafeText(confirmedFlushLsn),
            ["wal_status"] = SafeText(walStatus),
            ["invalidation_reason"] = SafeText(invalidationReason),
            ["retained_position_gap_evaluation"] = "not_evaluated_without_committed_offset",
        };

        var diagnostics = ReplicationSlotDiagnostics(
            replicationSlotName,
            exactShape,
            retainedPositionsReadable,
            noLostHistory,
            activeAllowed,
            observedValues
        );

        var classification =
            diagnostics
                .FirstOrDefault(diagnostic =>
                    diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence
                    || diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryUnavailable
                )
                ?.Classification
            ?? CdcProviderRetryContinuityClassification.None;

        return new ReplicationSlotInspection(
            Exists: true,
            IsExactMatch: exactShape && noLostHistory && activeAllowed,
            ObservedValues: observedValues,
            Classification: classification,
            Diagnostics: diagnostics,
            DatabaseIdentity: database,
            RestartLsn: restartLsn,
            ConfirmedFlushLsn: confirmedFlushLsn
        );
    }

    private static string ReplicationSlotSql(CdcSafeName replicationSlotName) =>
        $"""
            /* cdc:postgresql:replication-slot */
            SELECT
                slot.slot_name AS slot_name,
                COALESCE(slot.plugin, '') AS plugin,
                slot.slot_type AS slot_type,
                COALESCE(slot.database, '') AS database,
                current_database() AS expected_database,
                slot.temporary::text AS temporary,
                slot.active::text AS active,
                COALESCE(to_jsonb(slot)->>'two_phase', 'unsupported') AS two_phase,
                COALESCE(slot.restart_lsn::text, '') AS restart_lsn,
                COALESCE(slot.confirmed_flush_lsn::text, '') AS confirmed_flush_lsn,
                COALESCE(to_jsonb(slot)->>'wal_status', 'unavailable') AS wal_status,
                COALESCE(to_jsonb(slot)->>'invalidation_reason', '') AS invalidation_reason
            FROM pg_catalog.pg_replication_slots slot
            WHERE slot.slot_name = '{EscapeSqlLiteral(replicationSlotName.Value)}';
            """;

    private static async Task<ConnectorPrincipalAccessInspection> InspectConnectorPrincipalAccessAsync(
        ICdcProviderDatabaseExecutor executor,
        CdcProviderSetupRequest request,
        CancellationToken cancellationToken
    )
    {
        var rows = await executor
            .QueryAsync(ConnectorPrincipalAccessSql(request), cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return new ConnectorPrincipalAccessInspection(
                IsExactMatch: false,
                IsGrantableMissingPrivilege: false,
                ObservedValues: new Dictionary<string, string> { ["connector_access"] = "unavailable" },
                GrantInventory: [],
                Diagnostics:
                [
                    ConnectorPrincipalPrivilegeFailure(
                        request.ConnectorPrincipal.SafePrincipalName,
                        "CDC_POSTGRESQL_CONNECTOR_PRIVILEGE_UNAVAILABLE",
                        expectedValue: "readable-connector-privilege-inventory",
                        observedValue: "unavailable"
                    ),
                ]
            );
        }

        var row = rows[0];
        var roleExists = ReadBool(row, "role_exists");
        var canLogin = ReadBool(row, "can_login");
        var canReplicate = ReadBool(row, "can_replicate");
        var disallowedRoleAttributes = ReadCsv(row, "disallowed_role_attributes");
        var ownership = ReadCsv(row, "ownership");
        var hasDatabaseConnect = ReadBool(row, "database_connect");
        var hasSchemaUsage = ReadBool(row, "schema_usage");
        var hasDocumentSelect = ReadBool(row, "document_select");
        var hasDocumentCacheSelect = ReadBool(row, "document_cache_select");
        var hasHeartbeatSelect = ReadBool(row, "heartbeat_select");
        var hasHeartbeatSequenceUpdate = ReadBool(row, "heartbeat_sequence_update");
        var hasHeartbeatAtUpdate = ReadBool(row, "heartbeat_at_update");
        var hasHeartbeatIdUpdate = ReadBool(row, "heartbeat_id_update");
        var documentWritePrivileges = ReadCsv(row, "document_write_privileges");
        var documentCacheWritePrivileges = ReadCsv(row, "document_cache_write_privileges");
        var workTablePrivileges = ReadCsv(row, "work_table_privileges");
        var extraDmsSelectTables = ReadCsv(row, "extra_dms_select_tables");

        var missingRequiredPrivileges = MissingRequiredConnectorPrivileges(
            hasDatabaseConnect,
            hasSchemaUsage,
            hasDocumentSelect,
            hasDocumentCacheSelect,
            hasHeartbeatSelect,
            hasHeartbeatSequenceUpdate,
            hasHeartbeatAtUpdate
        );
        var hasRequiredRoleAttributes =
            roleExists
            && canLogin
            && canReplicate
            && disallowedRoleAttributes.Count == 0
            && ownership.Count == 0;
        var hasForbiddenPrivileges =
            hasHeartbeatIdUpdate
            || documentWritePrivileges.Count > 0
            || documentCacheWritePrivileges.Count > 0
            || workTablePrivileges.Count > 0
            || extraDmsSelectTables.Count > 0;
        var isGrantableMissingPrivilege =
            hasRequiredRoleAttributes && !hasForbiddenPrivileges && missingRequiredPrivileges.Count > 0;
        var isExactMatch =
            hasRequiredRoleAttributes && !hasForbiddenPrivileges && missingRequiredPrivileges.Count == 0;

        var observedValues = new Dictionary<string, string>
        {
            ["role_exists"] = roleExists.ToString(),
            ["can_login"] = canLogin.ToString(),
            ["can_replicate"] = canReplicate.ToString(),
            ["disallowed_role_attributes"] = CsvOrNone(disallowedRoleAttributes),
            ["ownership"] = CsvOrNone(ownership),
            ["missing_required_privileges"] = CsvOrNone(missingRequiredPrivileges),
            ["document_write_privileges"] = CsvOrNone(documentWritePrivileges),
            ["document_cache_write_privileges"] = CsvOrNone(documentCacheWritePrivileges),
            ["heartbeat_id_update"] = hasHeartbeatIdUpdate.ToString(),
            ["work_table_privileges"] = CsvOrNone(workTablePrivileges),
            ["extra_dms_select_tables"] = CsvOrNone(extraDmsSelectTables),
        };

        var diagnostics = ConnectorPrincipalAccessDiagnostics(
            request.ConnectorPrincipal.SafePrincipalName,
            roleExists,
            canLogin,
            canReplicate,
            disallowedRoleAttributes,
            ownership,
            missingRequiredPrivileges,
            hasHeartbeatIdUpdate,
            documentWritePrivileges,
            documentCacheWritePrivileges,
            workTablePrivileges,
            extraDmsSelectTables
        );

        return new ConnectorPrincipalAccessInspection(
            isExactMatch,
            isGrantableMissingPrivilege,
            observedValues,
            ConnectorGrantInventory(
                request,
                hasDatabaseConnect,
                hasSchemaUsage,
                hasDocumentSelect,
                hasDocumentCacheSelect,
                hasHeartbeatSelect,
                hasHeartbeatSequenceUpdate,
                hasHeartbeatAtUpdate,
                hasHeartbeatIdUpdate,
                documentWritePrivileges,
                documentCacheWritePrivileges,
                workTablePrivileges
            ),
            diagnostics
        );
    }

    private static string ConnectorPrincipalAccessSql(CdcProviderSetupRequest request)
    {
        var connectorPrincipal = EscapeSqlLiteral(request.ConnectorPrincipal.SafePrincipalName.Value);
        var documentTable = EscapeSqlLiteral(
            SourceTable(request, CdcSourceTableKind.Document).EmittedQuotedTableName
        );
        var documentCacheTable = EscapeSqlLiteral(
            SourceTable(request, CdcSourceTableKind.DocumentCache).EmittedQuotedTableName
        );
        var heartbeatTable = EscapeSqlLiteral(
            SourceTable(request, CdcSourceTableKind.CdcHeartbeat).EmittedQuotedTableName
        );
        var workTable = EscapeSqlLiteral(_dialect.QualifyTable(DmsTableNames.DocumentProjectionWork));
        var publicationName = EscapeSqlLiteral(request.ArtifactNames.Postgresql!.PublicationName.Value);

        return $"""
            /* cdc:postgresql:connector-principal-access */
            WITH connector AS (
                SELECT
                    role_info.oid,
                    role_info.rolcanlogin,
                    role_info.rolreplication,
                    role_info.rolsuper,
                    role_info.rolcreatedb,
                    role_info.rolcreaterole,
                    role_info.rolbypassrls
                FROM pg_catalog.pg_roles role_info
                WHERE role_info.rolname = '{connectorPrincipal}'
            ),
            dms_tables AS (
                SELECT table_info.table_name
                FROM information_schema.tables table_info
                WHERE table_info.table_schema = 'dms'
                AND table_info.table_type = 'BASE TABLE'
            )
            SELECT
                EXISTS (SELECT 1 FROM connector)::text AS role_exists,
                COALESCE((SELECT rolcanlogin::text FROM connector), 'false') AS can_login,
                COALESCE((SELECT rolreplication::text FROM connector), 'false') AS can_replicate,
                COALESCE(
                    (
                        SELECT string_agg(attribute_name, ',' ORDER BY attribute_name)
                        FROM (
                            SELECT 'SUPERUSER' AS attribute_name FROM connector WHERE rolsuper
                            UNION ALL SELECT 'CREATEDB' FROM connector WHERE rolcreatedb
                            UNION ALL SELECT 'CREATEROLE' FROM connector WHERE rolcreaterole
                            UNION ALL SELECT 'BYPASSRLS' FROM connector WHERE rolbypassrls
                            UNION ALL
                            SELECT special_role.rolname
                            FROM connector
                            INNER JOIN pg_catalog.pg_roles special_role
                                ON special_role.rolname IN ('pg_read_all_data', 'pg_write_all_data')
                                AND pg_catalog.pg_has_role(connector.oid, special_role.oid, 'member')
                        ) disallowed_attributes
                    ),
                    ''
                ) AS disallowed_role_attributes,
                COALESCE(
                    (
                        SELECT string_agg(owned_object, ',' ORDER BY owned_object)
                        FROM (
                            SELECT 'database' AS owned_object
                            FROM connector
                            INNER JOIN pg_catalog.pg_database database_info
                                ON database_info.datname = current_database()
                                AND database_info.datdba = connector.oid
                            UNION ALL
                            SELECT 'schema:dms'
                            FROM connector
                            INNER JOIN pg_catalog.pg_namespace namespace_info
                                ON namespace_info.nspname = 'dms'
                                AND namespace_info.nspowner = connector.oid
                            UNION ALL
                            SELECT 'table:' || table_info.relname
                            FROM connector
                            INNER JOIN pg_catalog.pg_class table_info
                                ON table_info.relowner = connector.oid
                            INNER JOIN pg_catalog.pg_namespace namespace_info
                                ON namespace_info.oid = table_info.relnamespace
                                AND namespace_info.nspname = 'dms'
                            WHERE table_info.relkind IN ('r', 'p')
                            UNION ALL
                            SELECT 'publication'
                            FROM connector
                            INNER JOIN pg_catalog.pg_publication publication
                                ON publication.pubname = '{publicationName}'
                                AND publication.pubowner = connector.oid
                        ) ownership
                    ),
                    ''
                ) AS ownership,
                COALESCE((SELECT pg_catalog.has_database_privilege(oid, current_database(), 'CONNECT')::text FROM connector), 'false') AS database_connect,
                COALESCE((SELECT pg_catalog.has_schema_privilege(oid, 'dms', 'USAGE')::text FROM connector), 'false') AS schema_usage,
                COALESCE((SELECT pg_catalog.has_table_privilege(oid, '{documentTable}', 'SELECT')::text FROM connector), 'false') AS document_select,
                COALESCE((SELECT pg_catalog.has_table_privilege(oid, '{documentCacheTable}', 'SELECT')::text FROM connector), 'false') AS document_cache_select,
                COALESCE((SELECT pg_catalog.has_table_privilege(oid, '{heartbeatTable}', 'SELECT')::text FROM connector), 'false') AS heartbeat_select,
                COALESCE((SELECT pg_catalog.has_column_privilege(oid, '{heartbeatTable}', 'HeartbeatSequence', 'UPDATE')::text FROM connector), 'false') AS heartbeat_sequence_update,
                COALESCE((SELECT pg_catalog.has_column_privilege(oid, '{heartbeatTable}', 'HeartbeatAt', 'UPDATE')::text FROM connector), 'false') AS heartbeat_at_update,
                COALESCE((SELECT pg_catalog.has_column_privilege(oid, '{heartbeatTable}', 'HeartbeatId', 'UPDATE')::text FROM connector), 'false') AS heartbeat_id_update,
                COALESCE(
                    (
                        SELECT string_agg(privilege_name, ',' ORDER BY privilege_name)
                        FROM (
                            SELECT 'INSERT' AS privilege_name FROM connector WHERE pg_catalog.has_table_privilege(oid, '{documentTable}', 'INSERT')
                            UNION ALL SELECT 'UPDATE' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{documentTable}', 'UPDATE')
                            UNION ALL SELECT 'DELETE' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{documentTable}', 'DELETE')
                            UNION ALL SELECT 'TRUNCATE' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{documentTable}', 'TRUNCATE')
                            UNION ALL SELECT 'REFERENCES' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{documentTable}', 'REFERENCES')
                            UNION ALL SELECT 'TRIGGER' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{documentTable}', 'TRIGGER')
                        ) document_write_privileges
                    ),
                    ''
                ) AS document_write_privileges,
                COALESCE(
                    (
                        SELECT string_agg(privilege_name, ',' ORDER BY privilege_name)
                        FROM (
                            SELECT 'INSERT' AS privilege_name FROM connector WHERE pg_catalog.has_table_privilege(oid, '{documentCacheTable}', 'INSERT')
                            UNION ALL SELECT 'UPDATE' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{documentCacheTable}', 'UPDATE')
                            UNION ALL SELECT 'DELETE' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{documentCacheTable}', 'DELETE')
                            UNION ALL SELECT 'TRUNCATE' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{documentCacheTable}', 'TRUNCATE')
                            UNION ALL SELECT 'REFERENCES' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{documentCacheTable}', 'REFERENCES')
                            UNION ALL SELECT 'TRIGGER' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{documentCacheTable}', 'TRIGGER')
                        ) document_cache_write_privileges
                    ),
                    ''
                ) AS document_cache_write_privileges,
                COALESCE(
                    (
                        SELECT string_agg(privilege_name, ',' ORDER BY privilege_name)
                        FROM (
                            SELECT 'SELECT' AS privilege_name FROM connector WHERE pg_catalog.has_table_privilege(oid, '{workTable}', 'SELECT')
                            UNION ALL SELECT 'INSERT' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{workTable}', 'INSERT')
                            UNION ALL SELECT 'UPDATE' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{workTable}', 'UPDATE')
                            UNION ALL SELECT 'DELETE' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{workTable}', 'DELETE')
                            UNION ALL SELECT 'TRUNCATE' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{workTable}', 'TRUNCATE')
                            UNION ALL SELECT 'REFERENCES' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{workTable}', 'REFERENCES')
                            UNION ALL SELECT 'TRIGGER' FROM connector WHERE pg_catalog.has_table_privilege(oid, '{workTable}', 'TRIGGER')
                        ) work_table_privileges
                    ),
                    ''
                ) AS work_table_privileges,
                COALESCE(
                    (
                        SELECT string_agg(table_name, ',' ORDER BY table_name)
                        FROM connector
                        CROSS JOIN dms_tables
                        WHERE dms_tables.table_name NOT IN ('Document', 'DocumentCache', 'CdcHeartbeat', 'DocumentProjectionWork')
                        AND pg_catalog.has_table_privilege(connector.oid, pg_catalog.format('%I.%I', 'dms', dms_tables.table_name), 'SELECT')
                    ),
                    ''
                ) AS extra_dms_select_tables;
            """;
    }

    private static string GrantConnectorPrivilegesSql(CdcProviderSetupRequest request)
    {
        var connectorPrincipalLiteral = EscapeSqlLiteral(request.ConnectorPrincipal.SafePrincipalName.Value);
        var connectorPrincipalIdentifier = _dialect.QuoteIdentifier(
            request.ConnectorPrincipal.SafePrincipalName.Value
        );
        var document = SourceTable(request, CdcSourceTableKind.Document);
        var documentCache = SourceTable(request, CdcSourceTableKind.DocumentCache);
        var heartbeat = SourceTable(request, CdcSourceTableKind.CdcHeartbeat);
        var heartbeatSequence = SourceColumn(heartbeat, "HeartbeatSequence");
        var heartbeatAt = SourceColumn(heartbeat, "HeartbeatAt");

        return $"""
            /* cdc:postgresql:grant-connector-access */
            DO $cdc$
            DECLARE
                _database_name text := current_database();
            BEGIN
                EXECUTE pg_catalog.format('GRANT CONNECT ON DATABASE %I TO %I', _database_name, '{connectorPrincipalLiteral}');
            END;
            $cdc$;

            GRANT USAGE ON SCHEMA {_dialect.QuoteIdentifier(
                DmsTableNames.DmsSchema.Value
            )} TO {connectorPrincipalIdentifier};
            GRANT SELECT ON TABLE {document.EmittedQuotedTableName}, {documentCache.EmittedQuotedTableName}, {heartbeat.EmittedQuotedTableName} TO {connectorPrincipalIdentifier};
            GRANT UPDATE ({heartbeatSequence.EmittedQuotedColumnName}, {heartbeatAt.EmittedQuotedColumnName}) ON TABLE {heartbeat.EmittedQuotedTableName} TO {connectorPrincipalIdentifier};
            """;
    }

    private static CdcProviderSetupStepResult ConnectorPrincipalAccessResult(
        CdcSafeName connectorPrincipal,
        CdcProviderArtifactState state,
        ConnectorPrincipalAccessInspection access
    ) =>
        new(
            artifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.Grant,
                    connectorPrincipal,
                    state,
                    access.ObservedValues
                ),
            ],
            grantInventory: access.GrantInventory,
            diagnostics: access.Diagnostics
        );

    private static IReadOnlyList<string> MissingRequiredConnectorPrivileges(
        bool hasDatabaseConnect,
        bool hasSchemaUsage,
        bool hasDocumentSelect,
        bool hasDocumentCacheSelect,
        bool hasHeartbeatSelect,
        bool hasHeartbeatSequenceUpdate,
        bool hasHeartbeatAtUpdate
    )
    {
        List<string> missing = [];

        if (!hasDatabaseConnect)
        {
            missing.Add("CONNECT:database");
        }

        if (!hasSchemaUsage)
        {
            missing.Add("USAGE:dms");
        }

        if (!hasDocumentSelect)
        {
            missing.Add("SELECT:dms.Document");
        }

        if (!hasDocumentCacheSelect)
        {
            missing.Add("SELECT:dms.DocumentCache");
        }

        if (!hasHeartbeatSelect)
        {
            missing.Add("SELECT:dms.CdcHeartbeat");
        }

        if (!hasHeartbeatSequenceUpdate)
        {
            missing.Add("UPDATE:dms.CdcHeartbeat.HeartbeatSequence");
        }

        if (!hasHeartbeatAtUpdate)
        {
            missing.Add("UPDATE:dms.CdcHeartbeat.HeartbeatAt");
        }

        return missing;
    }

    private static IReadOnlyList<CdcProviderDiagnostic> ConnectorPrincipalAccessDiagnostics(
        CdcSafeName connectorPrincipal,
        bool roleExists,
        bool canLogin,
        bool canReplicate,
        IReadOnlyList<string> disallowedRoleAttributes,
        IReadOnlyList<string> ownership,
        IReadOnlyList<string> missingRequiredPrivileges,
        bool hasHeartbeatIdUpdate,
        IReadOnlyList<string> documentWritePrivileges,
        IReadOnlyList<string> documentCacheWritePrivileges,
        IReadOnlyList<string> workTablePrivileges,
        IReadOnlyList<string> extraDmsSelectTables
    )
    {
        List<CdcProviderDiagnostic> diagnostics = [];

        if (!roleExists)
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_POSTGRESQL_CONNECTOR_ROLE_MISSING",
                    expectedValue: "existing-login-replication-role",
                    observedValue: "missing"
                )
            );
        }

        if (roleExists && (!canLogin || !canReplicate || disallowedRoleAttributes.Count > 0))
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_POSTGRESQL_CONNECTOR_ROLE_ATTRIBUTES_MISMATCH",
                    expectedValue: "LOGIN,REPLICATION,without-elevated-role-attributes",
                    observedValue: string.Join(
                        ";",
                        new[]
                        {
                            canLogin ? null : "LOGIN:missing",
                            canReplicate ? null : "REPLICATION:missing",
                            disallowedRoleAttributes.Count == 0
                                ? null
                                : $"disallowed:{CsvOrNone(disallowedRoleAttributes)}",
                        }.Where(value => value is not null)
                    )
                )
            );
        }

        if (ownership.Count > 0)
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_POSTGRESQL_CONNECTOR_OWNERSHIP_MISMATCH",
                    expectedValue: "no-database-schema-table-or-publication-ownership",
                    observedValue: CsvOrNone(ownership)
                )
            );
        }

        if (missingRequiredPrivileges.Count > 0)
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_POSTGRESQL_CONNECTOR_REQUIRED_GRANTS_MISSING",
                    expectedValue: "connect-schema-usage-source-select-heartbeat-column-update",
                    observedValue: CsvOrNone(missingRequiredPrivileges)
                )
            );
        }

        if (documentWritePrivileges.Count > 0 || documentCacheWritePrivileges.Count > 0)
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_POSTGRESQL_CONNECTOR_SOURCE_WRITE_GRANT_MISMATCH",
                    expectedValue: "no-write-on-dms.Document-or-dms.DocumentCache",
                    observedValue: $"Document={CsvOrNone(documentWritePrivileges)};DocumentCache={CsvOrNone(documentCacheWritePrivileges)}"
                )
            );
        }

        if (hasHeartbeatIdUpdate)
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_POSTGRESQL_CONNECTOR_HEARTBEAT_UPDATE_GRANT_MISMATCH",
                    expectedValue: "UPDATE-only-HeartbeatSequence-and-HeartbeatAt",
                    observedValue: "HeartbeatId"
                )
            );
        }

        if (extraDmsSelectTables.Count > 0)
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_POSTGRESQL_CONNECTOR_EXTRA_DMS_SELECT_GRANT_MISMATCH",
                    expectedValue: "SELECT-only-Document-DocumentCache-CdcHeartbeat",
                    observedValue: CsvOrNone(extraDmsSelectTables)
                )
            );
        }

        if (workTablePrivileges.Count > 0)
        {
            diagnostics.Add(
                new CdcProviderDiagnostic(
                    Code: "CDC_POSTGRESQL_CONNECTOR_WORK_TABLE_GRANT_MISMATCH",
                    Category: CdcProviderDiagnosticCategory.WorkTableGrantViolation,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.ConnectorPrincipal,
                    ArtifactKind: CdcProviderArtifactKind.Grant,
                    SafeName: connectorPrincipal,
                    ExpectedValue: "no-dms.DocumentProjectionWork-privileges",
                    ObservedValue: CsvOrNone(workTablePrivileges),
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                )
            );
        }

        return diagnostics;
    }

    private static CdcProviderDiagnostic ConnectorPrincipalPrivilegeFailure(
        CdcSafeName connectorPrincipal,
        string code,
        string expectedValue,
        string observedValue
    ) =>
        new(
            Code: code,
            Category: CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure,
            Severity: CdcProviderDiagnosticSeverity.Error,
            PrincipalKind: CdcPrincipalKind.ConnectorPrincipal,
            ArtifactKind: CdcProviderArtifactKind.Grant,
            SafeName: connectorPrincipal,
            ExpectedValue: expectedValue,
            ObservedValue: observedValue,
            ProviderErrorClass: null,
            Classification: CdcProviderRetryContinuityClassification.FailClosed
        );

    private static IReadOnlyList<CdcGrantObservation> ConnectorGrantInventory(
        CdcProviderSetupRequest request,
        bool hasDatabaseConnect,
        bool hasSchemaUsage,
        bool hasDocumentSelect,
        bool hasDocumentCacheSelect,
        bool hasHeartbeatSelect,
        bool hasHeartbeatSequenceUpdate,
        bool hasHeartbeatAtUpdate,
        bool hasHeartbeatIdUpdate,
        IReadOnlyList<string> documentWritePrivileges,
        IReadOnlyList<string> documentCacheWritePrivileges,
        IReadOnlyList<string> workTablePrivileges
    )
    {
        var connector = request.ConnectorPrincipal.SafePrincipalName;
        List<CdcGrantObservation> grants = [];

        if (hasDatabaseConnect)
        {
            grants.Add(GrantObservation(connector, new CdcSafeName("database.current"), ["CONNECT"]));
        }

        if (hasSchemaUsage)
        {
            grants.Add(GrantObservation(connector, new CdcSafeName("dms"), ["USAGE"]));
        }

        if (hasDocumentSelect || documentWritePrivileges.Count > 0)
        {
            grants.Add(
                GrantObservation(
                    connector,
                    SafeName(DmsTableNames.Document),
                    Privileges(hasDocumentSelect, documentWritePrivileges)
                )
            );
        }

        if (hasDocumentCacheSelect || documentCacheWritePrivileges.Count > 0)
        {
            grants.Add(
                GrantObservation(
                    connector,
                    SafeName(DmsTableNames.DocumentCache),
                    Privileges(hasDocumentCacheSelect, documentCacheWritePrivileges)
                )
            );
        }

        if (hasHeartbeatSelect)
        {
            grants.Add(GrantObservation(connector, SafeName(DmsTableNames.CdcHeartbeat), ["SELECT"]));
        }

        List<DbColumnName> heartbeatUpdateColumns = [];
        if (hasHeartbeatSequenceUpdate)
        {
            heartbeatUpdateColumns.Add(new DbColumnName("HeartbeatSequence"));
        }

        if (hasHeartbeatAtUpdate)
        {
            heartbeatUpdateColumns.Add(new DbColumnName("HeartbeatAt"));
        }

        if (hasHeartbeatIdUpdate)
        {
            heartbeatUpdateColumns.Add(new DbColumnName("HeartbeatId"));
        }

        if (heartbeatUpdateColumns.Count > 0)
        {
            grants.Add(
                new CdcGrantObservation(
                    CdcPrincipalKind.ConnectorPrincipal,
                    connector,
                    CdcProviderArtifactKind.Grant,
                    SafeName(DmsTableNames.CdcHeartbeat),
                    ["UPDATE"],
                    heartbeatUpdateColumns
                )
            );
        }

        if (workTablePrivileges.Count > 0)
        {
            grants.Add(
                GrantObservation(
                    connector,
                    SafeName(DmsTableNames.DocumentProjectionWork),
                    workTablePrivileges
                )
            );
        }

        return grants;
    }

    private static CdcGrantObservation GrantObservation(
        CdcSafeName connector,
        CdcSafeName objectName,
        IReadOnlyList<string> privileges
    ) =>
        new(
            CdcPrincipalKind.ConnectorPrincipal,
            connector,
            CdcProviderArtifactKind.Grant,
            objectName,
            privileges,
            []
        );

    private static IReadOnlyList<string> Privileges(bool includeSelect, IReadOnlyList<string> writePrivileges)
    {
        List<string> privileges = [];
        if (includeSelect)
        {
            privileges.Add("SELECT");
        }

        privileges.AddRange(writePrivileges);
        return privileges.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

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

    private static CdcProviderSetupStepResult ProviderHistoryUnavailable(
        CdcSafeName replicationSlotName,
        string code,
        string? providerErrorClass
    ) =>
        new(
            artifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    replicationSlotName,
                    CdcProviderArtifactState.Unavailable,
                    new Dictionary<string, string> { ["history"] = "unavailable" }
                ),
            ],
            providerHistoryObservations:
            [
                new CdcProviderHistoryObservation(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    replicationSlotName,
                    new Dictionary<string, string> { ["history"] = "unavailable" },
                    CdcProviderRetryContinuityClassification.SourceHistoryUnknown
                ),
            ],
            diagnostics:
            [
                new CdcProviderDiagnostic(
                    Code: code,
                    Category: CdcProviderDiagnosticCategory.ProviderHistoryUnavailable,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.SetupPrincipal,
                    ArtifactKind: CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    SafeName: replicationSlotName,
                    ExpectedValue: "readable-provider-history",
                    ObservedValue: "unavailable",
                    ProviderErrorClass: providerErrorClass,
                    Classification: CdcProviderRetryContinuityClassification.SourceHistoryUnknown
                ),
            ]
        );

    private static CdcProviderSetupStepResult ReplicationSlotResult(
        CdcSafeName replicationSlotName,
        CdcProviderArtifactState state,
        IReadOnlyDictionary<string, string> observedValues,
        CdcProviderRetryContinuityClassification classification,
        IReadOnlyList<CdcProviderDiagnostic>? diagnostics = null
    ) =>
        new(
            artifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    replicationSlotName,
                    state,
                    observedValues
                ),
            ],
            providerHistoryObservations:
            [
                new CdcProviderHistoryObservation(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    replicationSlotName,
                    observedValues,
                    classification
                ),
            ],
            diagnostics: diagnostics
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

    private static IReadOnlyList<CdcProviderDiagnostic> ReplicationSlotDiagnostics(
        CdcSafeName replicationSlotName,
        bool exactShape,
        bool retainedPositionsReadable,
        bool noLostHistory,
        bool activeAllowed,
        IReadOnlyDictionary<string, string> observedValues
    )
    {
        List<CdcProviderDiagnostic> diagnostics = [];

        if (!exactShape)
        {
            diagnostics.Add(
                new CdcProviderDiagnostic(
                    Code: "CDC_POSTGRESQL_REPLICATION_SLOT_MISMATCH",
                    Category: CdcProviderDiagnosticCategory.ValidationMismatch,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.None,
                    ArtifactKind: CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    SafeName: replicationSlotName,
                    ExpectedValue: "logical-pgoutput-permanent-current-database-slot",
                    ObservedValue: string.Join(
                        ";",
                        observedValues.Select(value => $"{value.Key}={value.Value}")
                    ),
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                )
            );
        }

        if (!retainedPositionsReadable || !noLostHistory)
        {
            diagnostics.Add(
                ProviderHistoryLossEvidence(
                    replicationSlotName,
                    "CDC_POSTGRESQL_REPLICATION_SLOT_HISTORY_LOST",
                    expectedValue: "readable-retained-positions-without-loss",
                    observedValue: string.Join(
                        ";",
                        observedValues.Select(value => $"{value.Key}={value.Value}")
                    )
                )
            );
        }

        if (!activeAllowed)
        {
            diagnostics.Add(
                new CdcProviderDiagnostic(
                    Code: "CDC_POSTGRESQL_REPLICATION_SLOT_HISTORY_UNPROVABLE",
                    Category: CdcProviderDiagnosticCategory.ProviderHistoryUnavailable,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.None,
                    ArtifactKind: CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    SafeName: replicationSlotName,
                    ExpectedValue: "inactive-unadvanced-initial-slot",
                    ObservedValue: string.Join(
                        ";",
                        observedValues.Select(value => $"{value.Key}={value.Value}")
                    ),
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.SourceHistoryUnknown
                )
            );
        }

        return diagnostics;
    }

    private static IReadOnlyList<CdcProviderDiagnostic> InitialReplicationSlotProofDiagnostics(
        CdcProviderSetupRequest request,
        CdcSafeName replicationSlotName,
        ReplicationSlotInspection slot
    )
    {
        var proof = request.PostgresqlInitialReplicationSlotProof;
        if (proof is null)
        {
            return
            [
                InitialSlotProofUnavailable(
                    replicationSlotName,
                    "CDC_POSTGRESQL_REPLICATION_SLOT_INITIAL_PROOF_MISSING",
                    expectedValue: "matching-same-workflow-initial-slot-proof",
                    observedValue: "missing"
                ),
            ];
        }

        List<string> mismatches = [];
        if (!proof.ReplicationSlotName.Equals(replicationSlotName))
        {
            mismatches.Add(
                $"slot_name={SafeText(proof.ReplicationSlotName.Value)};expected={SafeText(replicationSlotName.Value)}"
            );
        }

        if (proof.SourceFingerprint != request.BoundPhysicalSourceFingerprint)
        {
            mismatches.Add(
                $"source_fingerprint={FingerprintValue(proof.SourceFingerprint)};expected={FingerprintValue(request.BoundPhysicalSourceFingerprint)}"
            );
        }

        if (!string.Equals(proof.DatabaseIdentity.Value, slot.DatabaseIdentity, StringComparison.Ordinal))
        {
            mismatches.Add(
                $"database_identity={SafeText(proof.DatabaseIdentity.Value)};expected={SafeText(slot.DatabaseIdentity ?? "<missing>")}"
            );
        }

        var proofRestartReadable = TryParsePostgresqlLsn(proof.RetainedRestartLsn, out var proofRestartLsn);
        var proofConfirmedFlushReadable = TryParsePostgresqlLsn(
            proof.RetainedConfirmedFlushLsn,
            out var proofConfirmedFlushLsn
        );
        var observedRestartReadable = TryParsePostgresqlLsn(slot.RestartLsn, out var observedRestartLsn);
        var observedConfirmedFlushReadable = TryParsePostgresqlLsn(
            slot.ConfirmedFlushLsn,
            out var observedConfirmedFlushLsn
        );
        var retainedPositionsReadable =
            proofRestartReadable
            && proofConfirmedFlushReadable
            && observedRestartReadable
            && observedConfirmedFlushReadable;

        if (!retainedPositionsReadable)
        {
            mismatches.Add("retained_position=unreadable");
        }

        if (mismatches.Count > 0)
        {
            return
            [
                InitialSlotProofUnavailable(
                    replicationSlotName,
                    "CDC_POSTGRESQL_REPLICATION_SLOT_INITIAL_PROOF_MISMATCH",
                    expectedValue: "same-slot-source-database-retained-position-proof",
                    observedValue: string.Join(";", mismatches)
                ),
            ];
        }

        if (observedRestartLsn > proofRestartLsn || observedConfirmedFlushLsn > proofConfirmedFlushLsn)
        {
            return
            [
                ProviderHistoryLossEvidence(
                    replicationSlotName,
                    "CDC_POSTGRESQL_REPLICATION_SLOT_ADVANCED_BEFORE_CONNECTOR_REGISTRATION",
                    expectedValue: "unadvanced-same-workflow-initial-slot",
                    observedValue: $"restart_lsn={SafeText(slot.RestartLsn!)};proved_restart_lsn={SafeText(proof.RetainedRestartLsn)};confirmed_flush_lsn={SafeText(slot.ConfirmedFlushLsn!)};proved_confirmed_flush_lsn={SafeText(proof.RetainedConfirmedFlushLsn)}"
                ),
            ];
        }

        if (observedRestartLsn < proofRestartLsn || observedConfirmedFlushLsn < proofConfirmedFlushLsn)
        {
            return
            [
                InitialSlotProofUnavailable(
                    replicationSlotName,
                    "CDC_POSTGRESQL_REPLICATION_SLOT_INITIAL_PROOF_MISMATCH",
                    expectedValue: "same-workflow-retained-position-not-before-proof",
                    observedValue: $"restart_lsn={SafeText(slot.RestartLsn!)};proved_restart_lsn={SafeText(proof.RetainedRestartLsn)};confirmed_flush_lsn={SafeText(slot.ConfirmedFlushLsn!)};proved_confirmed_flush_lsn={SafeText(proof.RetainedConfirmedFlushLsn)}"
                ),
            ];
        }

        return [];
    }

    private static IReadOnlyDictionary<string, string> AddInitialSlotProofObservedValues(
        CdcProviderSetupRequest request,
        CdcSafeName replicationSlotName,
        ReplicationSlotInspection slot
    )
    {
        Dictionary<string, string> observedValues = new(slot.ObservedValues)
        {
            ["initial_slot_proof"] = "available",
            ["initial_slot_proof_slot_name"] = SafeText(replicationSlotName.Value),
            ["initial_slot_proof_source_fingerprint_version"] = SafeText(
                request.BoundPhysicalSourceFingerprint.Version
            ),
            ["initial_slot_proof_source_fingerprint"] = SafeText(
                request.BoundPhysicalSourceFingerprint.Value
            ),
            ["initial_slot_proof_database_identity"] = SafeText(slot.DatabaseIdentity ?? ""),
            ["initial_slot_proof_restart_lsn"] = SafeText(slot.RestartLsn ?? ""),
            ["initial_slot_proof_confirmed_flush_lsn"] = SafeText(slot.ConfirmedFlushLsn ?? ""),
        };

        return observedValues;
    }

    private static CdcProviderDiagnostic InitialSlotProofUnavailable(
        CdcSafeName replicationSlotName,
        string code,
        string expectedValue,
        string observedValue
    ) =>
        new(
            Code: code,
            Category: CdcProviderDiagnosticCategory.ProviderHistoryUnavailable,
            Severity: CdcProviderDiagnosticSeverity.Error,
            PrincipalKind: CdcPrincipalKind.None,
            ArtifactKind: CdcProviderArtifactKind.PostgresqlReplicationSlot,
            SafeName: replicationSlotName,
            ExpectedValue: expectedValue,
            ObservedValue: observedValue,
            ProviderErrorClass: null,
            Classification: CdcProviderRetryContinuityClassification.SourceHistoryUnknown
        );

    private static bool TryParsePostgresqlLsn(string? value, out ulong lsn)
    {
        lsn = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Replace('_', '/');
        var parts = normalized.Split('/', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        if (
            !uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var high)
            || !uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low)
        )
        {
            return false;
        }

        lsn = ((ulong)high << 32) | low;
        return true;
    }

    private static CdcProviderDiagnostic ProviderHistoryLossEvidence(
        CdcSafeName replicationSlotName,
        string code,
        string expectedValue,
        string observedValue
    ) =>
        new(
            Code: code,
            Category: CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence,
            Severity: CdcProviderDiagnosticSeverity.Error,
            PrincipalKind: CdcPrincipalKind.None,
            ArtifactKind: CdcProviderArtifactKind.PostgresqlReplicationSlot,
            SafeName: replicationSlotName,
            ExpectedValue: expectedValue,
            ObservedValue: observedValue,
            ProviderErrorClass: null,
            Classification: CdcProviderRetryContinuityClassification.SourceHistoryLost
        );

    private static IReadOnlyDictionary<string, string> ReplicaIdentityObservedValues(
        string? relReplicaIdentity
    ) =>
        new Dictionary<string, string>
        {
            ["replica_identity"] = ReplicaIdentityDisplayName(relReplicaIdentity),
            ["required_replica_identity"] = "FULL",
        };

    private static string FingerprintValue(CdcSourceFingerprint fingerprint) =>
        $"{SafeText(fingerprint.Version)}:{SafeText(fingerprint.Value)}";

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

    private static IReadOnlyList<string> ReadCsv(
        IReadOnlyDictionary<string, string?> row,
        string columnName
    ) =>
        ReadRequired(row, columnName)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SafeText)
            .ToArray();

    private static string CsvOrNone(IReadOnlyList<string> values) =>
        values.Count == 0 ? "none" : string.Join(",", values);

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
        IReadOnlyDictionary<string, string> ObservedValues,
        IReadOnlyList<CdcProviderDiagnostic> Diagnostics
    );

    private sealed record ReplicationSlotInspection(
        bool Exists,
        bool IsExactMatch,
        IReadOnlyDictionary<string, string> ObservedValues,
        CdcProviderRetryContinuityClassification Classification,
        IReadOnlyList<CdcProviderDiagnostic> Diagnostics,
        string? DatabaseIdentity,
        string? RestartLsn,
        string? ConfirmedFlushLsn
    );

    private sealed record ConnectorPrincipalAccessInspection(
        bool IsExactMatch,
        bool IsGrantableMissingPrivilege,
        IReadOnlyDictionary<string, string> ObservedValues,
        IReadOnlyList<CdcGrantObservation> GrantInventory,
        IReadOnlyList<CdcProviderDiagnostic> Diagnostics
    );
}
