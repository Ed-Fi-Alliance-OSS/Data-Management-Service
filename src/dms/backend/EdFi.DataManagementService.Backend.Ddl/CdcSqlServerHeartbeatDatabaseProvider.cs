// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

internal sealed class CdcSqlServerHeartbeatDatabaseProvider : ICdcProviderSetupProvider
{
    private static readonly ISqlDialect _dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
    private static readonly CdcSafeName _databaseCdcSafeName = new("sqlserver_database_cdc");
    private static readonly CdcSafeName _captureInstancesSafeName = new("sqlserver_cdc_capture_instances");

    private static readonly IReadOnlyList<CdcSourceTableKind> _captureTableOrder =
    [
        CdcSourceTableKind.DocumentCache,
        CdcSourceTableKind.Document,
        CdcSourceTableKind.CdcHeartbeat,
    ];

    public CdcProvider Provider => CdcProvider.SqlServer;

    public IReadOnlyList<CdcProviderSetupStep> BuildSetupSteps(CdcProviderSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _ =
            request.ArtifactNames.SqlServer
            ?? throw new InvalidOperationException("SQL Server artifact names were not supplied.");

        return
        [
            new CdcProviderSetupStep(
                CdcProviderArtifactKind.SourceFingerprint,
                CdcSourceFingerprintMetadata.SafeArtifactName,
                canCreateInInitialSetup: false,
                ExecuteSourceFingerprintAsync
            ),
            new CdcProviderSetupStep(
                CdcProviderArtifactKind.ProviderHistory,
                _databaseCdcSafeName,
                canCreateInInitialSetup: true,
                ExecuteDatabaseCdcAsync
            ),
            new CdcProviderSetupStep(
                CdcProviderArtifactKind.HeartbeatTable,
                SafeName(DmsTableNames.CdcHeartbeat),
                canCreateInInitialSetup: true,
                ExecuteHeartbeatTableAsync
            ),
            new CdcProviderSetupStep(
                CdcProviderArtifactKind.SourceTable,
                new CdcSafeName("sqlserver_cdc_source_inventory"),
                canCreateInInitialSetup: false,
                ExecuteSourceInventoryAsync
            ),
            new CdcProviderSetupStep(
                CdcProviderArtifactKind.SqlServerCaptureInstance,
                _captureInstancesSafeName,
                canCreateInInitialSetup: true,
                ExecuteCaptureInstancesAsync
            ),
            new CdcProviderSetupStep(
                CdcProviderArtifactKind.Grant,
                request.ConnectorPrincipal.SafePrincipalName,
                canCreateInInitialSetup: true,
                ExecuteConnectorPrincipalAccessAsync
            ),
            new CdcProviderSetupStep(
                CdcProviderArtifactKind.ProviderHistory,
                _databaseCdcSafeName,
                canCreateInInitialSetup: false,
                ExecuteProviderMetadataRefreshAsync
            ),
        ];
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
            .ReadAsync(executor, SourceFingerprintSql, CdcProvider.SqlServer, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<CdcProviderSetupStepResult> ExecuteDatabaseCdcAsync(
        CdcProviderSetupStepContext context,
        CancellationToken cancellationToken
    )
    {
        if (
            !TryGetExecutor(
                context,
                CdcProviderArtifactKind.ProviderHistory,
                out var executor,
                out var failure
            )
        )
        {
            return failure;
        }

        try
        {
            var inspection = await InspectDatabaseCdcAsync(executor, cancellationToken).ConfigureAwait(false);
            var wasEnabledAtStart = inspection.IsCdcEnabled;
            var state = CdcProviderArtifactState.Matched;

            if (!inspection.IsCdcEnabled)
            {
                if (context.Mode == CdcProviderSetupStepMode.ExactMatchOnly)
                {
                    return DatabaseCdcResult(
                        CdcProviderArtifactState.Missing,
                        inspection,
                        wasEnabledAtStart: false,
                        diagnostics: []
                    );
                }

                await executor
                    .ExecuteNonQueryAsync(EnableDatabaseCdcSql, cancellationToken)
                    .ConfigureAwait(false);

                state = CdcProviderArtifactState.Created;
                inspection = await InspectDatabaseCdcAsync(executor, cancellationToken).ConfigureAwait(false);
            }

            var diagnostics = DatabaseCdcDiagnostics(inspection);
            if (diagnostics.Any(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error))
            {
                state = CdcProviderArtifactState.Mismatched;
            }

            return DatabaseCdcResult(state, inspection, wasEnabledAtStart, diagnostics);
        }
        catch (DbException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.ProviderHistory,
                _databaseCdcSafeName,
                exception
            );
        }
        catch (InvalidOperationException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.ProviderHistory,
                _databaseCdcSafeName,
                exception
            );
        }
    }

