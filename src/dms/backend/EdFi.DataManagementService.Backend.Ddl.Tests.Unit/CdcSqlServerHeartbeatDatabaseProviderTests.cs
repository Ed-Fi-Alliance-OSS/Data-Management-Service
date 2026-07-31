// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_MssqlCdcHeartbeatDatabase_Initial_Setup
{
    private RecordingSqlServerCdcExecutor _executor = null!;
    private CdcProviderSetupResult _result = null!;

    [SetUp]
    public async Task SetUp()
    {
        _executor = new RecordingSqlServerCdcExecutor();
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        _result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(databaseExecutor: _executor)
        );
    }

    [Test]
    public void It_should_enable_database_cdc_without_mutating_projection_prerequisites_or_jobs()
    {
        _result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        _result
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        _executor.ExecutedSql.Should().ContainSingle(sql => sql.Contains("EXEC sys.sp_cdc_enable_db"));
        _executor.ExecutedSql.Should().NotContain(sql => sql.Contains("READ_COMMITTED_SNAPSHOT"));
        _executor.ExecutedSql.Should().NotContain(sql => sql.Contains("sp_configure"));
        _executor.ExecutedSql.Should().NotContain(sql => sql.Contains("sp_cdc_add_job"));
        _executor.ExecutedSql.Should().NotContain(sql => sql.Contains("sp_cdc_change_job"));

        _result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.SafeObservedValues["database_cdc_enabled"] == "True"
                && observation.SafeObservedValues["capture_instance_count"] == "0"
                && observation.SafeObservedValues["capture_job_present"] == "False"
                && observation.SafeObservedValues["cleanup_job_present"] == "False"
            );
    }

    [Test]
    public void It_should_create_the_opt_in_heartbeat_table_and_singleton()
    {
        _result
            .ArtifactInventory.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
                && observation.State == CdcProviderArtifactState.Created
            );

        var createSql = _executor.ExecutedSql.Single(sql =>
            sql.Contains("cdc:sqlserver:create-heartbeat-table")
        );
        createSql.Should().Contain("CREATE TABLE [dms].[CdcHeartbeat]");
        createSql.Should().Contain("[HeartbeatId] smallint NOT NULL");
        createSql.Should().Contain("[HeartbeatSequence] bigint NOT NULL");
        createSql.Should().Contain("[HeartbeatAt] datetime2(7) NOT NULL");
        createSql.Should().Contain("CONSTRAINT [PK_CdcHeartbeat] PRIMARY KEY CLUSTERED ([HeartbeatId])");
        createSql.Should().Contain("CONSTRAINT [CK_CdcHeartbeat_Singleton] CHECK ([HeartbeatId] = 1)");
        createSql.Should().Contain("CONSTRAINT [CK_CdcHeartbeat_Sequence] CHECK ([HeartbeatSequence] >= 0)");
        createSql.Should().Contain("VALUES (1, 0, sysutcdatetime())");
    }

    [Test]
    public void It_should_return_generated_heartbeat_action_query_and_document_uuid_message_keys()
    {
        _result
            .HeartbeatActionQuery!.Sql.Should()
            .Be(
                "UPDATE [dms].[CdcHeartbeat] SET [HeartbeatSequence] = [HeartbeatSequence] + 1, [HeartbeatAt] = sysutcdatetime() WHERE [HeartbeatId] = 1"
            );
        _result.HeartbeatActionQuery.Sha256Hash.Should().HaveLength(64);

        _result.ExpectedMessageKeyColumns.Should().HaveCount(2);
        _result
            .ExpectedMessageKeyColumns.Should()
            .ContainSingle(key =>
                key.TableKind == CdcSourceTableKind.Document
                && key.KeyColumns.Select(column => column.Value).SequenceEqual(new[] { "DocumentUuid" })
            );
        _result
            .ExpectedMessageKeyColumns.Should()
            .ContainSingle(key =>
                key.TableKind == CdcSourceTableKind.DocumentCache
                && key.KeyColumns.Select(column => column.Value).SequenceEqual(new[] { "DocumentUuid" })
            );
        _result
            .ExpectedMessageKeyColumns.Should()
            .NotContain(key => key.TableKind == CdcSourceTableKind.CdcHeartbeat);
    }
}

