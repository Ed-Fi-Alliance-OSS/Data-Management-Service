// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Transactions;
using EdFi.DataManagementService.Backend.Ddl;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Uses independent autocommit connections to the configured bound source. Each inspection verifies
/// source identity and complete metadata visibility before accepting even an empty catalog result.
/// Never disables database-wide CDC, drops source tables/users, terminates slot users, or cascades.
/// Managed concurrency is excluded by the controller lock; out-of-band DDL is outside that lock.
/// </summary>
public sealed class CdcProviderArtifactCleanupAdapter : ICdcArtifactCleanupAdapter
{
    private readonly ICdcProviderDatabaseExecutor database;

    public CdcProviderArtifactCleanupAdapter(DbProviderFactory factory, string connectionString)
        : this(new AutocommitExecutor(factory, connectionString)) { }

    internal CdcProviderArtifactCleanupAdapter(ICdcProviderDatabaseExecutor executor) => database = executor;

    private sealed class AutocommitExecutor(DbProviderFactory factory, string connectionString)
        : ICdcProviderDatabaseExecutor
    {
        private DbConnection Connection()
        {
            // A transaction-local absence could disappear on rollback. Neither mutations nor their
            // independent read-backs may enlist in a caller's ambient transaction.
            if (Transaction.Current is not null)
            {
                throw new InvalidOperationException();
            }
            DbConnection connection = factory.CreateConnection() ?? throw new InvalidOperationException();
            connection.ConnectionString = connectionString;
            return connection;
        }

        public async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
        {
            await using var connection = Connection();
            await new DbConnectionCdcProviderDatabaseExecutor(connection).ExecuteNonQueryAsync(
                sql,
                cancellationToken
            );
        }

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
            string sql,
            CancellationToken cancellationToken
        )
        {
            await using var connection = Connection();
            return await new DbConnectionCdcProviderDatabaseExecutor(connection).QueryAsync(
                sql,
                cancellationToken
            );
        }
    }

    public Task<CdcTransportResult<CoreCdc.CdcGovernedArtifact>> DeleteAsync(
        CdcArtifactCleanupScope scope,
        CoreCdc.CdcGovernedArtifactKind kind,
        CancellationToken cancellationToken
    ) =>
        CdcArtifactCleanup.GuardAsync(
            scope,
            CdcDeploymentComponent.ProviderSetup,
            async token =>
            {
                if (
                    !scope.Inventory.Any(item => item.Kind == kind)
                    || kind
                        is not (
                            CoreCdc.CdcGovernedArtifactKind.PostgresqlPublication
                            or CoreCdc.CdcGovernedArtifactKind.PostgresqlLogicalSlot
                            or CoreCdc.CdcGovernedArtifactKind.SqlServerCdcGatingRole
                            or CoreCdc.CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument
                            or CoreCdc.CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache
                            or CoreCdc.CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat
                        )
                )
                {
                    return CdcArtifactCleanup.Failure(
                        CdcDeploymentComponent.ProviderSetup,
                        CdcDeploymentFailure.InvalidInput
                    );
                }

                var artifact = scope.Artifact(kind);
                var (inspect, drop) = Statements(scope, artifact);
                await VerifySourceAsync(scope, token);
                var before = await QueryAsync(scope, inspect, token);
                if (before.Count == 0)
                {
                    return CdcArtifactCleanup.Removed(artifact, false);
                }

                if (before.Count != 1 || before[0].GetValueOrDefault("safe_to_delete") != "1")
                {
                    return CdcArtifactCleanup.Failure(
                        CdcDeploymentComponent.ProviderSetup,
                        CdcDeploymentFailure.ValidationFailed
                    );
                }

                await CdcArtifactCleanup.AttemptAsync(
                    callToken => database.ExecuteNonQueryAsync(drop, callToken),
                    scope.Request,
                    token
                );
                await VerifySourceAsync(scope, token);
                var after = await QueryAsync(scope, inspect, token);
                return after.Count == 0
                    ? CdcArtifactCleanup.Removed(artifact, true)
                    : CdcArtifactCleanup.Failure(CdcDeploymentComponent.ProviderSetup);
            },
            cancellationToken
        );

    /// <summary>
    /// Database-scoped jobs require original trusted managed CREATE/source history under the held
    /// controller session, plus live absence of EVERY capture instance. Never drops peer/shared jobs
    /// from binding names alone. The controller journals this additional database-owned cleanup
    /// separately from Core's per-binding artifact inventory before discarding retirement state.
    /// </summary>
    public async Task<CdcTransportResult<CdcTransportAcknowledgement>> DeleteOwnedSqlServerJobsAsync(
        CdcArtifactCleanupScope scope,
        LocalCdcWorkflowJournalStore.Session session,
        CancellationToken cancellationToken
    )
    {
        return await CdcArtifactCleanup.GuardAsync<CdcTransportAcknowledgement>(
            scope,
            CdcDeploymentComponent.ProviderSetup,
            async token =>
            {
                if (scope.Request.ProviderSetup.Provider != CdcProvider.SqlServer)
                {
                    return JobFailure(
                        CdcDeploymentComponent.ProviderSetup,
                        CdcDeploymentFailure.InvalidInput
                    );
                }

                var journal = await session.ReadAsync(scope.Request.TargetIdentity, token);
                var history = await session.ReadSourcePublicationHistoryAsync(
                    scope.Request.TargetIdentity,
                    scope.Request.Binding.PhysicalSourceFingerprint,
                    token
                );
                if (
                    history.WorkflowId != journal.WorkflowId
                    || history.CreationTarget != journal.Target
                    || history.CreationReceipt.Outcome != CdcDatabaseCreationOutcome.Created
                    || !journal.Operations.Any(operation => operation.Effect == CdcWorkflowEffect.Retire)
                )
                {
                    return JobFailure(
                        CdcDeploymentComponent.WorkflowState,
                        CdcDeploymentFailure.ValidationFailed
                    );
                }

                await VerifySourceAsync(scope, token);
                const string inspect = """
                /* cdc:cleanup:database-jobs */
                IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1)
                BEGIN
                    IF EXISTS (SELECT 1 FROM cdc.change_tables)
                        THROW 51000, 'CDC capture instances still require database jobs.', 1;
                END;
                IF OBJECT_ID(N'msdb.dbo.cdc_jobs') IS NOT NULL
                    EXEC sys.sp_executesql N'
                        IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs j
                            WHERE j.name IN (N''cdc.'' + DB_NAME() + N''_capture'', N''cdc.'' + DB_NAME() + N''_cleanup'')
                            AND NOT EXISTS (SELECT 1 FROM msdb.dbo.cdc_jobs c WHERE c.database_id = DB_ID() AND c.job_id = j.job_id))
                            THROW 51000, ''CDC job metadata is missing for surviving jobs.'', 1;
                        SELECT job_type, CONVERT(nvarchar(36), job_id) AS job_id FROM msdb.dbo.cdc_jobs WHERE database_id = DB_ID();';
                ELSE
                BEGIN
                    IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name IN (N'cdc.' + DB_NAME() + N'_capture', N'cdc.' + DB_NAME() + N'_cleanup'))
                        THROW 51000, 'CDC job metadata is missing for surviving jobs.', 1;
                    SELECT '' AS job_type, '' AS job_id WHERE 1 = 0;
                END;
                """;
                var jobs = await QueryAsync(scope, inspect, token);
                if (
                    jobs.Any(row =>
                        row.GetValueOrDefault("job_type") is not ("capture" or "cleanup")
                        || !Guid.TryParseExact(row.GetValueOrDefault("job_id"), "D", out Guid id)
                        || id == Guid.Empty
                    )
                    || jobs.Select(row => row["job_type"]).Distinct().Count() != jobs.Count
                )
                {
                    return JobFailure(CdcDeploymentComponent.ProviderSetup);
                }

                foreach (var job in jobs)
                {
                    await CdcArtifactCleanup.AttemptAsync(
                        callToken =>
                            database.ExecuteNonQueryAsync(
                                $"EXEC sys.sp_cdc_drop_job @job_type = N{Literal(job["job_type"]!)};",
                                callToken
                            ),
                        scope.Request,
                        token
                    );
                }
                await VerifySourceAsync(scope, token);
                if (jobs.Count > 0)
                {
                    string ids = string.Join(",", jobs.Select(job => Literal(job["job_id"]!)));
                    if (
                        (
                            await QueryAsync(
                                scope,
                                $"/* cdc:cleanup:job-identities */ SELECT job_id FROM msdb.dbo.sysjobs WHERE job_id IN ({ids});",
                                token
                            )
                        ).Count > 0
                    )
                    {
                        return JobFailure(CdcDeploymentComponent.ProviderSetup);
                    }
                }
                return (await QueryAsync(scope, inspect, token)).Count == 0
                    ? new CdcTransportResult<CdcTransportAcknowledgement>.Observed(new())
                    : JobFailure(CdcDeploymentComponent.ProviderSetup);
            },
            cancellationToken
        );
    }

    private static CdcTransportResult<CdcTransportAcknowledgement> JobFailure(
        CdcDeploymentComponent component,
        CdcDeploymentFailure failure = CdcDeploymentFailure.Unavailable
    ) => new CdcTransportResult<CdcTransportAcknowledgement>.Unavailable(new(component, failure));

    private async Task VerifySourceAsync(CdcArtifactCleanupScope scope, CancellationToken token)
    {
        bool postgres = scope.Request.ProviderSetup.Provider == CdcProvider.Postgresql;
        // SQL Server metadata can hide rows from low-privilege principals. Require the authority used
        // by the local provider setup, rather than mistaking filtered catalogs for artifact absence.
        string sql = postgres
            ? """
                /* cdc:cleanup:source */
                SELECT "SourceIdentity"::text AS source_identity,
                    CASE WHEN (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN '1' ELSE '0' END AS metadata_authority
                FROM dms."DataStoreIdentity" WHERE "DataStoreIdentitySingletonId" = 1;
                """
            : """
                /* cdc:cleanup:source */
                SELECT CONVERT(nvarchar(36), [SourceIdentity]) AS source_identity,
                    CASE WHEN IS_SRVROLEMEMBER(N'sysadmin') = 1 THEN '1' ELSE '0' END AS metadata_authority
                FROM [dms].[DataStoreIdentity] WHERE [DataStoreIdentitySingletonId] = 1;
                """;
        var rows = await QueryAsync(scope, sql, token);
        if (
            rows.Count != 1
            || rows[0].GetValueOrDefault("metadata_authority") != "1"
            || rows[0].GetValueOrDefault("source_identity") is not string identity
            || CdcSourceFingerprintMetadata.Compute(scope.Request.ProviderSetup.Provider, identity)
                != scope.Request.ProviderSetup.BoundPhysicalSourceFingerprint
        )
        {
            throw new InvalidOperationException("CDC cleanup source or metadata authority unavailable.");
        }
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
        CdcArtifactCleanupScope scope,
        string sql,
        CancellationToken token
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(scope.Request.Timing.CallTimeout);
        return await database.QueryAsync(sql, timeout.Token).WaitAsync(timeout.Token);
    }

    private static (string Inspect, string Drop) Statements(
        CdcArtifactCleanupScope scope,
        CoreCdc.CdcGovernedArtifactName artifact
    )
    {
        string literal = Literal(artifact.Name);
        if (artifact.Kind == CoreCdc.CdcGovernedArtifactKind.PostgresqlLogicalSlot)
        {
            return (
                $"""
                /* cdc:cleanup:slot */
                SELECT CASE WHEN database = current_database() AND slot_type = 'logical'
                    AND plugin = 'pgoutput' AND NOT active AND NOT temporary THEN '1' ELSE '0' END AS safe_to_delete
                FROM pg_replication_slots WHERE slot_name = {literal};
                """,
                $"SELECT pg_drop_replication_slot({literal});"
            );
        }

        if (artifact.Kind == CoreCdc.CdcGovernedArtifactKind.PostgresqlPublication)
        {
            return (
                $"""
                /* cdc:cleanup:publication */
                SELECT CASE WHEN NOT puballtables AND NOT EXISTS (
                    SELECT 1 FROM pg_publication_tables t WHERE t.pubname = p.pubname
                    AND (t.schemaname <> 'dms' OR t.tablename NOT IN ('Document','DocumentCache','CdcHeartbeat'))
                ) THEN '1' ELSE '0' END AS safe_to_delete FROM pg_publication p WHERE pubname = {literal};
                """,
                $"DROP PUBLICATION {PgIdentifier(artifact.Name)} RESTRICT;"
            );
        }

        string role = scope.Artifact(CoreCdc.CdcGovernedArtifactKind.SqlServerCdcGatingRole).Name;
        if (artifact.Kind == CoreCdc.CdcGovernedArtifactKind.SqlServerCdcGatingRole)
        {
            string member = scope.Request.ProviderSetup.ConnectorPrincipal.SafePrincipalName.Value;
            return (
                $"""
                /* cdc:cleanup:role */
                IF OBJECT_ID(N'cdc.change_tables') IS NOT NULL
                    AND EXISTS (SELECT 1 FROM cdc.change_tables WHERE role_name = N{literal})
                    THROW 51000, 'CDC gating role is still in use.', 1;
                SELECT CASE WHEN p.type = 'R' AND p.is_fixed_role = 0
                    AND NOT EXISTS (SELECT 1 FROM sys.database_role_members m WHERE
                        (m.role_principal_id = p.principal_id AND m.member_principal_id <> DATABASE_PRINCIPAL_ID(N{Literal(
                    member
                )}))
                        OR m.member_principal_id = p.principal_id)
                    AND NOT EXISTS (SELECT 1 FROM sys.database_permissions g WHERE
                        (g.grantee_principal_id = p.principal_id AND NOT (g.class = 1 AND g.minor_id = 0
                            AND g.permission_name = 'SELECT' AND g.state = 'G'
                            AND g.major_id IN (OBJECT_ID(N'cdc.change_tables'), OBJECT_ID(N'cdc.captured_columns'))))
                        OR g.grantor_principal_id = p.principal_id)
                    AND NOT EXISTS (SELECT 1 FROM sys.schemas WHERE principal_id = p.principal_id)
                    AND NOT EXISTS (SELECT 1 FROM sys.objects WHERE principal_id = p.principal_id)
                    THEN '1' ELSE '0' END AS safe_to_delete
                FROM sys.database_principals p WHERE p.name = N{literal};
                """,
                $"""
                IF EXISTS (SELECT 1 FROM sys.database_role_members WHERE
                    role_principal_id = DATABASE_PRINCIPAL_ID(N{literal})
                    AND member_principal_id = DATABASE_PRINCIPAL_ID(N{Literal(member)}))
                    ALTER ROLE {SqlIdentifier(role)} DROP MEMBER {SqlIdentifier(member)};
                DROP ROLE {SqlIdentifier(role)};
                """
            );
        }
        string table = artifact.Kind switch
        {
            CoreCdc.CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument => "Document",
            CoreCdc.CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache => "DocumentCache",
            CoreCdc.CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat => "CdcHeartbeat",
            _ => throw new InvalidOperationException(),
        };
        return (
            $"""
            /* cdc:cleanup:capture */
            IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1)
                SELECT CASE WHEN source_object_id = OBJECT_ID(N'dms.{table}') AND role_name = N{Literal(role)}
                    THEN '1' ELSE '0' END AS safe_to_delete FROM cdc.change_tables WHERE capture_instance = N{literal};
            ELSE SELECT '0' AS safe_to_delete WHERE 1 = 0;
            """,
            $"EXEC sys.sp_cdc_disable_table @source_schema=N'dms', @source_name=N'{table}', @capture_instance=N{literal};"
        );
    }

    private static string Literal(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string PgIdentifier(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string SqlIdentifier(string value) =>
        "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