    private static async Task<CdcProviderSetupStepResult> ExecuteProviderMetadataRefreshAsync(
        CdcProviderSetupStepContext context,
        CancellationToken cancellationToken
    )
    {
        if (
            !TryGetExecutor(
                context,
                CdcProviderArtifactKind.ProviderHistory,
                out var executor,
                out var failure
            )
        )
        {
            return failure;
        }

        try
        {
            var inspection = await InspectDatabaseCdcAsync(executor, cancellationToken).ConfigureAwait(false);
            return DatabaseCdcMetadataRefreshResult(inspection, DatabaseCdcDiagnostics(inspection));
        }
        catch (DbException exception)
        {
            return ProviderMetadataUnavailableResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return ProviderMetadataUnavailableResult(exception);
        }
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
                heartbeatActionQuery: BuildHeartbeatActionQuery(context.Request)
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

            return new CdcProviderSetupStepResult(
                sourceTableInventory: liveInventory,
                expectedMessageKeyColumns: ExpectedMessageKeyColumns()
            );
        }
        catch (DbException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.SourceTable,
                new CdcSafeName("sqlserver_cdc_source_inventory"),
                exception
            );
        }
        catch (InvalidOperationException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.SourceTable,
                new CdcSafeName("sqlserver_cdc_source_inventory"),
                exception
            );
        }
    }

    private static async Task<CdcProviderSetupStepResult> ExecuteCaptureInstancesAsync(
        CdcProviderSetupStepContext context,
        CancellationToken cancellationToken
    )
    {
        if (
            !TryGetExecutor(
                context,
                CdcProviderArtifactKind.SqlServerCaptureInstance,
                out var executor,
                out var failure
            )
        )
        {
            return failure;
        }

        try
        {
            var inspection = await InspectCaptureInstancesAsync(executor, context.Request, cancellationToken)
                .ConfigureAwait(false);
            var missingKinds = inspection
                .ExpectedInstances.Where(capture => !capture.Exists)
                .Select(capture => capture.TableKind)
                .ToArray();

            if (missingKinds.Length > 0)
            {
                if (
                    context.Mode == CdcProviderSetupStepMode.ExactMatchOnly
                    || inspection.HasMismatchedExistingArtifacts
                )
                {
                    return CaptureInstancesResult(inspection, createdKinds: []);
                }

                foreach (var tableKind in _captureTableOrder.Where(missingKinds.Contains))
                {
                    await executor
                        .ExecuteNonQueryAsync(
                            EnableCaptureInstanceSql(context.Request, tableKind),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }

                inspection = await InspectCaptureInstancesAsync(executor, context.Request, cancellationToken)
                    .ConfigureAwait(false);
            }

            return CaptureInstancesResult(inspection, missingKinds);
        }
        catch (DbException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.SqlServerCaptureInstance,
                _captureInstancesSafeName,
                exception
            );
        }
        catch (InvalidOperationException exception)
        {
            return SetupPrincipalFailure(
                CdcProviderArtifactKind.SqlServerCaptureInstance,
                _captureInstancesSafeName,
                exception
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
            $"UPDATE {heartbeat.EmittedQuotedTableName} SET {heartbeatSequence.EmittedQuotedColumnName} = {heartbeatSequence.EmittedQuotedColumnName} + 1, {heartbeatAt.EmittedQuotedColumnName} = sysutcdatetime() WHERE {heartbeatId.EmittedQuotedColumnName} = 1";

        return new CdcHeartbeatActionQuery(sql, Sha256(sql));
    }

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
                        "CDC_SQLSERVER_CONNECTOR_PRIVILEGE_UNAVAILABLE",
                        expectedValue: "readable-connector-privilege-inventory",
                        observedValue: "unavailable"
                    ),
                ]
            );
        }

        var row = rows[0];
        var connectorExists = ReadBool(row, "connector_exists");
        var connectorIsDatabasePrincipal = ReadBool(row, "connector_is_database_principal");
        var gatingRoleExists = ReadBool(row, "gating_role_exists");
        var gatingRoleIsNormalRole = ReadBool(row, "gating_role_is_normal_role");
        var gatingRoleMember = ReadBool(row, "gating_role_member");
        var gatingRoleDirectMembers = ReadCsv(row, "gating_role_direct_members");
        var gatingRoleParentRoles = ReadCsv(row, "gating_role_parent_roles");
        var gatingRoleOwnedObjects = ReadCsv(row, "gating_role_owned_objects");
        var gatingRoleExplicitPermissions = ReadCsv(row, "gating_role_explicit_permissions");
        var expectedCaptureInstancesUsingRole = ReadInt32(row, "expected_capture_instances_using_role");
        var unexpectedCaptureInstancesUsingRole = ReadCsv(row, "unexpected_capture_instances_using_role");
        var disallowedDatabaseRoles = ReadCsv(row, "disallowed_database_roles");
        var disallowedServerRoles = ReadCsv(row, "disallowed_server_roles");
        var ownership = ReadCsv(row, "ownership");
        var hasDatabaseConnect = ReadBool(row, "database_connect");
        var hasDocumentSelect = ReadBool(row, "document_select");
        var hasDocumentCacheSelect = ReadBool(row, "document_cache_select");
        var hasHeartbeatSelect = ReadBool(row, "heartbeat_select");
        var hasHeartbeatSequenceUpdate = ReadBool(row, "heartbeat_sequence_update");
        var hasHeartbeatAtUpdate = ReadBool(row, "heartbeat_at_update");
        var hasHeartbeatIdUpdate = ReadBool(row, "heartbeat_id_update");
        var documentWritePrivileges = ReadCsv(row, "document_write_privileges");
        var documentCacheWritePrivileges = ReadCsv(row, "document_cache_write_privileges");
        var heartbeatWritePrivileges = ReadCsv(row, "heartbeat_write_privileges");
        var workTablePrivileges = ReadCsv(row, "work_table_privileges");
        var extraDmsSelectTables = ReadCsv(row, "extra_dms_select_tables");

        var connectorPrincipal = request.ConnectorPrincipal.SafePrincipalName;
        var gatingRoleName = request.ArtifactNames.SqlServer!.GatingRoleName;
        var missingRequiredPrivileges = MissingRequiredSqlServerConnectorPrivileges(
            hasDatabaseConnect,
            gatingRoleExists,
            gatingRoleMember,
            hasDocumentSelect,
            hasDocumentCacheSelect,
            hasHeartbeatSelect,
            hasHeartbeatSequenceUpdate,
            hasHeartbeatAtUpdate
        );
        var gatingRoleDirectMembershipIsGrantable =
            gatingRoleDirectMembers.Count == 0
            || gatingRoleDirectMembers.SequenceEqual([connectorPrincipal.Value], StringComparer.Ordinal);
        var gatingRoleShapeIsGrantable =
            !gatingRoleExists
            || (
                gatingRoleIsNormalRole
                && gatingRoleDirectMembershipIsGrantable
                && gatingRoleParentRoles.Count == 0
                && gatingRoleOwnedObjects.Count == 0
                && gatingRoleExplicitPermissions.Count == 0
                && expectedCaptureInstancesUsingRole == _captureTableOrder.Count
                && unexpectedCaptureInstancesUsingRole.Count == 0
            );
        var connectorIdentityIsGrantable =
            connectorExists
            && connectorIsDatabasePrincipal
            && disallowedDatabaseRoles.Count == 0
            && disallowedServerRoles.Count == 0
            && ownership.Count == 0;
        var hasForbiddenPrivileges =
            hasHeartbeatIdUpdate
            || documentWritePrivileges.Count > 0
            || documentCacheWritePrivileges.Count > 0
            || heartbeatWritePrivileges.Count > 0
            || workTablePrivileges.Count > 0
            || extraDmsSelectTables.Count > 0;
        var isGrantableMissingPrivilege =
            connectorIdentityIsGrantable
            && gatingRoleShapeIsGrantable
            && !hasForbiddenPrivileges
            && missingRequiredPrivileges.Count > 0;
        var isExactMatch =
            connectorIdentityIsGrantable
            && gatingRoleExists
            && gatingRoleIsNormalRole
            && gatingRoleMember
            && gatingRoleDirectMembers.SequenceEqual([connectorPrincipal.Value], StringComparer.Ordinal)
            && gatingRoleParentRoles.Count == 0
            && gatingRoleOwnedObjects.Count == 0
            && gatingRoleExplicitPermissions.Count == 0
            && expectedCaptureInstancesUsingRole == _captureTableOrder.Count
            && unexpectedCaptureInstancesUsingRole.Count == 0
            && !hasForbiddenPrivileges
            && missingRequiredPrivileges.Count == 0;

        var observedValues = new Dictionary<string, string>
        {
            ["connector_exists"] = connectorExists.ToString(),
            ["connector_is_database_principal"] = connectorIsDatabasePrincipal.ToString(),
            ["gating_role_exists"] = gatingRoleExists.ToString(),
            ["gating_role_is_normal_role"] = gatingRoleIsNormalRole.ToString(),
            ["gating_role_member"] = gatingRoleMember.ToString(),
            ["gating_role_direct_members"] = CsvOrNone(gatingRoleDirectMembers),
            ["gating_role_parent_roles"] = CsvOrNone(gatingRoleParentRoles),
            ["gating_role_owned_objects"] = CsvOrNone(gatingRoleOwnedObjects),
            ["gating_role_explicit_permissions"] = CsvOrNone(gatingRoleExplicitPermissions),
            ["expected_capture_instances_using_role"] = expectedCaptureInstancesUsingRole.ToString(),
            ["unexpected_capture_instances_using_role"] = CsvOrNone(unexpectedCaptureInstancesUsingRole),
            ["disallowed_database_roles"] = CsvOrNone(disallowedDatabaseRoles),
            ["disallowed_server_roles"] = CsvOrNone(disallowedServerRoles),
            ["ownership"] = CsvOrNone(ownership),
            ["missing_required_privileges"] = CsvOrNone(missingRequiredPrivileges),
            ["document_write_privileges"] = CsvOrNone(documentWritePrivileges),
            ["document_cache_write_privileges"] = CsvOrNone(documentCacheWritePrivileges),
            ["heartbeat_write_privileges"] = CsvOrNone(heartbeatWritePrivileges),
            ["heartbeat_id_update"] = hasHeartbeatIdUpdate.ToString(),
            ["work_table_privileges"] = CsvOrNone(workTablePrivileges),
            ["extra_dms_select_tables"] = CsvOrNone(extraDmsSelectTables),
        };

        var diagnostics = ConnectorPrincipalAccessDiagnostics(
            connectorPrincipal,
            gatingRoleName,
            connectorExists,
            connectorIsDatabasePrincipal,
            gatingRoleExists,
            gatingRoleIsNormalRole,
            gatingRoleDirectMembers,
            gatingRoleParentRoles,
            gatingRoleOwnedObjects,
            gatingRoleExplicitPermissions,
            expectedCaptureInstancesUsingRole,
            unexpectedCaptureInstancesUsingRole,
            disallowedDatabaseRoles,
            disallowedServerRoles,
            ownership,
            missingRequiredPrivileges,
            hasHeartbeatIdUpdate,
            documentWritePrivileges,
            documentCacheWritePrivileges,
            heartbeatWritePrivileges,
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
                gatingRoleMember,
                hasDocumentSelect,
                hasDocumentCacheSelect,
                hasHeartbeatSelect,
                hasHeartbeatSequenceUpdate,
                hasHeartbeatAtUpdate,
                hasHeartbeatIdUpdate,
                documentWritePrivileges,
                documentCacheWritePrivileges,
                heartbeatWritePrivileges,
                workTablePrivileges
            ),
            diagnostics
        );
    }

    private static string ConnectorPrincipalAccessSql(CdcProviderSetupRequest request)
    {
        var connectorPrincipal = EscapeSqlLiteral(request.ConnectorPrincipal.SafePrincipalName.Value);
        var gatingRoleName = EscapeSqlLiteral(request.ArtifactNames.SqlServer!.GatingRoleName.Value);
        var documentObjectName = ObjectIdName(SourceTable(request, CdcSourceTableKind.Document).TableName);
        var documentCacheObjectName = ObjectIdName(
            SourceTable(request, CdcSourceTableKind.DocumentCache).TableName
        );
        var heartbeatObjectName = ObjectIdName(
            SourceTable(request, CdcSourceTableKind.CdcHeartbeat).TableName
        );
        var workTableObjectName = ObjectIdName(DmsTableNames.DocumentProjectionWork);
        var expectedCaptureInstances = string.Join(
            ",\n            ",
            _captureTableOrder.Select(kind =>
                $"(N'{EscapeSqlLiteral(request.ArtifactNames.SqlServer.CaptureInstanceNames[kind].Value)}')"
            )
        );

        return $"""
            /* cdc:sqlserver:connector-principal-access */
            DECLARE @connector_name sysname = N'{connectorPrincipal}';
            DECLARE @gating_role_name sysname = N'{gatingRoleName}';
            DECLARE @document_object_id int = OBJECT_ID(N'{documentObjectName}', N'U');
            DECLARE @document_cache_object_id int = OBJECT_ID(N'{documentCacheObjectName}', N'U');
            DECLARE @heartbeat_object_id int = OBJECT_ID(N'{heartbeatObjectName}', N'U');
            DECLARE @work_table_object_id int = OBJECT_ID(N'{workTableObjectName}', N'U');
            DECLARE @heartbeat_sequence_column_id int = COLUMNPROPERTY(@heartbeat_object_id, N'HeartbeatSequence', N'ColumnId');
            DECLARE @heartbeat_at_column_id int = COLUMNPROPERTY(@heartbeat_object_id, N'HeartbeatAt', N'ColumnId');
            DECLARE @heartbeat_id_column_id int = COLUMNPROPERTY(@heartbeat_object_id, N'HeartbeatId', N'ColumnId');

            WITH expected_capture_instances(capture_instance) AS (
                SELECT *
                FROM (VALUES
            {expectedCaptureInstances}
                ) AS expected(capture_instance)
            ),
            connector AS (
                SELECT TOP (1)
                    principal_info.principal_id,
                    principal_info.name,
                    principal_info.type,
                    principal_info.sid
                FROM sys.database_principals principal_info
                WHERE principal_info.name = @connector_name
                AND principal_info.type <> N'R'
            ),
            gating_role AS (
                SELECT TOP (1)
                    principal_info.principal_id,
                    principal_info.name,
                    principal_info.type,
                    principal_info.is_fixed_role
                FROM sys.database_principals principal_info
                WHERE principal_info.name = @gating_role_name
            ),
            public_principal AS (
                SELECT principal_id
                FROM sys.database_principals
                WHERE name = N'public'
            ),
            direct_database_roles AS (
                SELECT
                    database_role.principal_id,
                    database_role.name COLLATE DATABASE_DEFAULT AS name
                FROM connector
                INNER JOIN sys.database_role_members role_member
                    ON role_member.member_principal_id = connector.principal_id
                INNER JOIN sys.database_principals database_role
                    ON database_role.principal_id = role_member.role_principal_id
            ),
            reachable_database_roles(principal_id, name, role_path) AS (
                SELECT
                    direct_database_roles.principal_id,
                    direct_database_roles.name,
                    CONVERT(
                        nvarchar(max),
                        N',' + CONVERT(nvarchar(20), direct_database_roles.principal_id) + N','
                    ) AS role_path
                FROM direct_database_roles
                UNION ALL
                SELECT
                    parent_role.principal_id,
                    parent_role.name COLLATE DATABASE_DEFAULT AS name,
                    CONVERT(
                        nvarchar(max),
                        reachable_database_roles.role_path
                            + CONVERT(nvarchar(20), parent_role.principal_id)
                            + N','
                    ) AS role_path
                FROM reachable_database_roles
                INNER JOIN sys.database_role_members role_member
                    ON role_member.member_principal_id = reachable_database_roles.principal_id
                INNER JOIN sys.database_principals parent_role
                    ON parent_role.principal_id = role_member.role_principal_id
                WHERE CHARINDEX(
                    N',' + CONVERT(nvarchar(20), parent_role.principal_id) + N',',
                    reachable_database_roles.role_path
                ) = 0
            ),
            connector_permission_principals AS (
                SELECT
                    connector.principal_id,
                    CONVERT(nvarchar(300), N'direct') AS source_name
                FROM connector
                UNION
                SELECT
                    public_principal.principal_id,
                    CONVERT(nvarchar(300), N'public') AS source_name
                FROM public_principal
                WHERE EXISTS (SELECT 1 FROM connector)
                UNION
                SELECT DISTINCT
                    reachable_database_roles.principal_id,
                    CONVERT(
                        nvarchar(300),
                        N'role.' + reachable_database_roles.name COLLATE DATABASE_DEFAULT
                    ) AS source_name
                FROM reachable_database_roles
            ),
            connector_permissions AS (
                SELECT
                    permission_info.permission_name,
                    permission_info.class,
                    permission_info.major_id,
                    permission_info.minor_id,
                    permission_info.state,
                    connector_permission_principals.source_name
                FROM connector_permission_principals
                INNER JOIN sys.database_permissions permission_info
                    ON permission_info.grantee_principal_id = connector_permission_principals.principal_id
                    AND permission_info.state IN (N'G', N'W', N'D')
            ),
            direct_connector_permissions AS (
                SELECT
                    permission_info.permission_name,
                    permission_info.class,
                    permission_info.major_id,
                    permission_info.minor_id,
                    permission_info.state
                FROM connector
                INNER JOIN sys.database_permissions permission_info
                    ON permission_info.grantee_principal_id = connector.principal_id
                    AND permission_info.state IN (N'G', N'W')
            ),
            dms_base_tables AS (
                SELECT
                    object_info.object_id,
                    object_info.name COLLATE DATABASE_DEFAULT AS object_name,
                    object_info.schema_id
                FROM sys.objects object_info
                INNER JOIN sys.schemas schema_info
                    ON schema_info.schema_id = object_info.schema_id
                WHERE schema_info.name = N'dms'
                AND object_info.type = N'U'
            ),
            dms_table_columns AS (
                SELECT
                    table_info.object_id,
                    table_info.object_name,
                    table_info.schema_id,
                    column_info.column_id,
                    column_info.name COLLATE DATABASE_DEFAULT AS column_name
                FROM dms_base_tables table_info
                INNER JOIN sys.columns column_info
                    ON column_info.object_id = table_info.object_id
            ),
            dms_object_effective_permissions AS (
                SELECT DISTINCT
                    grant_info.permission_name,
                    table_info.object_id,
                    table_info.object_name,
                    grant_info.source_name
                FROM connector_permissions grant_info
                INNER JOIN dms_base_tables table_info
                    ON (
                        grant_info.class = 0
                        AND grant_info.major_id = 0
                    )
                    OR (
                        grant_info.class = 3
                        AND grant_info.major_id = table_info.schema_id
                    )
                    OR (
                        grant_info.class = 1
                        AND grant_info.major_id = table_info.object_id
                        AND grant_info.minor_id = 0
                    )
                WHERE grant_info.state IN (N'G', N'W')
                AND grant_info.permission_name IN (
                    N'SELECT',
                    N'INSERT',
                    N'UPDATE',
                    N'DELETE',
                    N'ALTER',
                    N'CONTROL',
                    N'TAKE OWNERSHIP',
                    N'REFERENCES'
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM connector_permissions deny_info
                    WHERE deny_info.state = N'D'
                    AND deny_info.permission_name = grant_info.permission_name
                    AND (
                        (
                            deny_info.class = 0
                            AND deny_info.major_id = 0
                        )
                        OR (
                            deny_info.class = 3
                            AND deny_info.major_id = table_info.schema_id
                        )
                        OR (
                            deny_info.class = 1
                            AND deny_info.major_id = table_info.object_id
                            AND deny_info.minor_id = 0
                        )
                    )
                )
            ),
            dms_column_specific_effective_permissions AS (
                SELECT DISTINCT
                    grant_info.permission_name,
                    column_info.object_id,
                    column_info.object_name,
                    column_info.column_id,
                    column_info.column_name,
                    grant_info.source_name
                FROM connector_permissions grant_info
                INNER JOIN dms_table_columns column_info
                    ON grant_info.class = 1
                    AND grant_info.major_id = column_info.object_id
                    AND grant_info.minor_id = column_info.column_id
                WHERE grant_info.state IN (N'G', N'W')
                AND grant_info.permission_name IN (
                    N'SELECT',
                    N'UPDATE',
                    N'REFERENCES'
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM connector_permissions deny_info
                    WHERE deny_info.state = N'D'
                    AND deny_info.permission_name = grant_info.permission_name
                    AND (
                        (
                            deny_info.class = 0
                            AND deny_info.major_id = 0
                        )
                        OR (
                            deny_info.class = 3
                            AND deny_info.major_id = column_info.schema_id
                        )
                        OR (
                            deny_info.class = 1
                            AND deny_info.major_id = column_info.object_id
                            AND deny_info.minor_id IN (0, column_info.column_id)
                        )
                    )
                )
            )
            SELECT
                CONVERT(nvarchar(5), CASE WHEN EXISTS (SELECT 1 FROM connector) THEN 1 ELSE 0 END) AS connector_exists,
                CONVERT(nvarchar(5), CASE WHEN EXISTS (SELECT 1 FROM connector WHERE type <> N'R') THEN 1 ELSE 0 END) AS connector_is_database_principal,
                CONVERT(nvarchar(5), CASE WHEN EXISTS (SELECT 1 FROM gating_role) THEN 1 ELSE 0 END) AS gating_role_exists,
                CONVERT(nvarchar(5), CASE WHEN EXISTS (SELECT 1 FROM gating_role WHERE type = N'R' AND is_fixed_role = 0) THEN 1 ELSE 0 END) AS gating_role_is_normal_role,
                CONVERT(nvarchar(5), CASE WHEN EXISTS (
                    SELECT 1
                    FROM gating_role
                    INNER JOIN connector
                        ON 1 = 1
                    INNER JOIN sys.database_role_members role_member
                        ON role_member.role_principal_id = gating_role.principal_id
                        AND role_member.member_principal_id = connector.principal_id
                ) THEN 1 ELSE 0 END) AS gating_role_member,
                COALESCE((
                    SELECT STRING_AGG(role_member_principal.name, N',') WITHIN GROUP (ORDER BY role_member_principal.name)
                    FROM gating_role
                    INNER JOIN sys.database_role_members role_member
                        ON role_member.role_principal_id = gating_role.principal_id
                    INNER JOIN sys.database_principals role_member_principal
                        ON role_member_principal.principal_id = role_member.member_principal_id
                ), N'') AS gating_role_direct_members,
                COALESCE((
                    SELECT STRING_AGG(parent_role.name, N',') WITHIN GROUP (ORDER BY parent_role.name)
                    FROM gating_role
                    INNER JOIN sys.database_role_members role_member
                        ON role_member.member_principal_id = gating_role.principal_id
                    INNER JOIN sys.database_principals parent_role
                        ON parent_role.principal_id = role_member.role_principal_id
                ), N'') AS gating_role_parent_roles,
                COALESCE((
                    SELECT STRING_AGG(owned_object, N',') WITHIN GROUP (ORDER BY owned_object)
                    FROM (
                        SELECT N'schema:' + schema_info.name AS owned_object
                        FROM gating_role
                        INNER JOIN sys.schemas schema_info
                            ON schema_info.principal_id = gating_role.principal_id
                        UNION ALL
                        SELECT N'object:' + schema_info.name + N'.' + object_info.name
                        FROM gating_role
                        INNER JOIN sys.objects object_info
                            ON object_info.principal_id = gating_role.principal_id
                        INNER JOIN sys.schemas schema_info
                            ON schema_info.schema_id = object_info.schema_id
                    ) ownership
                ), N'') AS gating_role_owned_objects,
                COALESCE((
                    SELECT STRING_AGG(permission_info.permission_name, N',') WITHIN GROUP (ORDER BY permission_info.permission_name)
                    FROM gating_role
                    INNER JOIN sys.database_permissions permission_info
                        ON permission_info.grantee_principal_id = gating_role.principal_id
                        AND permission_info.state IN (N'G', N'W')
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM sys.objects object_info
                        INNER JOIN sys.schemas schema_info
                            ON schema_info.schema_id = object_info.schema_id
                        WHERE object_info.object_id = permission_info.major_id
                        AND permission_info.class = 1
                        AND permission_info.permission_name = N'SELECT'
                        AND schema_info.name = N'cdc'
                    )
                ), N'') AS gating_role_explicit_permissions,
                COALESCE((
                    SELECT CONVERT(nvarchar(20), COUNT_BIG(*))
                    FROM cdc.change_tables capture_info
                    INNER JOIN expected_capture_instances expected
                        ON expected.capture_instance = capture_info.capture_instance
                    WHERE capture_info.role_name = @gating_role_name
                ), N'0') AS expected_capture_instances_using_role,
                COALESCE((
                    SELECT STRING_AGG(capture_info.capture_instance, N',') WITHIN GROUP (ORDER BY capture_info.capture_instance)
                    FROM cdc.change_tables capture_info
                    LEFT JOIN expected_capture_instances expected
                        ON expected.capture_instance = capture_info.capture_instance
                    WHERE capture_info.role_name = @gating_role_name
                    AND expected.capture_instance IS NULL
                ), N'') AS unexpected_capture_instances_using_role,
                COALESCE((
                    SELECT STRING_AGG(disallowed_role.name, N',') WITHIN GROUP (ORDER BY disallowed_role.name)
                    FROM (
                        SELECT DISTINCT reachable_database_roles.name
                        FROM reachable_database_roles
                        WHERE reachable_database_roles.name <> @gating_role_name
                    ) disallowed_role
                ), N'') AS disallowed_database_roles,
                COALESCE((
                    SELECT STRING_AGG(server_role.name, N',') WITHIN GROUP (ORDER BY server_role.name)
                    FROM connector
                    INNER JOIN sys.server_principals server_principal
                        ON server_principal.sid = connector.sid
                    INNER JOIN sys.server_role_members role_member
                        ON role_member.member_principal_id = server_principal.principal_id
                    INNER JOIN sys.server_principals server_role
                        ON server_role.principal_id = role_member.role_principal_id
                    WHERE server_role.name IN (N'sysadmin', N'securityadmin', N'serveradmin', N'dbcreator')
                ), N'') AS disallowed_server_roles,
                COALESCE((
                    SELECT STRING_AGG(owned_object, N',') WITHIN GROUP (ORDER BY owned_object)
                    FROM (
                        SELECT N'schema:' + schema_info.name AS owned_object
                        FROM connector
                        INNER JOIN sys.schemas schema_info
                            ON schema_info.principal_id = connector.principal_id
                        UNION ALL
                        SELECT N'object:' + schema_info.name + N'.' + object_info.name
                        FROM connector
                        INNER JOIN sys.objects object_info
                            ON object_info.principal_id = connector.principal_id
                        INNER JOIN sys.schemas schema_info
                            ON schema_info.schema_id = object_info.schema_id
                    ) ownership
                ), N'') AS ownership,
                CONVERT(nvarchar(5), CASE WHEN EXISTS (
                    SELECT 1
                    FROM connector
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM connector_permissions deny_info
                        WHERE deny_info.state = N'D'
                        AND deny_info.permission_name = N'CONNECT'
                        AND deny_info.class = 0
                        AND deny_info.major_id = 0
                    )
                ) THEN 1 ELSE 0 END) AS database_connect,
                CONVERT(nvarchar(5), CASE WHEN EXISTS (
                    SELECT 1
                    FROM direct_connector_permissions permission_info
                    WHERE permission_info.permission_name = N'SELECT'
                    AND permission_info.class = 1
                    AND permission_info.major_id = @document_object_id
                    AND permission_info.minor_id = 0
                    AND EXISTS (
                        SELECT 1
                        FROM dms_object_effective_permissions effective_permission
                        WHERE effective_permission.permission_name = N'SELECT'
                        AND effective_permission.object_id = @document_object_id
                    )
                ) THEN 1 ELSE 0 END) AS document_select,
                CONVERT(nvarchar(5), CASE WHEN EXISTS (
                    SELECT 1
                    FROM direct_connector_permissions permission_info
                    WHERE permission_info.permission_name = N'SELECT'
                    AND permission_info.class = 1
                    AND permission_info.major_id = @document_cache_object_id
                    AND permission_info.minor_id = 0
                    AND EXISTS (
                        SELECT 1
                        FROM dms_object_effective_permissions effective_permission
                        WHERE effective_permission.permission_name = N'SELECT'
                        AND effective_permission.object_id = @document_cache_object_id
                    )
                ) THEN 1 ELSE 0 END) AS document_cache_select,
                CONVERT(nvarchar(5), CASE WHEN EXISTS (
                    SELECT 1
                    FROM direct_connector_permissions permission_info
                    WHERE permission_info.permission_name = N'SELECT'
                    AND permission_info.class = 1
                    AND permission_info.major_id = @heartbeat_object_id
                    AND permission_info.minor_id = 0
                    AND EXISTS (
                        SELECT 1
                        FROM dms_object_effective_permissions effective_permission
                        WHERE effective_permission.permission_name = N'SELECT'
                        AND effective_permission.object_id = @heartbeat_object_id
                    )
                ) THEN 1 ELSE 0 END) AS heartbeat_select,
                CONVERT(nvarchar(5), CASE WHEN EXISTS (
                    SELECT 1
                    FROM direct_connector_permissions permission_info
                    WHERE permission_info.permission_name = N'UPDATE'
                    AND permission_info.class = 1
                    AND permission_info.major_id = @heartbeat_object_id
                    AND permission_info.minor_id = @heartbeat_sequence_column_id
                    AND EXISTS (
                        SELECT 1
                        FROM dms_column_specific_effective_permissions effective_permission
                        WHERE effective_permission.permission_name = N'UPDATE'
                        AND effective_permission.object_id = @heartbeat_object_id
                        AND effective_permission.column_id = @heartbeat_sequence_column_id
                    )
                ) THEN 1 ELSE 0 END) AS heartbeat_sequence_update,
                CONVERT(nvarchar(5), CASE WHEN EXISTS (
                    SELECT 1
                    FROM direct_connector_permissions permission_info
                    WHERE permission_info.permission_name = N'UPDATE'
                    AND permission_info.class = 1
                    AND permission_info.major_id = @heartbeat_object_id
                    AND permission_info.minor_id = @heartbeat_at_column_id
                    AND EXISTS (
                        SELECT 1
                        FROM dms_column_specific_effective_permissions effective_permission
                        WHERE effective_permission.permission_name = N'UPDATE'
                        AND effective_permission.object_id = @heartbeat_object_id
                        AND effective_permission.column_id = @heartbeat_at_column_id
                    )
                ) THEN 1 ELSE 0 END) AS heartbeat_at_update,
                CONVERT(nvarchar(5), CASE WHEN EXISTS (
                    SELECT 1
                    FROM dms_column_specific_effective_permissions permission_info
                    WHERE permission_info.permission_name = N'UPDATE'
                    AND permission_info.object_id = @heartbeat_object_id
                    AND permission_info.column_id = @heartbeat_id_column_id
                ) OR EXISTS (
                    SELECT 1
                    FROM dms_object_effective_permissions permission_info
                    WHERE permission_info.permission_name = N'UPDATE'
                    AND permission_info.object_id = @heartbeat_object_id
                ) THEN 1 ELSE 0 END) AS heartbeat_id_update,
                COALESCE((
                    SELECT STRING_AGG(permission_info.privilege_source, N',') WITHIN GROUP (ORDER BY permission_info.privilege_source)
                    FROM (
                        SELECT DISTINCT
                            permission_info.permission_name COLLATE DATABASE_DEFAULT
                                + N'.via.'
                                + permission_info.source_name COLLATE DATABASE_DEFAULT AS privilege_source
                        FROM dms_object_effective_permissions permission_info
                        WHERE permission_info.object_id = @document_object_id
                        AND permission_info.permission_name <> N'SELECT'
                        UNION
                        SELECT DISTINCT
                            permission_info.permission_name COLLATE DATABASE_DEFAULT
                                + N'.via.'
                                + permission_info.source_name COLLATE DATABASE_DEFAULT AS privilege_source
                        FROM dms_column_specific_effective_permissions permission_info
                        WHERE permission_info.object_id = @document_object_id
                        AND permission_info.permission_name <> N'SELECT'
                    ) permission_info
                ), N'') AS document_write_privileges,
                COALESCE((
                    SELECT STRING_AGG(permission_info.privilege_source, N',') WITHIN GROUP (ORDER BY permission_info.privilege_source)
                    FROM (
                        SELECT DISTINCT
                            permission_info.permission_name COLLATE DATABASE_DEFAULT
                                + N'.via.'
                                + permission_info.source_name COLLATE DATABASE_DEFAULT AS privilege_source
                        FROM dms_object_effective_permissions permission_info
                        WHERE permission_info.object_id = @document_cache_object_id
                        AND permission_info.permission_name <> N'SELECT'
                        UNION
                        SELECT DISTINCT
                            permission_info.permission_name COLLATE DATABASE_DEFAULT
                                + N'.via.'
                                + permission_info.source_name COLLATE DATABASE_DEFAULT AS privilege_source
                        FROM dms_column_specific_effective_permissions permission_info
                        WHERE permission_info.object_id = @document_cache_object_id
                        AND permission_info.permission_name <> N'SELECT'
                    ) permission_info
                ), N'') AS document_cache_write_privileges,
                COALESCE((
                    SELECT STRING_AGG(permission_info.privilege_source, N',') WITHIN GROUP (ORDER BY permission_info.privilege_source)
                    FROM (
                        SELECT DISTINCT
                            permission_info.permission_name COLLATE DATABASE_DEFAULT
                                + N'.via.'
                                + permission_info.source_name COLLATE DATABASE_DEFAULT AS privilege_source
                        FROM dms_object_effective_permissions permission_info
                        WHERE permission_info.object_id = @heartbeat_object_id
                        AND permission_info.permission_name <> N'SELECT'
                        UNION
                        SELECT DISTINCT
                            permission_info.permission_name COLLATE DATABASE_DEFAULT
                                + N'.via.'
                                + permission_info.source_name COLLATE DATABASE_DEFAULT AS privilege_source
                        FROM dms_column_specific_effective_permissions permission_info
                        WHERE permission_info.object_id = @heartbeat_object_id
                        AND permission_info.permission_name <> N'SELECT'
                        AND NOT (
                            permission_info.permission_name = N'UPDATE'
                            AND permission_info.column_id IN (
                                @heartbeat_sequence_column_id,
                                @heartbeat_at_column_id
                            )
                        )
                    ) permission_info
                ), N'') AS heartbeat_write_privileges,
                COALESCE((
                    SELECT STRING_AGG(permission_info.privilege_source, N',') WITHIN GROUP (ORDER BY permission_info.privilege_source)
                    FROM (
                        SELECT DISTINCT
                            permission_info.permission_name COLLATE DATABASE_DEFAULT
                                + N'.via.'
                                + permission_info.source_name COLLATE DATABASE_DEFAULT AS privilege_source
                        FROM dms_object_effective_permissions permission_info
                        WHERE permission_info.object_id = @work_table_object_id
                        UNION
                        SELECT DISTINCT
                            permission_info.permission_name COLLATE DATABASE_DEFAULT
                                + N'.via.'
                                + permission_info.source_name COLLATE DATABASE_DEFAULT AS privilege_source
                        FROM dms_column_specific_effective_permissions permission_info
                        WHERE permission_info.object_id = @work_table_object_id
                    ) permission_info
                ), N'') AS work_table_privileges,
                COALESCE((
                    SELECT STRING_AGG(permission_info.object_source, N',') WITHIN GROUP (ORDER BY permission_info.object_source)
                    FROM (
                        SELECT DISTINCT
                            object_info.object_name COLLATE DATABASE_DEFAULT
                                + N'.via.'
                                + permission_info.source_name COLLATE DATABASE_DEFAULT AS object_source
                        FROM dms_object_effective_permissions permission_info
                    INNER JOIN dms_base_tables object_info
                        ON object_info.object_id = permission_info.object_id
                        WHERE 1 = 1
                        AND permission_info.permission_name = N'SELECT'
                        AND object_info.object_name NOT IN (N'Document', N'DocumentCache', N'CdcHeartbeat', N'DocumentProjectionWork')
                    ) permission_info
                ), N'') AS extra_dms_select_tables;
            """;
    }

    private static string GrantConnectorPrivilegesSql(CdcProviderSetupRequest request)
    {
        var connectorPrincipal = _dialect.QuoteIdentifier(request.ConnectorPrincipal.SafePrincipalName.Value);
        var connectorPrincipalLiteral = EscapeSqlLiteral(request.ConnectorPrincipal.SafePrincipalName.Value);
        var gatingRole = _dialect.QuoteIdentifier(request.ArtifactNames.SqlServer!.GatingRoleName.Value);
        var gatingRoleLiteral = EscapeSqlLiteral(request.ArtifactNames.SqlServer.GatingRoleName.Value);
        var document = SourceTable(request, CdcSourceTableKind.Document);
        var documentCache = SourceTable(request, CdcSourceTableKind.DocumentCache);
        var heartbeat = SourceTable(request, CdcSourceTableKind.CdcHeartbeat);
        var heartbeatSequence = SourceColumn(heartbeat, "HeartbeatSequence");
        var heartbeatAt = SourceColumn(heartbeat, "HeartbeatAt");

        return $"""
            /* cdc:sqlserver:grant-connector-access */
            IF USER_ID(N'{connectorPrincipalLiteral}') IS NULL
            BEGIN
                THROW 51000, 'CDC SQL Server connector database principal is missing.', 1;
            END;

            IF DATABASE_PRINCIPAL_ID(N'{gatingRoleLiteral}') IS NULL
            BEGIN
                CREATE ROLE {gatingRole};
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.database_role_members role_member
                INNER JOIN sys.database_principals database_role
                    ON database_role.principal_id = role_member.role_principal_id
                INNER JOIN sys.database_principals member_principal
                    ON member_principal.principal_id = role_member.member_principal_id
                WHERE database_role.name = N'{gatingRoleLiteral}'
                AND member_principal.name = N'{connectorPrincipalLiteral}'
            )
            BEGIN
                ALTER ROLE {gatingRole} ADD MEMBER {connectorPrincipal};
            END;

            GRANT CONNECT TO {connectorPrincipal};
            GRANT SELECT ON OBJECT::{document.EmittedQuotedTableName} TO {connectorPrincipal};
            GRANT SELECT ON OBJECT::{documentCache.EmittedQuotedTableName} TO {connectorPrincipal};
            GRANT SELECT ON OBJECT::{heartbeat.EmittedQuotedTableName} TO {connectorPrincipal};
            GRANT UPDATE ({heartbeatSequence.EmittedQuotedColumnName}, {heartbeatAt.EmittedQuotedColumnName}) ON OBJECT::{heartbeat.EmittedQuotedTableName} TO {connectorPrincipal};
            """;
    }

    private const string EnableDatabaseCdcSql = """
        /* cdc:sqlserver:enable-database-cdc */
        EXEC sys.sp_cdc_enable_db;
        """;

    private static async Task<DatabaseCdcInspection> InspectDatabaseCdcAsync(
        ICdcProviderDatabaseExecutor executor,
        CancellationToken cancellationToken
    )
    {
        var stateRows = await executor
            .QueryAsync(DatabaseCdcStateSql, cancellationToken)
            .ConfigureAwait(false);
        if (stateRows.Count == 0)
        {
            throw new InvalidOperationException("SQL Server database CDC state was not returned.");
        }

        var stateRow = stateRows[0];
        var isCdcEnabled = ReadBool(stateRow, "is_cdc_enabled");
        var captureInstanceCount = isCdcEnabled
            ? await ReadCaptureInstanceCountAsync(executor, cancellationToken).ConfigureAwait(false)
            : 0;
        var jobHelpRows =
            isCdcEnabled && captureInstanceCount > 0
                ? await executor.QueryAsync(CdcHelpJobsSql, cancellationToken).ConfigureAwait(false)
                : [];
        var jobRuntimeRows = isCdcEnabled
            ? await executor.QueryAsync(CdcJobRuntimeSql, cancellationToken).ConfigureAwait(false)
            : [];
        var lsnRows = isCdcEnabled
            ? await executor.QueryAsync(CdcRetainedLsnSql, cancellationToken).ConfigureAwait(false)
            : [];

        var jobHelp = jobHelpRows
            .Select(ReadJobHelp)
            .GroupBy(job => job.JobType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key.ToLowerInvariant(), group => group.First());
        var jobRuntime = jobRuntimeRows
            .Select(ReadJobRuntime)
            .GroupBy(job => job.JobType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key.ToLowerInvariant(), group => group.First());

        var retainedLsn =
            lsnRows.Count == 0
                ? new RetainedLsnObservation(RowCount: 0, MinLsn: "", MaxLsn: "")
                : new RetainedLsnObservation(
                    RowCount: ReadInt64(lsnRows[0], "lsn_row_count"),
                    MinLsn: ReadRequired(lsnRows[0], "min_lsn"),
                    MaxLsn: ReadRequired(lsnRows[0], "max_lsn")
                );

        return new DatabaseCdcInspection(
            IsCdcEnabled: isCdcEnabled,
            ReadCommittedSnapshotOn: ReadBool(stateRow, "read_committed_snapshot_on"),
            NestedTriggersValue: ReadRequired(stateRow, "nested_triggers_value"),
            CaptureInstanceCount: captureInstanceCount,
            JobsByType: jobHelp,
            JobRuntimeByType: jobRuntime,
            RetainedLsn: retainedLsn
        );
    }

    private const string DatabaseCdcStateSql = """
        /* cdc:sqlserver:database-cdc-state */
        SELECT
            CONVERT(nvarchar(5), database_info.is_cdc_enabled) AS is_cdc_enabled,
            CONVERT(nvarchar(5), database_info.is_read_committed_snapshot_on) AS read_committed_snapshot_on,
            COALESCE(
                CONVERT(nvarchar(20), (
                    SELECT configuration_info.value_in_use
                    FROM sys.configurations configuration_info
                    WHERE configuration_info.name = N'nested triggers'
                )),
                N'unavailable'
            ) AS nested_triggers_value
        FROM sys.databases database_info
        WHERE database_info.name = DB_NAME();
        """;

    private static async Task<int> ReadCaptureInstanceCountAsync(
        ICdcProviderDatabaseExecutor executor,
        CancellationToken cancellationToken
    )
    {
        var rows = await executor
            .QueryAsync(CdcCaptureInstanceCountSql, cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return 0;
        }

        return ReadInt32(rows[0], "capture_instance_count");
    }

    private const string CdcCaptureInstanceCountSql = """
        /* cdc:sqlserver:capture-instance-count */
        IF OBJECT_ID(N'cdc.change_tables', N'U') IS NULL
        BEGIN
            SELECT CONVERT(nvarchar(20), 0) AS capture_instance_count;
        END
        ELSE
        BEGIN
            SELECT CONVERT(nvarchar(20), COUNT_BIG(*)) AS capture_instance_count
            FROM cdc.change_tables;
        END;
        """;

    private const string CdcHelpJobsSql = """
        /* cdc:sqlserver:help-jobs */
        EXEC sys.sp_cdc_help_jobs;
        """;

    private const string CdcJobRuntimeSql = """
        /* cdc:sqlserver:job-runtime */
        DECLARE @database_name sysname = DB_NAME();
        DECLARE @capture_job_name sysname = N'cdc.' + @database_name + N'_capture';
        DECLARE @cleanup_job_name sysname = N'cdc.' + @database_name + N'_cleanup';

        WITH latest_activity AS (
            SELECT
                activity.job_id,
                activity.start_execution_date,
                activity.stop_execution_date,
                ROW_NUMBER() OVER (
                    PARTITION BY activity.job_id
                    ORDER BY activity.session_id DESC
                ) AS row_number
            FROM msdb.dbo.sysjobactivity activity
        ),
        latest_history AS (
            SELECT
                history.job_id,
                history.run_status,
                ROW_NUMBER() OVER (
                    PARTITION BY history.job_id
                    ORDER BY history.instance_id DESC
                ) AS row_number
            FROM msdb.dbo.sysjobhistory history
            WHERE history.step_id = 0
        )
        SELECT
            CASE
                WHEN job.name = @capture_job_name THEN N'capture'
                WHEN job.name = @cleanup_job_name THEN N'cleanup'
                ELSE N'unknown'
            END AS job_type,
            job.name AS job_name,
            CONVERT(nvarchar(36), job.job_id) AS job_id,
            CONVERT(nvarchar(5), job.enabled) AS enabled,
            CASE
                WHEN latest_activity.start_execution_date IS NOT NULL
                    AND latest_activity.stop_execution_date IS NULL
                    THEN N'true'
                ELSE N'false'
            END AS running,
            COALESCE(CONVERT(nvarchar(10), latest_history.run_status), N'') AS last_run_status
        FROM msdb.dbo.sysjobs job
        LEFT JOIN latest_activity
            ON latest_activity.job_id = job.job_id
            AND latest_activity.row_number = 1
        LEFT JOIN latest_history
            ON latest_history.job_id = job.job_id
            AND latest_history.row_number = 1
        WHERE job.name IN (@capture_job_name, @cleanup_job_name)
        ORDER BY job.name;
        """;

    private const string CdcRetainedLsnSql = """
        /* cdc:sqlserver:retained-lsn */
        IF OBJECT_ID(N'cdc.lsn_time_mapping', N'U') IS NULL
        BEGIN
            SELECT
                CONVERT(nvarchar(20), 0) AS lsn_row_count,
                N'' AS min_lsn,
                N'' AS max_lsn;
        END
        ELSE
        BEGIN
            SELECT
                CONVERT(nvarchar(20), COUNT_BIG(*)) AS lsn_row_count,
                COALESCE(sys.fn_varbintohexstr(MIN(start_lsn)), N'') AS min_lsn,
                COALESCE(sys.fn_varbintohexstr(MAX(start_lsn)), N'') AS max_lsn
            FROM cdc.lsn_time_mapping;
        END;
        """;

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
            /* cdc:sqlserver:create-heartbeat-table */
            IF OBJECT_ID(N'{ObjectIdName(DmsTableNames.CdcHeartbeat)}', N'U') IS NULL
            BEGIN
                CREATE TABLE {heartbeat.EmittedQuotedTableName}
                (
                    {ColumnDefinition("HeartbeatId")},
                    {ColumnDefinition("HeartbeatSequence")},
                    {ColumnDefinition("HeartbeatAt")},
                    CONSTRAINT {_dialect.QuoteIdentifier("PK_CdcHeartbeat")} PRIMARY KEY CLUSTERED ({columns[
                    "HeartbeatId"
                ].EmittedQuotedColumnName}),
                    CONSTRAINT {_dialect.QuoteIdentifier("CK_CdcHeartbeat_Singleton")} CHECK ({columns[
                    "HeartbeatId"
                ].EmittedQuotedColumnName} = 1),
                    CONSTRAINT {_dialect.QuoteIdentifier("CK_CdcHeartbeat_Sequence")} CHECK ({columns[
                    "HeartbeatSequence"
                ].EmittedQuotedColumnName} >= 0)
                );
            END;

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
            IF NOT EXISTS (SELECT 1 FROM {heartbeat.EmittedQuotedTableName} WHERE {heartbeatId.EmittedQuotedColumnName} = 1)
            BEGIN
                INSERT INTO {heartbeat.EmittedQuotedTableName} ({heartbeatId.EmittedQuotedColumnName}, {heartbeatSequence.EmittedQuotedColumnName}, {heartbeatAt.EmittedQuotedColumnName})
                VALUES (1, 0, sysutcdatetime());
            END;
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
            /* cdc:sqlserver:table-exists */
            SELECT CONVERT(nvarchar(5), CASE
                WHEN OBJECT_ID(N'{ObjectIdName(table)}', N'U') IS NULL THEN 0
                ELSE 1
            END) AS table_exists;
            """;

    private const string SourceFingerprintSql = """
        /* cdc:sqlserver:source-fingerprint */
        SELECT CONVERT(nvarchar(36), [SourceIdentity]) AS source_identity
        FROM [dms].[DataStoreIdentity]
        WHERE [DataStoreIdentitySingletonId] = 1;
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
                    $"({index + 1}, N'{EscapeSqlLiteral(table.TableName.Schema.Value)}', N'{EscapeSqlLiteral(table.TableName.Name)}')"
            )
        );

        return $"""
            /* cdc:sqlserver:source-inventory */
            WITH expected_tables(table_order, table_schema, table_name) AS (
                SELECT *
                FROM (VALUES
                {values}
                ) AS expected(table_order, table_schema, table_name)
            )
            SELECT
                schema_info.name AS table_schema,
                table_info.name AS table_name,
                column_info.name AS column_name,
                CONVERT(nvarchar(20), column_info.column_id) AS ordinal,
                CASE
                    WHEN column_info.is_identity = 1 AND type_info.name = N'bigint'
                        THEN N'bigint IDENTITY(1,1)'
                    WHEN type_info.name IN (N'nvarchar', N'nchar')
                        THEN type_info.name + N'(' +
                            CASE
                                WHEN column_info.max_length = -1 THEN N'max'
                                ELSE CONVERT(nvarchar(20), column_info.max_length / 2)
                            END + N')'
                    WHEN type_info.name IN (N'varchar', N'char', N'varbinary', N'binary')
                        THEN type_info.name + N'(' +
                            CASE
                                WHEN column_info.max_length = -1 THEN N'max'
                                ELSE CONVERT(nvarchar(20), column_info.max_length)
                            END + N')'
                    WHEN type_info.name IN (N'decimal', N'numeric')
                        THEN type_info.name + N'(' + CONVERT(nvarchar(20), column_info.precision) + N',' + CONVERT(nvarchar(20), column_info.scale) + N')'
                    WHEN type_info.name IN (N'datetime2', N'datetimeoffset', N'time')
                        THEN type_info.name + N'(' + CONVERT(nvarchar(20), column_info.scale) + N')'
                    ELSE type_info.name
                END AS provider_data_type,
                CONVERT(nvarchar(5), column_info.is_nullable) AS is_nullable
            FROM expected_tables
            INNER JOIN sys.schemas schema_info
                ON schema_info.name = expected_tables.table_schema
            INNER JOIN sys.tables table_info
                ON table_info.schema_id = schema_info.schema_id
                AND table_info.name = expected_tables.table_name
            INNER JOIN sys.columns column_info
                ON column_info.object_id = table_info.object_id
            INNER JOIN sys.types type_info
                ON type_info.user_type_id = column_info.user_type_id
            ORDER BY expected_tables.table_order, column_info.column_id;
            """;
    }

    internal static string EnableCaptureInstanceSql(
        CdcProviderSetupRequest request,
        CdcSourceTableKind tableKind
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var sqlServerNames = request.ArtifactNames.SqlServer!;
        var sourceTable = SourceTable(request, tableKind);
        var captureInstanceName = sqlServerNames.CaptureInstanceNames[tableKind];
        var capturedColumns = string.Join(
            ", ",
            sourceTable.Columns.Select(column => column.EmittedQuotedColumnName)
        );

        return $"""
            /* cdc:sqlserver:enable-capture-instance */
            EXEC sys.sp_cdc_enable_table
                @source_schema = N'{EscapeSqlLiteral(sourceTable.TableName.Schema.Value)}',
                @source_name = N'{EscapeSqlLiteral(sourceTable.TableName.Name)}',
                @capture_instance = N'{EscapeSqlLiteral(captureInstanceName.Value)}',
                @supports_net_changes = 0,
                @role_name = N'{EscapeSqlLiteral(sqlServerNames.GatingRoleName.Value)}',
                @index_name = NULL,
                @captured_column_list = N'{EscapeSqlLiteral(capturedColumns)}',
                @filegroup_name = NULL,
                @allow_partition_switch = 0;
            """;
    }

    private static async Task<SqlServerCaptureInstancesInspection> InspectCaptureInstancesAsync(
        ICdcProviderDatabaseExecutor executor,
        CdcProviderSetupRequest request,
        CancellationToken cancellationToken
    )
    {
        var rows = await executor
            .QueryAsync(CaptureInstancesSql(request), cancellationToken)
            .ConfigureAwait(false);
        var rowsByCaptureInstance = rows.GroupBy(row => ReadRequired(row, "capture_instance"))
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var expectedDefinitions = _captureTableOrder
            .Select(kind => ExpectedCaptureDefinition(request, kind))
            .ToArray();
        var expectedCaptureNames = expectedDefinitions
            .Select(definition => definition.CaptureInstanceName.Value)
            .ToHashSet(StringComparer.Ordinal);

        List<SqlServerCaptureInstanceInspection> expectedInstances = [];
        foreach (var definition in expectedDefinitions)
        {
            expectedInstances.Add(
                rowsByCaptureInstance.TryGetValue(definition.CaptureInstanceName.Value, out var captureRows)
                    ? ReadExpectedCaptureInstance(definition, captureRows)
                    : MissingCaptureInstance(definition)
            );
        }

        List<CdcProviderArtifactObservation> unexpectedArtifacts = [];
        List<CdcProviderDiagnostic> diagnostics = [];
        foreach (
            var unexpectedRows in rowsByCaptureInstance
                .Where(group => !expectedCaptureNames.Contains(group.Key))
                .Select(group => group.Value)
        )
        {
            var unexpected = ReadUnexpectedCaptureInstance(unexpectedRows);
            unexpectedArtifacts.Add(unexpected.Artifact);
            diagnostics.AddRange(unexpected.Diagnostics);
        }

        return new SqlServerCaptureInstancesInspection(expectedInstances, unexpectedArtifacts, diagnostics);
    }

    private static string CaptureInstancesSql(CdcProviderSetupRequest request)
    {
        var expectedValues = string.Join(
            ",\n                    ",
            _captureTableOrder.Select(
                (kind, index) =>
                {
                    var sourceTable = SourceTable(request, kind);
                    var captureInstance = request.ArtifactNames.SqlServer!.CaptureInstanceNames[kind];

                    return $"({index + 1}, N'{CaptureTableKindToken(kind)}', N'{EscapeSqlLiteral(sourceTable.TableName.Schema.Value)}', N'{EscapeSqlLiteral(sourceTable.TableName.Name)}', N'{EscapeSqlLiteral(captureInstance.Value)}')";
                }
            )
        );
        var dmsSchema = EscapeSqlLiteral(DmsTableNames.DmsSchema.Value);
        var workTableSchema = EscapeSqlLiteral(DmsTableNames.DocumentProjectionWork.Schema.Value);
        var workTableName = EscapeSqlLiteral(DmsTableNames.DocumentProjectionWork.Name);

        return $"""
            /* cdc:sqlserver:capture-instances */
            IF OBJECT_ID(N'cdc.change_tables', N'U') IS NULL
            BEGIN
                SELECT
                    CAST(NULL AS nvarchar(128)) AS capture_instance,
                    CAST(NULL AS nvarchar(128)) AS source_schema,
                    CAST(NULL AS nvarchar(128)) AS source_name,
                    CAST(NULL AS nvarchar(128)) AS table_kind,
                    CAST(NULL AS nvarchar(128)) AS expected_capture_instance_for_source,
                    CAST(NULL AS nvarchar(128)) AS expected_source_schema,
                    CAST(NULL AS nvarchar(128)) AS expected_source_name,
                    CAST(NULL AS nvarchar(128)) AS role_name,
                    CAST(NULL AS nvarchar(5)) AS supports_net_changes,
                    CAST(NULL AS nvarchar(5)) AS has_drop_pending,
                    CAST(NULL AS nvarchar(128)) AS index_name,
                    CAST(NULL AS nvarchar(128)) AS filegroup_name,
                    CAST(NULL AS nvarchar(5)) AS partition_switch,
                    CAST(NULL AS nvarchar(5)) AS source_is_partitioned,
                    CAST(NULL AS nvarchar(260)) AS change_table,
                    CAST(NULL AS nvarchar(128)) AS column_name,
                    CAST(NULL AS nvarchar(20)) AS column_ordinal
                WHERE 1 = 0;
            END
            ELSE
            BEGIN
                WITH expected_capture_instances(table_order, table_kind, source_schema, source_name, capture_instance) AS (
                    SELECT *
                    FROM (VALUES
                    {expectedValues}
                    ) AS expected(table_order, table_kind, source_schema, source_name, capture_instance)
                )
                SELECT
                    capture_info.capture_instance,
                    source_schema.name AS source_schema,
                    source_table.name AS source_name,
                    COALESCE(
                        expected_by_instance.table_kind,
                        expected_by_source.table_kind,
                        CASE
                            WHEN source_schema.name = N'{workTableSchema}'
                                AND source_table.name = N'{workTableName}'
                                THEN N'document_projection_work'
                            ELSE N'unexpected'
                        END
                    ) AS table_kind,
                    COALESCE(expected_by_source.capture_instance, N'') AS expected_capture_instance_for_source,
                    COALESCE(expected_by_instance.source_schema, expected_by_source.source_schema, N'') AS expected_source_schema,
                    COALESCE(expected_by_instance.source_name, expected_by_source.source_name, N'') AS expected_source_name,
                    COALESCE(capture_info.role_name, N'') AS role_name,
                    CONVERT(nvarchar(5), capture_info.supports_net_changes) AS supports_net_changes,
                    N'False' AS has_drop_pending,
                    COALESCE(capture_info.index_name, N'') AS index_name,
                    COALESCE(capture_info.filegroup_name, N'') AS filegroup_name,
                    CONVERT(nvarchar(5), capture_info.partition_switch) AS partition_switch,
                    CONVERT(nvarchar(5), CASE WHEN EXISTS (
                        SELECT 1
                        FROM sys.indexes source_index
                        INNER JOIN sys.partition_schemes partition_scheme
                            ON partition_scheme.data_space_id = source_index.data_space_id
                        WHERE source_index.object_id = source_table.object_id
                        AND source_index.index_id IN (0, 1)
                    ) THEN 1 ELSE 0 END) AS source_is_partitioned,
                    OBJECT_SCHEMA_NAME(capture_info.object_id) + N'.' + OBJECT_NAME(capture_info.object_id) AS change_table,
                    COALESCE(captured_column.column_name, N'') AS column_name,
                    COALESCE(CONVERT(nvarchar(20), captured_column.column_ordinal), N'0') AS column_ordinal
                FROM cdc.change_tables capture_info
                INNER JOIN sys.tables source_table
                    ON source_table.object_id = capture_info.source_object_id
                INNER JOIN sys.schemas source_schema
                    ON source_schema.schema_id = source_table.schema_id
                LEFT JOIN cdc.captured_columns captured_column
                    ON captured_column.object_id = capture_info.object_id
                LEFT JOIN expected_capture_instances expected_by_instance
                    ON expected_by_instance.capture_instance = capture_info.capture_instance
                LEFT JOIN expected_capture_instances expected_by_source
                    ON expected_by_source.source_schema = source_schema.name
                    AND expected_by_source.source_name = source_table.name
                WHERE source_schema.name = N'{dmsSchema}'
                ORDER BY
                    COALESCE(expected_by_instance.table_order, expected_by_source.table_order, 1000),
                    capture_info.capture_instance,
                    captured_column.column_ordinal;
            END;
            """;
    }

    private static ExpectedSqlServerCaptureDefinition ExpectedCaptureDefinition(
        CdcProviderSetupRequest request,
        CdcSourceTableKind tableKind
    )
    {
        var sourceTable = SourceTable(request, tableKind);

        return new ExpectedSqlServerCaptureDefinition(
            tableKind,
            sourceTable,
            request.ArtifactNames.SqlServer!.CaptureInstanceNames[tableKind],
            request.ArtifactNames.SqlServer.GatingRoleName
        );
    }

    private static SqlServerCaptureInstanceInspection MissingCaptureInstance(
        ExpectedSqlServerCaptureDefinition definition
    ) =>
        new(
            definition.TableKind,
            definition.CaptureInstanceName,
            Exists: false,
            IsExactMatch: false,
            new Dictionary<string, string>
            {
                ["capture_instance"] = SafeText(definition.CaptureInstanceName.Value),
                ["source_table_kind"] = CaptureTableKindToken(definition.TableKind),
                ["source_object"] = SafeName(definition.ExpectedSourceTable.TableName).Value,
                ["capture_instance_state"] = "missing",
            }
        );

    private static SqlServerCaptureInstanceInspection ReadExpectedCaptureInstance(
        ExpectedSqlServerCaptureDefinition definition,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows
    )
    {
        var first = rows[0];
        var captureInstanceName = ReadRequired(first, "capture_instance");
        var sourceSchema = ReadRequired(first, "source_schema");
        var sourceName = ReadRequired(first, "source_name");
        var roleName = ReadOptional(first, "role_name");
        var supportsNetChanges = ReadBool(first, "supports_net_changes");
        var hasDropPending = ReadBool(first, "has_drop_pending");
        var sourceIndex = ReadOptional(first, "index_name");
        var filegroupName = ReadOptional(first, "filegroup_name");
        var partitionSwitch = ReadBool(first, "partition_switch");
        var sourceIsPartitioned = ReadBool(first, "source_is_partitioned");
        var capturedColumns = CapturedColumnNames(rows);
        var expectedColumns = definition
            .ExpectedSourceTable.Columns.Select(column => column.ColumnName.Value)
            .ToArray();

        var sourceMatches =
            string.Equals(
                sourceSchema,
                definition.ExpectedSourceTable.TableName.Schema.Value,
                StringComparison.Ordinal
            )
            && string.Equals(
                sourceName,
                definition.ExpectedSourceTable.TableName.Name,
                StringComparison.Ordinal
            );
        var captureInstanceMatches = string.Equals(
            captureInstanceName,
            definition.CaptureInstanceName.Value,
            StringComparison.Ordinal
        );
        var roleMatches = string.Equals(roleName, definition.GatingRoleName.Value, StringComparison.Ordinal);
        var sourceIndexMatches = string.IsNullOrWhiteSpace(sourceIndex);
        var partitionSwitchMatches = !partitionSwitch;
        var capturedColumnsMatch = capturedColumns.SequenceEqual(expectedColumns, StringComparer.Ordinal);

        return new SqlServerCaptureInstanceInspection(
            definition.TableKind,
            definition.CaptureInstanceName,
            Exists: true,
            sourceMatches
                && captureInstanceMatches
                && roleMatches
                && !supportsNetChanges
                && !hasDropPending
                && sourceIndexMatches
                && string.IsNullOrWhiteSpace(filegroupName)
                && partitionSwitchMatches
                && capturedColumnsMatch,
            CaptureInstanceObservedValues(
                captureInstanceName,
                definition,
                sourceSchema,
                sourceName,
                roleName,
                supportsNetChanges,
                hasDropPending,
                sourceIndex,
                filegroupName,
                partitionSwitch,
                sourceIsPartitioned,
                ReadOptional(first, "change_table"),
                capturedColumns,
                expectedColumns
            )
        );
    }

    private static UnexpectedSqlServerCaptureInstance ReadUnexpectedCaptureInstance(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows
    )
    {
        var first = rows[0];
        var captureInstanceName = ReadRequired(first, "capture_instance");
        var sourceSchema = ReadRequired(first, "source_schema");
        var sourceName = ReadRequired(first, "source_name");
        var tableKind = ReadRequired(first, "table_kind");
        var capturedColumns = CapturedColumnNames(rows);
        var safeName = new CdcSafeName(SafeText(captureInstanceName));
        var observedValues = new Dictionary<string, string>
        {
            ["capture_instance"] = SafeText(captureInstanceName),
            ["source_table_kind"] = SafeText(tableKind),
            ["source_object"] = SafeText($"{sourceSchema}.{sourceName}"),
            ["role_name"] = SafeText(ReadOptional(first, "role_name")),
            ["supports_net_changes"] = ReadBool(first, "supports_net_changes").ToString(),
            ["source_index"] = EmptyAsNone(ReadOptional(first, "index_name")),
            ["filegroup_name"] = EmptyAsNone(ReadOptional(first, "filegroup_name")),
            ["partition_switch"] = ReadBool(first, "partition_switch").ToString(),
            ["source_is_partitioned"] = ReadBool(first, "source_is_partitioned").ToString(),
            ["change_table"] = SafeText(ReadOptional(first, "change_table")),
            ["captured_columns"] = CsvOrNone(capturedColumns),
        };

        var artifact = new CdcProviderArtifactObservation(
            CdcProviderArtifactKind.SqlServerCaptureInstance,
            safeName,
            CdcProviderArtifactState.Mismatched,
            observedValues
        );

        if (!string.Equals(tableKind, "document_projection_work", StringComparison.Ordinal))
        {
            return new UnexpectedSqlServerCaptureInstance(
                artifact,
                [
                    new CdcProviderDiagnostic(
                        Code: "CDC_SQLSERVER_UNEXPECTED_DMS_CAPTURE_INSTANCE",
                        Category: CdcProviderDiagnosticCategory.ValidationMismatch,
                        Severity: CdcProviderDiagnosticSeverity.Error,
                        PrincipalKind: CdcPrincipalKind.None,
                        ArtifactKind: CdcProviderArtifactKind.SqlServerCaptureInstance,
                        SafeName: safeName,
                        ExpectedValue: "only-dms.DocumentCache-dms.Document-dms.CdcHeartbeat-captured",
                        ObservedValue: SafeText($"{sourceSchema}.{sourceName}:capture:{captureInstanceName}"),
                        ProviderErrorClass: null,
                        Classification: CdcProviderRetryContinuityClassification.FailClosed
                    ),
                ]
            );
        }

        return new UnexpectedSqlServerCaptureInstance(
            artifact,
            [
                new CdcProviderDiagnostic(
                    Code: "CDC_SQLSERVER_WORK_TABLE_CAPTURE_FORBIDDEN",
                    Category: CdcProviderDiagnosticCategory.WorkTableCaptureViolation,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.None,
                    ArtifactKind: CdcProviderArtifactKind.SqlServerCaptureInstance,
                    SafeName: safeName,
                    ExpectedValue: "dms.DocumentProjectionWork-not-captured",
                    ObservedValue: "captured",
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                ),
            ]
        );
    }

    private static CdcProviderSetupStepResult CaptureInstancesResult(
        SqlServerCaptureInstancesInspection inspection,
        IReadOnlyCollection<CdcSourceTableKind> createdKinds
    )
    {
        var created = createdKinds.ToHashSet();
        var artifactInventory = inspection
            .ExpectedInstances.Select(capture => new CdcProviderArtifactObservation(
                CdcProviderArtifactKind.SqlServerCaptureInstance,
                capture.CaptureInstanceName,
                CaptureInstanceState(capture, created),
                capture.ObservedValues
            ))
            .Concat(inspection.UnexpectedArtifacts)
            .ToArray();

        return new CdcProviderSetupStepResult(
            artifactInventory: artifactInventory,
            providerHistoryObservations: artifactInventory
                .Select(observation => new CdcProviderHistoryObservation(
                    observation.ArtifactKind,
                    observation.SafeArtifactName,
                    observation.SafeObservedValues,
                    observation.State is CdcProviderArtifactState.Created or CdcProviderArtifactState.Matched
                        ? CdcProviderRetryContinuityClassification.None
                        : CdcProviderRetryContinuityClassification.FailClosed
                ))
                .ToArray(),
            diagnostics: inspection.Diagnostics
        );
    }

    private static CdcProviderArtifactState CaptureInstanceState(
        SqlServerCaptureInstanceInspection capture,
        HashSet<CdcSourceTableKind> createdKinds
    )
    {
        if (!capture.Exists)
        {
            return CdcProviderArtifactState.Missing;
        }

        if (createdKinds.Contains(capture.TableKind))
        {
            return CdcProviderArtifactState.Created;
        }

        if (!capture.IsExactMatch)
        {
            return CdcProviderArtifactState.Mismatched;
        }

        return CdcProviderArtifactState.Matched;
    }

    private static IReadOnlyList<string> CapturedColumnNames(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows
    )
    {
        List<(string ColumnName, int Ordinal)> capturedColumns = [];
        foreach (var row in rows)
        {
            var columnName = ReadOptional(row, "column_name");
            if (string.IsNullOrWhiteSpace(columnName))
            {
                continue;
            }

            capturedColumns.Add((columnName, ReadInt32(row, "column_ordinal")));
        }

        return capturedColumns
            .OrderBy(column => column.Ordinal)
            .Select(column => column.ColumnName)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> CaptureInstanceObservedValues(
        string captureInstanceName,
        ExpectedSqlServerCaptureDefinition definition,
        string sourceSchema,
        string sourceName,
        string roleName,
        bool supportsNetChanges,
        bool hasDropPending,
        string sourceIndex,
        string filegroupName,
        bool partitionSwitch,
        bool sourceIsPartitioned,
        string changeTable,
        IReadOnlyList<string> capturedColumns,
        IReadOnlyList<string> expectedColumns
    ) =>
        new Dictionary<string, string>
        {
            ["capture_instance"] = SafeText(captureInstanceName),
            ["expected_capture_instance"] = SafeText(definition.CaptureInstanceName.Value),
            ["source_table_kind"] = CaptureTableKindToken(definition.TableKind),
            ["source_object"] = SafeText($"{sourceSchema}.{sourceName}"),
            ["expected_source_object"] = SafeName(definition.ExpectedSourceTable.TableName).Value,
            ["role_name"] = EmptyAsNone(roleName),
            ["expected_role_name"] = SafeText(definition.GatingRoleName.Value),
            ["supports_net_changes"] = supportsNetChanges.ToString(),
            ["expected_supports_net_changes"] = "False",
            ["has_drop_pending"] = hasDropPending.ToString(),
            ["source_index"] = EmptyAsNone(sourceIndex),
            ["expected_source_index"] = "none",
            ["filegroup_name"] = EmptyAsNone(filegroupName),
            ["expected_filegroup_name"] = "none",
            ["partition_switch"] = partitionSwitch.ToString(),
            ["expected_partition_switch"] = "disabled",
            ["source_is_partitioned"] = sourceIsPartitioned.ToString(),
            ["change_table"] = SafeText(changeTable),
            ["captured_columns"] = CsvOrNone(capturedColumns),
            ["expected_captured_columns"] = CsvOrNone(expectedColumns),
            ["captured_column_count"] = capturedColumns.Count.ToString(),
            ["heartbeat_capture_visible"] = (
                definition.TableKind == CdcSourceTableKind.CdcHeartbeat
            ).ToString(),
        };

    private static string CaptureTableKindToken(CdcSourceTableKind tableKind) =>
        tableKind switch
        {
            CdcSourceTableKind.Document => "document",
            CdcSourceTableKind.DocumentCache => "document_cache",
            CdcSourceTableKind.CdcHeartbeat => "cdc_heartbeat",
            _ => throw new ArgumentOutOfRangeException(
                nameof(tableKind),
                tableKind,
                "Unsupported CDC source table kind."
            ),
        };

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
        /* cdc:sqlserver:heartbeat-shape */
        SELECT
            CONVERT(nvarchar(5), CASE WHEN EXISTS (
                SELECT 1
                FROM sys.key_constraints constraint_info
                INNER JOIN sys.tables table_info
                    ON table_info.object_id = constraint_info.parent_object_id
                INNER JOIN sys.schemas schema_info
                    ON schema_info.schema_id = table_info.schema_id
                WHERE schema_info.name = N'dms'
                AND table_info.name = N'CdcHeartbeat'
                AND constraint_info.name = N'PK_CdcHeartbeat'
                AND constraint_info.type = N'PK'
                AND (
                    SELECT STRING_AGG(column_info.name, N',') WITHIN GROUP (ORDER BY index_column.key_ordinal)
                    FROM sys.index_columns index_column
                    INNER JOIN sys.columns column_info
                        ON column_info.object_id = index_column.object_id
                        AND column_info.column_id = index_column.column_id
                    WHERE index_column.object_id = constraint_info.parent_object_id
                    AND index_column.index_id = constraint_info.unique_index_id
                ) = N'HeartbeatId'
            ) THEN 1 ELSE 0 END) AS primary_key_matches,
            CONVERT(nvarchar(5), CASE WHEN EXISTS (
                SELECT 1
                FROM sys.check_constraints constraint_info
                INNER JOIN sys.tables table_info
                    ON table_info.object_id = constraint_info.parent_object_id
                INNER JOIN sys.schemas schema_info
                    ON schema_info.schema_id = table_info.schema_id
                WHERE schema_info.name = N'dms'
                AND table_info.name = N'CdcHeartbeat'
                AND constraint_info.name = N'CK_CdcHeartbeat_Singleton'
                AND CHARINDEX(N'[HeartbeatId]', constraint_info.definition) > 0
                AND CHARINDEX(N'(1)', constraint_info.definition) > 0
            ) THEN 1 ELSE 0 END) AS singleton_check_matches,
            CONVERT(nvarchar(5), CASE WHEN EXISTS (
                SELECT 1
                FROM sys.check_constraints constraint_info
                INNER JOIN sys.tables table_info
                    ON table_info.object_id = constraint_info.parent_object_id
                INNER JOIN sys.schemas schema_info
                    ON schema_info.schema_id = table_info.schema_id
                WHERE schema_info.name = N'dms'
                AND table_info.name = N'CdcHeartbeat'
                AND constraint_info.name = N'CK_CdcHeartbeat_Sequence'
                AND CHARINDEX(N'[HeartbeatSequence]', constraint_info.definition) > 0
                AND CHARINDEX(N'(0)', constraint_info.definition) > 0
            ) THEN 1 ELSE 0 END) AS sequence_check_matches;
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
        /* cdc:sqlserver:heartbeat-singleton */
        SELECT
            CONVERT(nvarchar(20), COUNT_BIG(*)) AS row_count,
            CONVERT(nvarchar(20), COALESCE(SUM(CASE WHEN [HeartbeatId] = 1 THEN 1 ELSE 0 END), 0)) AS singleton_row_count,
            CONVERT(nvarchar(20), COALESCE(SUM(CASE WHEN [HeartbeatId] <> 1 THEN 1 ELSE 0 END), 0)) AS extra_row_count,
            CONVERT(nvarchar(20), COALESCE(MAX(CASE WHEN [HeartbeatId] = 1 THEN [HeartbeatSequence] END), -1)) AS heartbeat_sequence
        FROM [dms].[CdcHeartbeat];
        """;

    private static IReadOnlyList<CdcExpectedMessageKeyColumns> ExpectedMessageKeyColumns() =>
        [
            new CdcExpectedMessageKeyColumns(CdcSourceTableKind.Document, [new DbColumnName("DocumentUuid")]),
            new CdcExpectedMessageKeyColumns(
                CdcSourceTableKind.DocumentCache,
                [new DbColumnName("DocumentUuid")]
            ),
        ];

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

    private static IReadOnlyList<string> MissingRequiredSqlServerConnectorPrivileges(
        bool hasDatabaseConnect,
        bool gatingRoleExists,
        bool gatingRoleMember,
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

        if (!gatingRoleExists)
        {
            missing.Add("ROLE:gating-role");
        }

        if (!gatingRoleMember)
        {
            missing.Add("MEMBER:gating-role");
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
        CdcSafeName gatingRoleName,
        bool connectorExists,
        bool connectorIsDatabasePrincipal,
        bool gatingRoleExists,
        bool gatingRoleIsNormalRole,
        IReadOnlyList<string> gatingRoleDirectMembers,
        IReadOnlyList<string> gatingRoleParentRoles,
        IReadOnlyList<string> gatingRoleOwnedObjects,
        IReadOnlyList<string> gatingRoleExplicitPermissions,
        int expectedCaptureInstancesUsingRole,
        IReadOnlyList<string> unexpectedCaptureInstancesUsingRole,
        IReadOnlyList<string> disallowedDatabaseRoles,
        IReadOnlyList<string> disallowedServerRoles,
        IReadOnlyList<string> ownership,
        IReadOnlyList<string> missingRequiredPrivileges,
        bool hasHeartbeatIdUpdate,
        IReadOnlyList<string> documentWritePrivileges,
        IReadOnlyList<string> documentCacheWritePrivileges,
        IReadOnlyList<string> heartbeatWritePrivileges,
        IReadOnlyList<string> workTablePrivileges,
        IReadOnlyList<string> extraDmsSelectTables
    )
    {
        List<CdcProviderDiagnostic> diagnostics = [];

        if (!connectorExists || !connectorIsDatabasePrincipal)
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_SQLSERVER_CONNECTOR_USER_MISSING",
                    expectedValue: "existing-database-principal",
                    observedValue: connectorExists ? "not-database-user" : "missing"
                )
            );
        }

        var gatingRoleDirectMemberMismatch =
            gatingRoleExists
            && gatingRoleDirectMembers.Count > 0
            && !gatingRoleDirectMembers.SequenceEqual([connectorPrincipal.Value], StringComparer.Ordinal);
        if (
            gatingRoleExists
            && (
                !gatingRoleIsNormalRole
                || gatingRoleDirectMemberMismatch
                || gatingRoleParentRoles.Count > 0
                || gatingRoleOwnedObjects.Count > 0
                || gatingRoleExplicitPermissions.Count > 0
                || expectedCaptureInstancesUsingRole != _captureTableOrder.Count
                || unexpectedCaptureInstancesUsingRole.Count > 0
            )
        )
        {
            diagnostics.Add(
                new CdcProviderDiagnostic(
                    Code: "CDC_SQLSERVER_GATING_ROLE_MISMATCH",
                    Category: CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.ConnectorPrincipal,
                    ArtifactKind: CdcProviderArtifactKind.SqlServerGatingRole,
                    SafeName: gatingRoleName,
                    ExpectedValue: "normal-role-exact-connector-member-no-ownership-no-permissions-three-captures",
                    ObservedValue: string.Join(
                        ";",
                        new[]
                        {
                            gatingRoleIsNormalRole ? null : "not-normal-role",
                            gatingRoleDirectMemberMismatch
                                ? $"members:{CsvOrNone(gatingRoleDirectMembers)}"
                                : null,
                            gatingRoleParentRoles.Count == 0
                                ? null
                                : $"parent_roles:{CsvOrNone(gatingRoleParentRoles)}",
                            gatingRoleOwnedObjects.Count == 0
                                ? null
                                : $"ownership:{CsvOrNone(gatingRoleOwnedObjects)}",
                            gatingRoleExplicitPermissions.Count == 0
                                ? null
                                : $"permissions:{CsvOrNone(gatingRoleExplicitPermissions)}",
                            expectedCaptureInstancesUsingRole == _captureTableOrder.Count
                                ? null
                                : $"expected_capture_count:{expectedCaptureInstancesUsingRole}",
                            unexpectedCaptureInstancesUsingRole.Count == 0
                                ? null
                                : $"unexpected_captures:{CsvOrNone(unexpectedCaptureInstancesUsingRole)}",
                        }.Where(value => value is not null)
                    ),
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                )
            );
        }

        if (disallowedDatabaseRoles.Count > 0 || disallowedServerRoles.Count > 0)
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_SQLSERVER_CONNECTOR_ELEVATED_MEMBERSHIP_MISMATCH",
                    expectedValue: "no-disallowed-database-or-server-role-membership",
                    observedValue: $"database={CsvOrNone(disallowedDatabaseRoles)};server={CsvOrNone(disallowedServerRoles)}"
                )
            );
        }

        if (ownership.Count > 0)
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_SQLSERVER_CONNECTOR_OWNERSHIP_MISMATCH",
                    expectedValue: "no-schema-or-object-ownership",
                    observedValue: CsvOrNone(ownership)
                )
            );
        }

        if (missingRequiredPrivileges.Count > 0)
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_SQLSERVER_CONNECTOR_REQUIRED_GRANTS_MISSING",
                    expectedValue: "connect-gating-role-source-select-heartbeat-column-update",
                    observedValue: CsvOrNone(missingRequiredPrivileges)
                )
            );
        }

        if (documentWritePrivileges.Count > 0 || documentCacheWritePrivileges.Count > 0)
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_SQLSERVER_CONNECTOR_SOURCE_WRITE_GRANT_MISMATCH",
                    expectedValue: "no-write-on-dms.Document-or-dms.DocumentCache",
                    observedValue: $"Document={CsvOrNone(documentWritePrivileges)};DocumentCache={CsvOrNone(documentCacheWritePrivileges)}"
                )
            );
        }

        if (hasHeartbeatIdUpdate || heartbeatWritePrivileges.Count > 0)
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_SQLSERVER_CONNECTOR_HEARTBEAT_UPDATE_GRANT_MISMATCH",
                    expectedValue: "UPDATE-only-HeartbeatSequence-and-HeartbeatAt",
                    observedValue: heartbeatWritePrivileges.Count == 0
                        ? "HeartbeatId"
                        : CsvOrNone(heartbeatWritePrivileges)
                )
            );
        }

        if (extraDmsSelectTables.Count > 0)
        {
            diagnostics.Add(
                ConnectorPrincipalPrivilegeFailure(
                    connectorPrincipal,
                    "CDC_SQLSERVER_CONNECTOR_EXTRA_DMS_SELECT_GRANT_MISMATCH",
                    expectedValue: "SELECT-only-Document-DocumentCache-CdcHeartbeat",
                    observedValue: CsvOrNone(extraDmsSelectTables)
                )
            );
        }

        if (workTablePrivileges.Count > 0)
        {
            diagnostics.Add(
                new CdcProviderDiagnostic(
                    Code: "CDC_SQLSERVER_CONNECTOR_WORK_TABLE_GRANT_MISMATCH",
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
        bool hasGatingRoleMembership,
        bool hasDocumentSelect,
        bool hasDocumentCacheSelect,
        bool hasHeartbeatSelect,
        bool hasHeartbeatSequenceUpdate,
        bool hasHeartbeatAtUpdate,
        bool hasHeartbeatIdUpdate,
        IReadOnlyList<string> documentWritePrivileges,
        IReadOnlyList<string> documentCacheWritePrivileges,
        IReadOnlyList<string> heartbeatWritePrivileges,
        IReadOnlyList<string> workTablePrivileges
    )
    {
        var connector = request.ConnectorPrincipal.SafePrincipalName;
        List<CdcGrantObservation> grants = [];

        if (hasDatabaseConnect)
        {
            grants.Add(GrantObservation(connector, new CdcSafeName("database.current"), ["CONNECT"]));
        }

        if (hasGatingRoleMembership)
        {
            grants.Add(
                GrantObservation(
                    connector,
                    new CdcSafeName(
                        $"role.{SafeText(request.ArtifactNames.SqlServer!.GatingRoleName.Value)}"
                    ),
                    ["MEMBER"]
                )
            );
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

        var heartbeatPrivileges = Privileges(hasHeartbeatSelect, heartbeatWritePrivileges);
        if (heartbeatPrivileges.Count > 0)
        {
            grants.Add(
                GrantObservation(connector, SafeName(DmsTableNames.CdcHeartbeat), heartbeatPrivileges)
            );
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
                    PrivilegeNames(workTablePrivileges)
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

        privileges.AddRange(PrivilegeNames(writePrivileges));
        return privileges;
    }

    private static IReadOnlyList<string> PrivilegeNames(IReadOnlyList<string> privileges) =>
        privileges.Select(PrivilegeName).Distinct(StringComparer.Ordinal).ToArray();

    private static string PrivilegeName(string privilege)
    {
        var separatorIndex = privilege.IndexOf(".via.", StringComparison.Ordinal);
        return separatorIndex < 0 ? privilege : privilege[..separatorIndex];
    }

    private static CdcProviderSetupStepResult DatabaseCdcResult(
        CdcProviderArtifactState state,
        DatabaseCdcInspection inspection,
        bool wasEnabledAtStart,
        IReadOnlyList<CdcProviderDiagnostic> diagnostics
    )
    {
        var observedValues = DatabaseCdcObservedValues(inspection, wasEnabledAtStart);
        var classification =
            diagnostics
                .FirstOrDefault(diagnostic =>
                    diagnostic.Severity == CdcProviderDiagnosticSeverity.Error
                    && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryUnavailable
                )
                ?.Classification
            ?? CdcProviderRetryContinuityClassification.None;

        return new CdcProviderSetupStepResult(
            artifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.ProviderHistory,
                    _databaseCdcSafeName,
                    state,
                    observedValues
                ),
            ],
            providerHistoryObservations:
            [
                new CdcProviderHistoryObservation(
                    CdcProviderArtifactKind.ProviderHistory,
                    _databaseCdcSafeName,
                    observedValues,
                    classification
                ),
            ],
            diagnostics: diagnostics
        );
    }

    private static CdcProviderSetupStepResult DatabaseCdcMetadataRefreshResult(
        DatabaseCdcInspection inspection,
        IReadOnlyList<CdcProviderDiagnostic> diagnostics
    )
    {
        var observedValues = DatabaseCdcObservedValues(
            inspection,
            wasEnabledAtStart: inspection.IsCdcEnabled
        );
        var classification =
            diagnostics
                .FirstOrDefault(diagnostic =>
                    diagnostic.Severity == CdcProviderDiagnosticSeverity.Error
                    && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryUnavailable
                )
                ?.Classification
            ?? CdcProviderRetryContinuityClassification.None;

        return new CdcProviderSetupStepResult(
            providerHistoryObservations:
            [
                new CdcProviderHistoryObservation(
                    CdcProviderArtifactKind.ProviderHistory,
                    _databaseCdcSafeName,
                    observedValues,
                    classification
                ),
            ],
            diagnostics: diagnostics
        );
    }

    private static CdcProviderSetupStepResult ProviderMetadataUnavailableResult(Exception exception) =>
        new(
            providerHistoryObservations:
            [
                new CdcProviderHistoryObservation(
                    CdcProviderArtifactKind.ProviderHistory,
                    _databaseCdcSafeName,
                    new Dictionary<string, string> { ["history"] = "unavailable" },
                    CdcProviderRetryContinuityClassification.SourceHistoryUnknown
                ),
            ],
            diagnostics:
            [
                new CdcProviderDiagnostic(
                    Code: "CDC_SQLSERVER_PROVIDER_METADATA_UNAVAILABLE",
                    Category: CdcProviderDiagnosticCategory.ProviderHistoryUnavailable,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.SetupPrincipal,
                    ArtifactKind: CdcProviderArtifactKind.ProviderHistory,
                    SafeName: _databaseCdcSafeName,
                    ExpectedValue: "readable-provider-history",
                    ObservedValue: "unavailable",
                    ProviderErrorClass: exception.GetType().Name,
                    Classification: CdcProviderRetryContinuityClassification.SourceHistoryUnknown
                ),
            ]
        );

    private static IReadOnlyDictionary<string, string> DatabaseCdcObservedValues(
        DatabaseCdcInspection inspection,
        bool wasEnabledAtStart
    )
    {
        var captureJob = inspection.JobsByType.GetValueOrDefault("capture");
        var cleanupJob = inspection.JobsByType.GetValueOrDefault("cleanup");
        var captureRuntime = inspection.JobRuntimeByType.GetValueOrDefault("capture");
        var cleanupRuntime = inspection.JobRuntimeByType.GetValueOrDefault("cleanup");

        return new Dictionary<string, string>
        {
            ["database_cdc_enabled"] = inspection.IsCdcEnabled.ToString(),
            ["database_cdc_was_enabled_at_start"] = wasEnabledAtStart.ToString(),
            ["read_committed_snapshot_on"] = inspection.ReadCommittedSnapshotOn.ToString(),
            ["nested_triggers_value"] = SafeText(inspection.NestedTriggersValue),
            ["capture_instance_count"] = inspection.CaptureInstanceCount.ToString(),
            ["capture_job_present"] = (captureJob is not null || captureRuntime is not null).ToString(),
            ["capture_job_name"] = SafeText(captureJob?.JobName ?? captureRuntime?.JobName ?? ""),
            ["capture_job_enabled"] = SafeText(captureRuntime?.Enabled ?? ""),
            ["capture_job_running"] = SafeText(captureRuntime?.Running ?? ""),
            ["capture_job_last_run_status"] = SafeText(captureRuntime?.LastRunStatus ?? ""),
            ["capture_job_maxtrans"] = SafeText(captureJob?.MaxTrans ?? ""),
            ["capture_job_maxscans"] = SafeText(captureJob?.MaxScans ?? ""),
            ["capture_job_continuous"] = SafeText(captureJob?.Continuous ?? ""),
            ["capture_job_pollinginterval"] = SafeText(captureJob?.PollingInterval ?? ""),
            ["cleanup_job_present"] = (cleanupJob is not null || cleanupRuntime is not null).ToString(),
            ["cleanup_job_name"] = SafeText(cleanupJob?.JobName ?? cleanupRuntime?.JobName ?? ""),
            ["cleanup_job_enabled"] = SafeText(cleanupRuntime?.Enabled ?? ""),
            ["cleanup_job_running"] = SafeText(cleanupRuntime?.Running ?? ""),
            ["cleanup_job_last_run_status"] = SafeText(cleanupRuntime?.LastRunStatus ?? ""),
            ["cleanup_job_retention_minutes"] = SafeText(cleanupJob?.Retention ?? ""),
            ["cleanup_job_threshold"] = SafeText(cleanupJob?.Threshold ?? ""),
            ["retained_lsn_row_count"] = inspection.RetainedLsn.RowCount.ToString(),
            ["retained_min_lsn"] = SafeText(inspection.RetainedLsn.MinLsn),
            ["retained_max_lsn"] = SafeText(inspection.RetainedLsn.MaxLsn),
            ["retained_lsn_gap_evaluation"] = "not_evaluated_without_committed_offset",
        };
    }

    private static IReadOnlyList<CdcProviderDiagnostic> DatabaseCdcDiagnostics(
        DatabaseCdcInspection inspection
    )
    {
        List<CdcProviderDiagnostic> diagnostics = [];

        if (!inspection.IsCdcEnabled)
        {
            return diagnostics;
        }

        var captureJobMissing =
            !inspection.JobsByType.ContainsKey("capture")
            && !inspection.JobRuntimeByType.ContainsKey("capture");
        var cleanupJobMissing =
            !inspection.JobsByType.ContainsKey("cleanup")
            && !inspection.JobRuntimeByType.ContainsKey("cleanup");

        if (inspection.CaptureInstanceCount > 0 && (captureJobMissing || cleanupJobMissing))
        {
            diagnostics.Add(
                ProviderHistoryUnavailable(
                    "CDC_SQLSERVER_DATABASE_CDC_JOBS_MISSING",
                    expectedValue: "capture-and-cleanup-jobs-present-after-table-cdc",
                    observedValue: $"capture={MissingOrPresent(captureJobMissing)};cleanup={MissingOrPresent(cleanupJobMissing)}"
                )
            );
        }

        foreach (var runtime in inspection.JobRuntimeByType.Values.OrderBy(job => job.JobType))
        {
            if (runtime.Enabled is "False" or "0")
            {
                diagnostics.Add(
                    ProviderHistoryWarning(
                        "CDC_SQLSERVER_CDC_JOB_DISABLED",
                        expectedValue: "cdc-job-enabled",
                        observedValue: $"{runtime.JobType}:{runtime.JobName}"
                    )
                );
            }

            if (runtime.JobType == "capture" && runtime.Running is "False" or "0")
            {
                diagnostics.Add(
                    ProviderHistoryWarning(
                        "CDC_SQLSERVER_CAPTURE_JOB_NOT_RUNNING",
                        expectedValue: "capture-job-running",
                        observedValue: runtime.JobName
                    )
                );
            }

            if (runtime.LastRunStatus is "0")
            {
                diagnostics.Add(
                    ProviderHistoryWarning(
                        "CDC_SQLSERVER_CDC_JOB_LAST_RUN_FAILED",
                        expectedValue: "last-run-succeeded-or-no-history",
                        observedValue: $"{runtime.JobType}:{runtime.JobName}"
                    )
                );
            }
        }

        if (!inspection.ReadCommittedSnapshotOn)
        {
            diagnostics.Add(
                ProviderHistoryWarning(
                    "CDC_SQLSERVER_READ_COMMITTED_SNAPSHOT_OFF",
                    expectedValue: "read-committed-snapshot-on",
                    observedValue: "false"
                )
            );
        }

        if (inspection.NestedTriggersValue is not "1")
        {
            diagnostics.Add(
                ProviderHistoryWarning(
                    "CDC_SQLSERVER_NESTED_TRIGGERS_NOT_ENABLED",
                    expectedValue: "nested-triggers-enabled",
                    observedValue: SafeText(inspection.NestedTriggersValue)
                )
            );
        }

        return diagnostics;
    }

    private static CdcProviderDiagnostic ProviderHistoryUnavailable(
        string code,
        string expectedValue,
        string observedValue
    ) =>
        new(
            Code: code,
            Category: CdcProviderDiagnosticCategory.ProviderHistoryUnavailable,
            Severity: CdcProviderDiagnosticSeverity.Error,
            PrincipalKind: CdcPrincipalKind.SetupPrincipal,
            ArtifactKind: CdcProviderArtifactKind.ProviderHistory,
            SafeName: _databaseCdcSafeName,
            ExpectedValue: expectedValue,
            ObservedValue: observedValue,
            ProviderErrorClass: null,
            Classification: CdcProviderRetryContinuityClassification.SourceHistoryUnknown
        );

    private static CdcProviderDiagnostic ProviderHistoryWarning(
        string code,
        string expectedValue,
        string observedValue
    ) =>
        new(
            Code: code,
            Category: CdcProviderDiagnosticCategory.ProviderHistoryUnavailable,
            Severity: CdcProviderDiagnosticSeverity.Warning,
            PrincipalKind: CdcPrincipalKind.None,
            ArtifactKind: CdcProviderArtifactKind.ProviderHistory,
            SafeName: _databaseCdcSafeName,
            ExpectedValue: expectedValue,
            ObservedValue: observedValue,
            ProviderErrorClass: null,
            Classification: CdcProviderRetryContinuityClassification.SourceHistoryUnknown
        );

    private static string MissingOrPresent(bool missing) => missing ? "missing" : "present";

    private static JobHelpObservation ReadJobHelp(IReadOnlyDictionary<string, string?> row) =>
        new(
            JobType: ReadRequired(row, "job_type").ToLowerInvariant(),
            JobName: ReadRequired(row, "job_name"),
            MaxTrans: ReadOptional(row, "maxtrans"),
            MaxScans: ReadOptional(row, "maxscans"),
            Continuous: ReadOptional(row, "continuous"),
            PollingInterval: ReadOptional(row, "pollinginterval"),
            Retention: ReadOptional(row, "retention"),
            Threshold: ReadOptional(row, "threshold")
        );

    private static JobRuntimeObservation ReadJobRuntime(IReadOnlyDictionary<string, string?> row) =>
        new(
            JobType: ReadRequired(row, "job_type").ToLowerInvariant(),
            JobName: ReadRequired(row, "job_name"),
            Enabled: ReadRequired(row, "enabled"),
            Running: ReadRequired(row, "running"),
            LastRunStatus: ReadOptional(row, "last_run_status")
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
                    SafeName: new CdcSafeName("sqlserver_setup_connection"),
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
                    Code: "CDC_SQLSERVER_SETUP_PRINCIPAL_FAILURE",
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

    private static string ObjectIdName(DbTableName table) =>
        $"{EscapeSqlLiteral(table.Schema.Value)}.{EscapeSqlLiteral(table.Name)}";

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string EmptyAsNone(string value) =>
        string.IsNullOrWhiteSpace(value) ? "none" : SafeText(value);

    private static IReadOnlyList<string> ReadCsv(
        IReadOnlyDictionary<string, string?> row,
        string columnName
    ) =>
        ReadRequired(row, columnName)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SafeText)
            .ToArray();

    private static string CsvOrNone(IEnumerable<string> values)
    {
        var sanitizedValues = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(SafeText)
            .ToArray();
        return sanitizedValues.Length == 0 ? "none" : string.Join(",", sanitizedValues);
    }

    private static string ReadRequired(IReadOnlyDictionary<string, string?> row, string columnName) =>
        row.TryGetValue(columnName, out var value) && value is not null
            ? value
            : throw new InvalidOperationException($"Expected SQL Server result column '{columnName}'.");

    private static string ReadOptional(IReadOnlyDictionary<string, string?> row, string columnName) =>
        row.TryGetValue(columnName, out var value) && value is not null ? value : "";

    private static bool ReadBool(IReadOnlyDictionary<string, string?> row, string columnName)
    {
        var value = ReadRequired(row, columnName);
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return value switch
        {
            "1" => true,
            "0" => false,
            _ => throw new InvalidOperationException(
                $"Expected SQL Server result column '{columnName}' to contain a boolean value."
            ),
        };
    }

    private static int ReadInt32(IReadOnlyDictionary<string, string?> row, string columnName) =>
        int.Parse(ReadRequired(row, columnName));

    private static long ReadInt64(IReadOnlyDictionary<string, string?> row, string columnName) =>
        long.Parse(ReadRequired(row, columnName));

    private sealed record DatabaseCdcInspection(
        bool IsCdcEnabled,
        bool ReadCommittedSnapshotOn,
        string NestedTriggersValue,
        int CaptureInstanceCount,
        IReadOnlyDictionary<string, JobHelpObservation> JobsByType,
        IReadOnlyDictionary<string, JobRuntimeObservation> JobRuntimeByType,
        RetainedLsnObservation RetainedLsn
    );

    private sealed record JobHelpObservation(
        string JobType,
        string JobName,
        string MaxTrans,
        string MaxScans,
        string Continuous,
        string PollingInterval,
        string Retention,
        string Threshold
    );

    private sealed record JobRuntimeObservation(
        string JobType,
        string JobName,
        string Enabled,
        string Running,
        string LastRunStatus
    );

    private sealed record RetainedLsnObservation(long RowCount, string MinLsn, string MaxLsn);

    private sealed record ConnectorPrincipalAccessInspection(
        bool IsExactMatch,
        bool IsGrantableMissingPrivilege,
        IReadOnlyDictionary<string, string> ObservedValues,
        IReadOnlyList<CdcGrantObservation> GrantInventory,
        IReadOnlyList<CdcProviderDiagnostic> Diagnostics
    );

    private sealed record HeartbeatTableShapeInspection(
        bool IsExactMatch,
        IReadOnlyDictionary<string, string> ObservedValues
    );

    private sealed record HeartbeatSingletonInspection(
        int SingletonRowCount,
        bool IsExactMatch,
        IReadOnlyDictionary<string, string> ObservedValues
    );

    private sealed record ExpectedSqlServerCaptureDefinition(
        CdcSourceTableKind TableKind,
        CdcSourceTableInventory ExpectedSourceTable,
        CdcSafeName CaptureInstanceName,
        CdcSafeName GatingRoleName
    );

    private sealed record SqlServerCaptureInstanceInspection(
        CdcSourceTableKind TableKind,
        CdcSafeName CaptureInstanceName,
        bool Exists,
        bool IsExactMatch,
        IReadOnlyDictionary<string, string> ObservedValues
    );

    private sealed record UnexpectedSqlServerCaptureInstance(
        CdcProviderArtifactObservation Artifact,
        IReadOnlyList<CdcProviderDiagnostic> Diagnostics
    );

    private sealed record SqlServerCaptureInstancesInspection(
        IReadOnlyList<SqlServerCaptureInstanceInspection> ExpectedInstances,
        IReadOnlyList<CdcProviderArtifactObservation> UnexpectedArtifacts,
        IReadOnlyList<CdcProviderDiagnostic> Diagnostics
    )
    {
        public bool HasMismatchedExistingArtifacts =>
            ExpectedInstances.Any(capture => capture.Exists && !capture.IsExactMatch)
            || UnexpectedArtifacts.Count > 0;
    }
}