[TestFixture]
public class Given_MssqlCdcHeartbeatDatabase_ValidateOnly
{
    [Test]
    public async Task It_should_not_enable_database_cdc_when_missing()
    {
        var executor = new RecordingSqlServerCdcExecutor();
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.State == CdcProviderArtifactState.Missing
            );
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISSING");
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_exact_match_existing_database_cdc_and_heartbeat_without_writes()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase();
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        result
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_fail_closed_when_existing_table_cdc_has_missing_required_jobs()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(captureInstanceCount: 3);
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_DATABASE_CDC_JOBS_MISSING"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryUnavailable
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.SourceHistoryUnknown
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_report_disabled_stopped_or_failed_jobs_without_repairing_them()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            captureInstanceCount: 3,
            captureJobPresent: true,
            cleanupJobPresent: true,
            captureJobEnabled: "False",
            captureJobRunning: "False",
            captureJobLastRunStatus: "0"
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CDC_JOB_DISABLED"
                && diagnostic.Severity == CdcProviderDiagnosticSeverity.Warning
            );
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CAPTURE_JOB_NOT_RUNNING"
                && diagnostic.Severity == CdcProviderDiagnosticSeverity.Warning
            );
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_CDC_JOB_LAST_RUN_FAILED"
                && diagnostic.Severity == CdcProviderDiagnosticSeverity.Warning
            );
        executor.ExecutedSql.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_report_projection_prerequisite_diagnostics_without_mutation()
    {
        var executor = RecordingSqlServerCdcExecutor.WithExistingHeartbeatDatabase(
            readCommittedSnapshotOn: false,
            nestedTriggersValue: "0"
        );
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildSqlServerRequest(
                mode: CdcProviderSetupMode.ValidateOnly,
                databaseExecutor: executor
            )
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_READ_COMMITTED_SNAPSHOT_OFF"
                && diagnostic.Severity == CdcProviderDiagnosticSeverity.Warning
            );
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_NESTED_TRIGGERS_NOT_ENABLED"
                && diagnostic.Severity == CdcProviderDiagnosticSeverity.Warning
            );
        executor.ExecutedSql.Should().BeEmpty();
    }
}

internal sealed class RecordingSqlServerCdcExecutor : ICdcProviderDatabaseExecutor
{
    private bool _databaseCdcEnabled;
    private bool _heartbeatTableExists;
    private bool _heartbeatSingletonExists;
    private readonly bool _readCommittedSnapshotOn;
    private readonly string _nestedTriggersValue;
    private readonly int _captureInstanceCount;
    private readonly bool _captureJobPresent;
    private readonly bool _cleanupJobPresent;
    private readonly string _captureJobEnabled;
    private readonly string _captureJobRunning;
    private readonly string _captureJobLastRunStatus;
    private readonly string _cleanupJobEnabled;
    private readonly string _cleanupJobRunning;
    private readonly string _cleanupJobLastRunStatus;

