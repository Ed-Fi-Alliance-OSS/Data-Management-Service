// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Kind = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcGovernedArtifactKind;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, Category = "MssqlIntegration")]
[Category("CdcArtifactCleanup")]
[Category("DatabaseIntegration")]
[NonParallelizable]
[Platform(Exclude = "Win", Reason = "Local CDC ownership state requires Unix permissions.")]
public class Given_CdcArtifactCleanupProviderDatabase(CdcProvider provider)
{
    private const string SourceIdentity = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";
    private DbConnection _admin = null!;
    private DbConnection _connection = null!;
    private DbConnectionCdcProviderDatabaseExecutor _executor = null!;
    private CdcArtifactCleanupScope _scope = null!;
    private CdcProviderArtifactCleanupAdapter _adapter = null!;
    private LocalCdcWorkflowJournalStore _store = null!;
    private string _database = "";
    private string _stateRoot = "";
    private bool _created;

    [SetUp]
    public async Task Setup()
    {
        _created = false;
        string variable =
            provider == CdcProvider.Postgresql ? "CDC_CLEANUP_POSTGRESQL_ADMIN" : "CDC_CLEANUP_MSSQL_ADMIN";
        string configured = Environment.GetEnvironmentVariable(variable) ?? "";
        if (configured.Length == 0)
        {
            if (Environment.GetEnvironmentVariable("CDC_ARTIFACT_CLEANUP_FAIL_FAST") == "true")
            {
                Assert.Fail("The requested provider cleanup qualification requires its admin endpoint.");
            }
            Assert.Ignore("Provider cleanup admin endpoint is not configured.");
        }
        _database = "cdc_cleanup_" + Guid.NewGuid().ToString("N");
        _stateRoot = Path.Combine(Path.GetTempPath(), _database);
        _store = new(_stateRoot);
        _admin =
            provider == CdcProvider.Postgresql
                ? new NpgsqlConnection(configured)
                : new SqlConnection(configured);
        await _admin.OpenAsync();
        var request = CdcDeploymentRequestTestData.Request(provider);
        _scope = new(
            request,
            request.Binding.ToCompleteBindingIdentity(),
            CoreCdc
                .CdcArtifactNameGenerator.RecoverFromCompleteBindingIdentity(
                    request.Binding.ToCompleteBindingIdentity()
                )
                .Inventory!.GovernedArtifacts
        );
        string connectionString =
            provider == CdcProvider.Postgresql
                ? new NpgsqlConnectionStringBuilder(configured) { Database = _database }.ConnectionString
                : new SqlConnectionStringBuilder(configured) { InitialCatalog = _database }.ConnectionString;
        _connection =
            provider == CdcProvider.Postgresql
                ? new NpgsqlConnection(connectionString)
                : new SqlConnection(connectionString);
        _executor = new(_connection);
        _adapter = new(
            provider == CdcProvider.Postgresql ? NpgsqlFactory.Instance : SqlClientFactory.Instance,
            connectionString
        );
        var receipt = await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
            request.TargetIdentity,
            new Provisioner(this),
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );
        await session.RecordIntentAsync(
            request.TargetIdentity,
            receipt.WorkflowId,
            Guid.NewGuid(),
            CdcWorkflowEffect.Retire,
            [],
            CancellationToken.None
        );
    }

    private sealed class Provisioner(Given_CdcArtifactCleanupProviderDatabase fixture)
        : ICdcManagedDatabaseProvisioner
    {
        public bool CreateDatabase()
        {
            using var command = fixture._admin.CreateCommand();
            command.CommandText = "CREATE DATABASE " + fixture._database;
            command.ExecuteNonQuery();
            fixture._created = true;
            return true;
        }

        public void ProvisionSchema(bool databaseWasCreated) =>
            fixture.CreateArtifacts().GetAwaiter().GetResult();

        public void ValidateSchema() { }

        public Task<string> ReadSourceFingerprintAsync(CancellationToken cancellationToken) =>
            Task.FromResult(fixture._scope.Request.Binding.PhysicalSourceFingerprint);
    }

    private async Task CreateArtifacts()
    {
        await _connection.OpenAsync();
        if (provider == CdcProvider.Postgresql)
        {
            await Execute(
                $"""
                CREATE SCHEMA dms;
                CREATE TABLE dms."DataStoreIdentity" ("DataStoreIdentitySingletonId" smallint PRIMARY KEY, "SourceIdentity" uuid NOT NULL);
                INSERT INTO dms."DataStoreIdentity" VALUES (1, '{SourceIdentity}');
                CREATE TABLE dms."Document" (id int PRIMARY KEY);
                CREATE TABLE dms."DocumentCache" (id int PRIMARY KEY);
                CREATE TABLE dms."CdcHeartbeat" (id int PRIMARY KEY);
                CREATE TABLE dms.peer (id int PRIMARY KEY);
                CREATE PUBLICATION "{Name(
                    Kind.PostgresqlPublication
                )}" FOR TABLE dms."Document", dms."DocumentCache", dms."CdcHeartbeat";
                """
            );
            await Execute(
                $"SELECT pg_create_logical_replication_slot('{Name(Kind.PostgresqlLogicalSlot)}', 'pgoutput');"
            );
        }
        else
        {
            await Execute("CREATE SCHEMA dms;");
            await Execute(
                $"""
                CREATE TABLE dms.DataStoreIdentity (DataStoreIdentitySingletonId smallint PRIMARY KEY, SourceIdentity uniqueidentifier NOT NULL);
                INSERT INTO dms.DataStoreIdentity VALUES (1, '{SourceIdentity}');
                CREATE TABLE dms.Document (id int PRIMARY KEY);
                CREATE TABLE dms.DocumentCache (id int PRIMARY KEY);
                CREATE TABLE dms.CdcHeartbeat (id int PRIMARY KEY);
                CREATE TABLE dms.peer (id int PRIMARY KEY);
                CREATE USER [connector-principal] WITHOUT LOGIN;
                EXEC sys.sp_cdc_enable_db;
                """
            );
            foreach (var (kind, table) in Captures)
            {
                await Execute(
                    $"EXEC sys.sp_cdc_enable_table @source_schema=N'dms', @source_name=N'{table}', @role_name=N'{Name(Kind.SqlServerCdcGatingRole)}', @capture_instance=N'{Name(kind)}', @supports_net_changes=0;"
                );
            }
            await Execute(
                $"""
                ALTER ROLE [{Name(Kind.SqlServerCdcGatingRole)}] ADD MEMBER [connector-principal];
                GRANT SELECT ON OBJECT::cdc.change_tables TO [{Name(Kind.SqlServerCdcGatingRole)}];
                GRANT SELECT ON OBJECT::cdc.captured_columns TO [{Name(Kind.SqlServerCdcGatingRole)}];
                """
            );
        }
    }

    private static readonly (Kind Kind, string Table)[] Captures =
    [
        (Kind.SqlServerCaptureInstanceDocument, "Document"),
        (Kind.SqlServerCaptureInstanceDocumentCache, "DocumentCache"),
        (Kind.SqlServerCaptureInstanceCdcHeartbeat, "CdcHeartbeat"),
    ];

    private string Name(Kind kind) => _scope.Inventory.Single(item => item.Kind == kind).Name;

    private Task Execute(string sql) => _executor.ExecuteNonQueryAsync(sql, CancellationToken.None);

    private Task<CdcTransportResult<CoreCdc.CdcGovernedArtifact>> Delete(Kind kind) =>
        _adapter.DeleteAsync(_scope, kind, CancellationToken.None);

    private async Task DeleteProviderArtifacts()
    {
        Kind[] kinds =
            provider == CdcProvider.Postgresql
                ? [Kind.PostgresqlLogicalSlot, Kind.PostgresqlPublication]
                : [.. Captures.Select(item => item.Kind), Kind.SqlServerCdcGatingRole];
        foreach (Kind kind in kinds)
        {
            var result = await Delete(kind);
            result
                .State.Should()
                .Be(
                    CdcTransportEvidenceState.Observed,
                    $"{kind}: {string.Join(",", result.Diagnostics.Select(d => d.ToString()))}"
                );
            (await Delete(kind))
                .State.Should()
                .Be(CdcTransportEvidenceState.Observed, "retirement retries must inspect absence");
        }
    }

    [Test]
    public async Task It_removes_provider_artifacts_and_only_owned_unused_database_jobs()
    {
        await DeleteProviderArtifacts();
        if (provider == CdcProvider.SqlServer)
        {
            await using var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None
            );
            (await _adapter.DeleteOwnedSqlServerJobsAsync(_scope, session, CancellationToken.None))
                .State.Should()
                .Be(CdcTransportEvidenceState.Observed);
            (
                await _executor.QueryAsync(
                    "SELECT job_id FROM msdb.dbo.sysjobs WHERE name IN (N'cdc.'+DB_NAME()+N'_capture', N'cdc.'+DB_NAME()+N'_cleanup');",
                    CancellationToken.None
                )
            )
                .Should()
                .BeEmpty();
            (
                await _executor.QueryAsync(
                    "SELECT name FROM sys.database_principals WHERE name=N'connector-principal';",
                    CancellationToken.None
                )
            )
                .Should()
                .ContainSingle();
        }
        (await _executor.QueryAsync("SELECT 1 AS present FROM dms.peer WHERE 1=0;", CancellationToken.None))
            .Should()
            .BeEmpty();
    }

    [Test]
    public async Task It_keeps_shared_provider_artifacts_and_rejects_unsafe_deletion()
    {
        if (provider == CdcProvider.Postgresql)
        {
            await Execute($"ALTER PUBLICATION \"{Name(Kind.PostgresqlPublication)}\" ADD TABLE dms.peer;");
            (await Delete(Kind.PostgresqlPublication))
                .State.Should()
                .Be(CdcTransportEvidenceState.Unavailable);
        }
        else
        {
            await Execute(
                "EXEC sys.sp_cdc_enable_table @source_schema=N'dms', @source_name=N'peer', @role_name=NULL, @capture_instance=N'peer_capture', @supports_net_changes=0;"
            );
            await DeleteProviderArtifacts();
            await using var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None
            );
            (await _adapter.DeleteOwnedSqlServerJobsAsync(_scope, session, CancellationToken.None))
                .State.Should()
                .Be(CdcTransportEvidenceState.Unavailable);
            (
                await _executor.QueryAsync(
                    "SELECT job_id FROM msdb.dbo.sysjobs WHERE name IN (N'cdc.'+DB_NAME()+N'_capture', N'cdc.'+DB_NAME()+N'_cleanup');",
                    CancellationToken.None
                )
            )
                .Should()
                .HaveCount(2);
            (
                await _executor.QueryAsync(
                    "SELECT capture_instance FROM cdc.change_tables WHERE capture_instance='peer_capture';",
                    CancellationToken.None
                )
            )
                .Should()
                .ContainSingle();
        }
    }

    [Test]
    public async Task It_rejects_broad_or_orphaned_provider_artifacts()
    {
        if (provider == CdcProvider.Postgresql)
        {
            await Execute(
                $"DROP PUBLICATION \"{Name(Kind.PostgresqlPublication)}\"; CREATE PUBLICATION \"{Name(Kind.PostgresqlPublication)}\" FOR ALL TABLES;"
            );
            (await Delete(Kind.PostgresqlPublication))
                .State.Should()
                .Be(CdcTransportEvidenceState.Unavailable);
        }
        else
        {
            await DeleteProviderArtifacts();
            await Execute("DELETE FROM msdb.dbo.cdc_jobs WHERE database_id=DB_ID();");
            await using var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None
            );
            (await _adapter.DeleteOwnedSqlServerJobsAsync(_scope, session, CancellationToken.None))
                .State.Should()
                .Be(CdcTransportEvidenceState.Unavailable);
        }
    }

    [Test]
    public async Task It_rejects_a_different_live_physical_source()
    {
        await Execute(
            provider == CdcProvider.Postgresql
                ? "UPDATE dms.\"DataStoreIdentity\" SET \"SourceIdentity\"='86a7cc04-64cf-4b34-b66f-a7b9b4f6b6fd';"
                : "UPDATE dms.DataStoreIdentity SET SourceIdentity='86a7cc04-64cf-4b34-b66f-a7b9b4f6b6fd';"
        );
        (
            await Delete(
                provider == CdcProvider.Postgresql
                    ? Kind.PostgresqlPublication
                    : Kind.SqlServerCaptureInstanceDocument
            )
        )
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_reconciles_actual_committed_deletion_after_lost_response()
    {
        _adapter = new(new LostResponse(_executor));
        (
            await Delete(
                provider == CdcProvider.Postgresql
                    ? Kind.PostgresqlLogicalSlot
                    : Kind.SqlServerCaptureInstanceDocument
            )
        )
            .State.Should()
            .Be(CdcTransportEvidenceState.Observed);
    }

    private sealed class LostResponse(ICdcProviderDatabaseExecutor inner) : ICdcProviderDatabaseExecutor
    {
        public async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
        {
            await inner.ExecuteNonQueryAsync(sql, cancellationToken);
            throw new TimeoutException("private-source-secret");
        }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
            string sql,
            CancellationToken cancellationToken
        ) => inner.QueryAsync(sql, cancellationToken);
    }

    [TearDown]
    public async Task Teardown()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
        if (_admin is not null)
        {
            try
            {
                if (_created)
                {
                    await using var command = _admin.CreateCommand();
                    if (provider == CdcProvider.Postgresql)
                    {
                        command.CommandText =
                            $"SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE database='{_database}';";
                        await command.ExecuteNonQueryAsync();
                    }
                    command.CommandText =
                        provider == CdcProvider.Postgresql
                            ? $"DROP DATABASE {_database} WITH (FORCE);"
                            : $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}];";
                    await command.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                await _admin.DisposeAsync();
            }
        }
        if (Directory.Exists(_stateRoot))
        {
            Directory.Delete(_stateRoot, true);
        }
    }
}