    public RecordingSqlServerCdcExecutor(
        bool databaseCdcEnabled = false,
        bool heartbeatTableExists = false,
        bool heartbeatSingletonExists = false,
        bool readCommittedSnapshotOn = true,
        string nestedTriggersValue = "1",
        int captureInstanceCount = 0,
        bool captureJobPresent = false,
        bool cleanupJobPresent = false,
        string captureJobEnabled = "True",
        string captureJobRunning = "True",
        string captureJobLastRunStatus = "",
        string cleanupJobEnabled = "True",
        string cleanupJobRunning = "False",
        string cleanupJobLastRunStatus = ""
    )
    {
        _databaseCdcEnabled = databaseCdcEnabled;
        _heartbeatTableExists = heartbeatTableExists;
        _heartbeatSingletonExists = heartbeatSingletonExists;
        _readCommittedSnapshotOn = readCommittedSnapshotOn;
        _nestedTriggersValue = nestedTriggersValue;
        _captureInstanceCount = captureInstanceCount;
        _captureJobPresent = captureJobPresent;
        _cleanupJobPresent = cleanupJobPresent;
        _captureJobEnabled = captureJobEnabled;
        _captureJobRunning = captureJobRunning;
        _captureJobLastRunStatus = captureJobLastRunStatus;
        _cleanupJobEnabled = cleanupJobEnabled;
        _cleanupJobRunning = cleanupJobRunning;
        _cleanupJobLastRunStatus = cleanupJobLastRunStatus;
    }

    public List<string> ExecutedSql { get; } = [];

    public static RecordingSqlServerCdcExecutor WithExistingHeartbeatDatabase(
        bool readCommittedSnapshotOn = true,
        string nestedTriggersValue = "1",
        int captureInstanceCount = 0,
        bool captureJobPresent = false,
        bool cleanupJobPresent = false,
        string captureJobEnabled = "True",
        string captureJobRunning = "True",
        string captureJobLastRunStatus = "",
        string cleanupJobEnabled = "True",
        string cleanupJobRunning = "False",
        string cleanupJobLastRunStatus = ""
    ) =>
        new(
            databaseCdcEnabled: true,
            heartbeatTableExists: true,
            heartbeatSingletonExists: true,
            readCommittedSnapshotOn: readCommittedSnapshotOn,
            nestedTriggersValue: nestedTriggersValue,
            captureInstanceCount: captureInstanceCount,
            captureJobPresent: captureJobPresent,
            cleanupJobPresent: cleanupJobPresent,
            captureJobEnabled: captureJobEnabled,
            captureJobRunning: captureJobRunning,
            captureJobLastRunStatus: captureJobLastRunStatus,
            cleanupJobEnabled: cleanupJobEnabled,
            cleanupJobRunning: cleanupJobRunning,
            cleanupJobLastRunStatus: cleanupJobLastRunStatus
        );

    public Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        ExecutedSql.Add(sql);

        if (sql.Contains("cdc:sqlserver:enable-database-cdc"))
        {
            _databaseCdcEnabled = true;
        }

        if (sql.Contains("cdc:sqlserver:create-heartbeat-table"))
        {
            _heartbeatTableExists = true;
            _heartbeatSingletonExists = true;
        }

        if (sql.Contains("INSERT INTO [dms].[CdcHeartbeat]"))
        {
            _heartbeatSingletonExists = true;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
        string sql,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = sql switch
        {
            var text when text.Contains("cdc:sqlserver:database-cdc-state") =>
            [
                Row(
                    ("is_cdc_enabled", _databaseCdcEnabled.ToString()),
                    ("read_committed_snapshot_on", _readCommittedSnapshotOn.ToString()),
                    ("nested_triggers_value", _nestedTriggersValue)
                ),
            ],
            var text when text.Contains("cdc:sqlserver:capture-instance-count") =>
            [
                Row(("capture_instance_count", _captureInstanceCount.ToString())),
            ],
            var text when text.Contains("cdc:sqlserver:help-jobs") => JobHelpRows(),
            var text when text.Contains("cdc:sqlserver:job-runtime") => JobRuntimeRows(),
            var text when text.Contains("cdc:sqlserver:retained-lsn") =>
            [
                Row(("lsn_row_count", "0"), ("min_lsn", ""), ("max_lsn", "")),
            ],
            var text when text.Contains("cdc:sqlserver:table-exists") =>
            [
                Row(("table_exists", _heartbeatTableExists.ToString())),
            ],
            var text when text.Contains("cdc:sqlserver:heartbeat-shape") =>
            [
                Row(
                    ("primary_key_matches", _heartbeatTableExists.ToString()),
                    ("singleton_check_matches", _heartbeatTableExists.ToString()),
                    ("sequence_check_matches", _heartbeatTableExists.ToString())
                ),
            ],
            var text when text.Contains("cdc:sqlserver:heartbeat-singleton") =>
            [
                Row(
                    ("row_count", _heartbeatSingletonExists ? "1" : "0"),
                    ("singleton_row_count", _heartbeatSingletonExists ? "1" : "0"),
                    ("extra_row_count", "0"),
                    ("heartbeat_sequence", _heartbeatSingletonExists ? "0" : "-1")
                ),
            ],
            var text when text.Contains("cdc:sqlserver:source-inventory") => SourceInventoryRows(),
            _ => throw new InvalidOperationException($"Unexpected SQL Server CDC query: {sql}"),
        };

        return Task.FromResult(rows);
    }

    private IReadOnlyList<IReadOnlyDictionary<string, string?>> JobHelpRows()
    {
        List<IReadOnlyDictionary<string, string?>> rows = [];

        if (_captureJobPresent)
        {
            rows.Add(
                Row(
                    ("job_type", "capture"),
                    ("job_name", "cdc.dms_test_capture"),
                    ("maxtrans", "500"),
                    ("maxscans", "10"),
                    ("continuous", "1"),
                    ("pollinginterval", "5"),
                    ("retention", ""),
                    ("threshold", "")
                )
            );
        }

        if (_cleanupJobPresent)
        {
            rows.Add(
                Row(
                    ("job_type", "cleanup"),
                    ("job_name", "cdc.dms_test_cleanup"),
                    ("maxtrans", ""),
                    ("maxscans", ""),
                    ("continuous", ""),
                    ("pollinginterval", ""),
                    ("retention", "4320"),
                    ("threshold", "5000")
                )
            );
        }

        return rows;
    }

    private IReadOnlyList<IReadOnlyDictionary<string, string?>> JobRuntimeRows()
    {
        List<IReadOnlyDictionary<string, string?>> rows = [];

        if (_captureJobPresent)
        {
            rows.Add(
                Row(
                    ("job_type", "capture"),
                    ("job_name", "cdc.dms_test_capture"),
                    ("job_id", "capture-job"),
                    ("enabled", _captureJobEnabled),
                    ("running", _captureJobRunning),
                    ("last_run_status", _captureJobLastRunStatus)
                )
            );
        }

        if (_cleanupJobPresent)
        {
            rows.Add(
                Row(
                    ("job_type", "cleanup"),
                    ("job_name", "cdc.dms_test_cleanup"),
                    ("job_id", "cleanup-job"),
                    ("enabled", _cleanupJobEnabled),
                    ("running", _cleanupJobRunning),
                    ("last_run_status", _cleanupJobLastRunStatus)
                )
            );
        }

        return rows;
    }

    private IReadOnlyList<IReadOnlyDictionary<string, string?>> SourceInventoryRows()
    {
        List<IReadOnlyDictionary<string, string?>> rows = [];
        foreach (var table in CdcProviderSetupContractTestData.BuildSqlServerRequiredSourceInventory())
        {
            if (table.TableKind == CdcSourceTableKind.CdcHeartbeat && !_heartbeatTableExists)
            {
                continue;
            }

            rows.AddRange(
                table.Columns.Select(column =>
                    Row(
                        ("table_schema", table.TableName.Schema.Value),
                        ("table_name", table.TableName.Name),
                        ("column_name", column.ColumnName.Value),
                        ("ordinal", column.Ordinal.ToString()),
                        ("provider_data_type", column.ProviderDataType),
                        ("is_nullable", column.IsNullable.ToString())
                    )
                )
            );
        }

        return rows;
    }

    private static IReadOnlyDictionary<string, string?> Row(params (string Key, string? Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value);
}
