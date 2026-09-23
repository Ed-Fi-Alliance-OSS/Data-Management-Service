// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;

/// <summary>
/// Shared setup for the DMS-1437 <c>dmscs.Job</c> schema fixtures (spec §3.1). Tenants created here are
/// removed in teardown, jobs and schedules first, since both foreign keys block deleting a referenced row.
/// </summary>
public abstract class JobSchemaTestBase : DatabaseTest
{
    protected const int CheckOrForeignKeyViolation = 547;
    protected const int UniqueIndexViolation = 2601;
    protected const int UniqueConstraintViolation = 2627;

    private readonly List<long> _tenantIds = [];

    [TearDown]
    public async Task DeleteCreatedTenants()
    {
        foreach (long tenantId in _tenantIds)
        {
            await Connection!.ExecuteAsync(
                """
                DELETE FROM dmscs.Job WHERE TenantId = @TenantId;
                DELETE FROM dmscs.JobSchedule WHERE TenantId = @TenantId;
                DELETE FROM dmscs.Tenant WHERE Id = @TenantId;
                """,
                new { TenantId = tenantId }
            );
        }
    }

    protected async Task<long> CreateTenantAsync()
    {
        long tenantId = await Connection!.ExecuteScalarAsync<long>(
            "INSERT INTO dmscs.Tenant (Name) OUTPUT inserted.Id VALUES (@Name);",
            new { Name = $"jobs-{Guid.NewGuid():N}" }
        );
        _tenantIds.Add(tenantId);
        return tenantId;
    }

    protected async Task<long> CreateScheduleAsync(long? tenantId = null) =>
        await Connection!.ExecuteScalarAsync<long>(
            """
            INSERT INTO dmscs.JobSchedule (
                TenantId, ScheduleType, JobType, PayloadVersion, Payload, IntervalMinutes, Enabled, NextRunAt)
            OUTPUT inserted.Id
            VALUES (@TenantId, @ScheduleType, N'DataStore.RefreshEducationOrganizations', 1, N'{}', 60, 1,
                SYSUTCDATETIME());
            """,
            new { TenantId = tenantId, ScheduleType = $"Probe.{Guid.NewGuid():N}" }
        );

    /// <summary>
    /// Inserts one job and returns the SQL Server error number it raised, or null when it was stored. Active
    /// jobs get <c>NextAttemptAt = now</c> unless <paramref name="withoutNextAttemptAt"/> is set.
    /// </summary>
    protected async Task<int?> TryInsertJobAsync(
        string? jobId = null,
        string status = "Pending",
        string payload = """{"dataStoreId":1}""",
        long? tenantId = null,
        long? sourceScheduleId = null,
        DateTime? scheduledOccurrence = null,
        int attemptCount = 0,
        bool withoutNextAttemptAt = false
    )
    {
        try
        {
            await using SqlCommand insert = new(
                """
                INSERT INTO dmscs.Job (
                    JobId, TenantId, JobType, PayloadVersion, Payload, SourceScheduleId, ScheduledOccurrence, Status,
                    NextAttemptAt, AttemptCount)
                VALUES (@JobId, @TenantId, N'DataStore.RefreshEducationOrganizations', 1, @Payload, @SourceScheduleId,
                    @ScheduledOccurrence, @Status,
                    CASE WHEN @WithoutNextAttemptAt = 1 THEN NULL ELSE SYSUTCDATETIME() END, @AttemptCount);
                """,
                Connection
            );
            insert.Parameters.AddWithValue("@JobId", jobId ?? Guid.NewGuid().ToString("N"));
            insert.Parameters.Add(Nullable("@TenantId", SqlDbType.BigInt, tenantId));
            insert.Parameters.AddWithValue("@Payload", payload);
            insert.Parameters.Add(Nullable("@SourceScheduleId", SqlDbType.BigInt, sourceScheduleId));
            insert.Parameters.Add(Nullable("@ScheduledOccurrence", SqlDbType.DateTime2, scheduledOccurrence));
            insert.Parameters.AddWithValue("@Status", status);
            insert.Parameters.AddWithValue("@WithoutNextAttemptAt", withoutNextAttemptAt);
            insert.Parameters.AddWithValue("@AttemptCount", attemptCount);
            await insert.ExecuteNonQueryAsync();
            return null;
        }
        catch (SqlException exception)
        {
            return exception.Number;
        }
    }

    private static SqlParameter Nullable(string name, SqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };
}

[TestFixture]
public class Given_the_Job_table : JobSchemaTestBase
{
    private ColumnShape[] _columns = [];
    private ConstraintShape[] _constraints = [];
    private IndexShape[] _indexes = [];
    private InsertedDefaults _defaults = null!;

    [SetUp]
    public async Task Setup()
    {
        _columns = (
            await Connection!.QueryAsync<ColumnShape>(
                """
                SELECT COLUMN_NAME AS Name,
                       DATA_TYPE AS DataType,
                       CAST(CASE WHEN IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS bit) AS IsNullable,
                       CHARACTER_MAXIMUM_LENGTH AS MaxLength,
                       CAST(COLUMNPROPERTY(OBJECT_ID(N'dmscs.Job'), COLUMN_NAME, 'IsIdentity') AS bit) AS IsIdentity
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'dmscs' AND TABLE_NAME = 'Job'
                ORDER BY ORDINAL_POSITION;
                """
            )
        ).ToArray();

        _constraints = (
            await Connection!.QueryAsync<ConstraintShape>(
                """
                SELECT object_info.name AS Name,
                       RTRIM(object_info.type) AS ConstraintType,
                       COALESCE(foreign_key.delete_referential_action_desc, N'') AS DeleteRule,
                       CAST(COALESCE(check_info.is_not_trusted, foreign_key.is_not_trusted, 0) AS bit) AS IsNotTrusted
                FROM sys.objects object_info
                LEFT JOIN sys.foreign_keys foreign_key ON foreign_key.object_id = object_info.object_id
                LEFT JOIN sys.check_constraints check_info ON check_info.object_id = object_info.object_id
                WHERE object_info.parent_object_id = OBJECT_ID(N'dmscs.Job')
                  AND object_info.type IN ('PK', 'UQ', 'F', 'C')
                ORDER BY object_info.name;
                """
            )
        ).ToArray();

        _indexes = (
            await Connection!.QueryAsync<IndexShape>(
                """
                SELECT index_info.name AS Name,
                       index_info.is_unique AS IsUnique,
                       (
                           SELECT STRING_AGG(column_info.name, ',') WITHIN GROUP (ORDER BY index_column.key_ordinal)
                           FROM sys.index_columns index_column
                           JOIN sys.columns column_info
                               ON column_info.object_id = index_column.object_id
                              AND column_info.column_id = index_column.column_id
                           WHERE index_column.object_id = index_info.object_id
                             AND index_column.index_id = index_info.index_id
                             AND index_column.is_included_column = 0
                       ) AS ColumnsCsv,
                       (
                           SELECT STRING_AGG(column_info.name, ',') WITHIN GROUP (ORDER BY index_column.index_column_id)
                           FROM sys.index_columns index_column
                           JOIN sys.columns column_info
                               ON column_info.object_id = index_column.object_id
                              AND column_info.column_id = index_column.column_id
                           WHERE index_column.object_id = index_info.object_id
                             AND index_column.index_id = index_info.index_id
                             AND index_column.is_included_column = 1
                       ) AS IncludedCsv,
                       index_info.filter_definition AS FilterDefinition
                FROM sys.indexes index_info
                WHERE index_info.object_id = OBJECT_ID(N'dmscs.Job')
                ORDER BY index_info.name;
                """
            )
        ).ToArray();

        _defaults = await Connection!.QuerySingleAsync<InsertedDefaults>(
            """
            INSERT INTO dmscs.Job (JobId, JobType, PayloadVersion, Payload, Status, NextAttemptAt)
            OUTPUT inserted.Id,
                   inserted.AttemptCount,
                   inserted.FencingToken,
                   ABS(DATEDIFF_BIG(millisecond, inserted.CreatedAt, SYSUTCDATETIME())) AS CreatedAtMillisecondsFromUtcNow,
                   CAST(CASE WHEN inserted.FinishedAt IS NULL AND inserted.LeaseOwner IS NULL
                                  AND inserted.LeaseExpiresAt IS NULL AND inserted.ErrorMessage IS NULL
                             THEN 1 ELSE 0 END AS bit) AS OutcomeAndLeaseEmpty
            VALUES (@JobId, N'DataStore.RefreshEducationOrganizations', 1, N'{}', N'Pending', SYSUTCDATETIME());
            """,
            new { JobId = Guid.NewGuid().ToString("N") }
        );
    }

    [Test]
    public void It_declares_the_planned_columns_in_order()
    {
        _columns
            .Should()
            .Equal(
                new ColumnShape("Id", "bigint", false, null, true),
                new ColumnShape("JobId", "nvarchar", false, 150, false),
                new ColumnShape("TenantId", "bigint", true, null, false),
                new ColumnShape("JobType", "nvarchar", false, 100, false),
                new ColumnShape("PayloadVersion", "smallint", false, null, false),
                new ColumnShape("Payload", "nvarchar", false, 4000, false),
                new ColumnShape("SourceScheduleId", "bigint", true, null, false),
                new ColumnShape("ScheduledOccurrence", "datetime2", true, null, false),
                new ColumnShape("Status", "nvarchar", false, 20, false),
                new ColumnShape("CreatedAt", "datetime2", false, null, false),
                new ColumnShape("FinishedAt", "datetime2", true, null, false),
                new ColumnShape("NextAttemptAt", "datetime2", true, null, false),
                new ColumnShape("LeaseExpiresAt", "datetime2", true, null, false),
                new ColumnShape("ErrorMessage", "nvarchar", true, 1000, false),
                new ColumnShape("AttemptCount", "int", false, null, false),
                new ColumnShape("LeaseOwner", "nvarchar", true, 200, false),
                new ColumnShape("FencingToken", "bigint", false, null, false),
                new ColumnShape("CreatedBy", "nvarchar", true, 256, false),
                new ColumnShape("LastModifiedAt", "datetime2", true, null, false),
                new ColumnShape("ModifiedBy", "nvarchar", true, 256, false)
            );
    }

    [Test]
    public async Task It_uses_a_binary_collation_for_the_job_id_type_and_status()
    {
        string[] binaryCollatedColumns = (
            await Connection!.QueryAsync<string>(
                """
                SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'dmscs' AND TABLE_NAME = 'Job' AND COLLATION_NAME = 'Latin1_General_BIN2'
                ORDER BY ORDINAL_POSITION;
                """
            )
        ).ToArray();

        binaryCollatedColumns.Should().Equal("JobId", "JobType", "Status");
    }

    [Test]
    public void It_declares_trusted_key_unique_foreign_key_and_check_constraints()
    {
        _constraints
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    new ConstraintShape("CK_Job_AttemptCount", "C", "", false),
                    new ConstraintShape("CK_Job_NextAttemptAt_Active", "C", "", false),
                    new ConstraintShape("CK_Job_Occurrence_Pairing", "C", "", false),
                    new ConstraintShape("CK_Job_Payload_Object", "C", "", false),
                    new ConstraintShape("CK_Job_Status", "C", "", false),
                    new ConstraintShape("FK_Job_JobSchedule", "F", "NO_ACTION", false),
                    new ConstraintShape("FK_Job_Tenant", "F", "NO_ACTION", false),
                    new ConstraintShape("PK_Job", "PK", "", false),
                    new ConstraintShape("UX_Job_JobId", "UQ", "", false),
                }
            );
    }

    [Test]
    public void It_declares_the_claim_retention_occurrence_and_lookup_indexes()
    {
        _indexes
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    new IndexShape(
                        "IX_Job_Claim",
                        false,
                        "NextAttemptAt,Id",
                        "Status,LeaseExpiresAt,AttemptCount",
                        "([Status] IN (N'Pending', N'InProgress'))"
                    ),
                    new IndexShape(
                        "IX_Job_Retention",
                        false,
                        "Status,FinishedAt",
                        null,
                        "([Status] IN (N'Completed', N'Error'))"
                    ),
                    new IndexShape("IX_Job_TenantId", false, "TenantId", null, null),
                    new IndexShape("PK_Job", true, "Id", null, null),
                    new IndexShape("UX_Job_JobId", true, "JobId", null, null),
                    new IndexShape(
                        "UX_Job_SourceScheduleId_ScheduledOccurrence",
                        true,
                        "SourceScheduleId,ScheduledOccurrence",
                        null,
                        "([SourceScheduleId] IS NOT NULL AND [ScheduledOccurrence] IS NOT NULL)"
                    ),
                }
            );
    }

    [Test]
    public void It_generates_the_identity_and_the_attempt_fencing_and_audit_defaults()
    {
        _defaults.Id.Should().BePositive();
        _defaults.AttemptCount.Should().Be(0);
        _defaults.FencingToken.Should().Be(0);
        _defaults
            .CreatedAtMillisecondsFromUtcNow.Should()
            .BeLessThan(5_000, "CreatedAt defaults to database UTC time");
        _defaults.OutcomeAndLeaseEmpty.Should().BeTrue();
    }

    private sealed record ColumnShape(
        string Name,
        string DataType,
        bool IsNullable,
        int? MaxLength,
        bool IsIdentity
    );

    private sealed record ConstraintShape(
        string Name,
        string ConstraintType,
        string DeleteRule,
        bool IsNotTrusted
    );

    private sealed record IndexShape(
        string Name,
        bool IsUnique,
        string ColumnsCsv,
        string? IncludedCsv,
        string? FilterDefinition
    );

    private sealed record InsertedDefaults(
        long Id,
        int AttemptCount,
        long FencingToken,
        long CreatedAtMillisecondsFromUtcNow,
        bool OutcomeAndLeaseEmpty
    );
}

[TestFixture]
public class Given_manual_and_scheduled_jobs : JobSchemaTestBase
{
    private static readonly DateTime _occurrence = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Unspecified);

    private int? _firstManualJob;
    private int? _secondManualJob;
    private int? _scheduledJob;
    private int? _sameOccurrenceAgain;
    private int? _nextOccurrence;
    private int? _scheduleWithoutOccurrence;
    private int? _occurrenceWithoutSchedule;

    [SetUp]
    public async Task Setup()
    {
        long scheduleId = await CreateScheduleAsync();

        _firstManualJob = await TryInsertJobAsync();
        _secondManualJob = await TryInsertJobAsync();
        _scheduledJob = await TryInsertJobAsync(
            sourceScheduleId: scheduleId,
            scheduledOccurrence: _occurrence
        );
        _sameOccurrenceAgain = await TryInsertJobAsync(
            sourceScheduleId: scheduleId,
            scheduledOccurrence: _occurrence
        );
        _nextOccurrence = await TryInsertJobAsync(
            sourceScheduleId: scheduleId,
            scheduledOccurrence: _occurrence.AddMinutes(60)
        );
        _scheduleWithoutOccurrence = await TryInsertJobAsync(sourceScheduleId: scheduleId);
        _occurrenceWithoutSchedule = await TryInsertJobAsync(scheduledOccurrence: _occurrence);
    }

    [Test]
    public void It_lets_manual_jobs_without_an_occurrence_coexist()
    {
        _firstManualJob.Should().BeNull();
        _secondManualJob.Should().BeNull();
    }

    [Test]
    public void It_rejects_a_second_job_for_the_same_scheduled_occurrence()
    {
        _scheduledJob.Should().BeNull();
        _sameOccurrenceAgain.Should().Be(UniqueIndexViolation);
    }

    [Test]
    public void It_accepts_the_next_occurrence_of_the_same_schedule() => _nextOccurrence.Should().BeNull();

    [Test]
    public void It_rejects_a_half_set_occurrence_pair()
    {
        _scheduleWithoutOccurrence.Should().Be(CheckOrForeignKeyViolation);
        _occurrenceWithoutSchedule.Should().Be(CheckOrForeignKeyViolation);
    }
}

[TestFixture]
public class Given_job_identifiers : JobSchemaTestBase
{
    private const string JobId = "3f2a9c1e7b5d4e6f8a0b1c2d3e4f5a6b";

    private int? _firstInsert;
    private int? _duplicateInsert;
    private long _exactMatches;
    private long _trailingSpaceMatches;
    private long _upperCaseMatches;
    private long _trailingSpaceMatchesWithoutLengthGuard;

    [SetUp]
    public async Task Setup()
    {
        _firstInsert = await TryInsertJobAsync(JobId);
        _duplicateInsert = await TryInsertJobAsync(JobId);

        _exactMatches = await CountExactAsync(JobId);
        _trailingSpaceMatches = await CountExactAsync(JobId + " ");
        _upperCaseMatches = await CountExactAsync(JobId.ToUpperInvariant());
        _trailingSpaceMatchesWithoutLengthGuard = await Connection!.ExecuteScalarAsync<long>(
            "SELECT COUNT_BIG(*) FROM dmscs.Job WHERE JobId = @JobId;",
            new { JobId = JobId + " " }
        );
    }

    /// <summary>The D-14 lookup: binary equality plus an exact length check.</summary>
    private Task<long> CountExactAsync(string jobId) =>
        Connection!.ExecuteScalarAsync<long>(
            "SELECT COUNT_BIG(*) FROM dmscs.Job WHERE JobId = @JobId AND DATALENGTH(JobId) = DATALENGTH(@JobId);",
            new { JobId = jobId }
        );

    [Test]
    public void It_rejects_a_duplicate_job_id()
    {
        _firstInsert.Should().BeNull();
        _duplicateInsert.Should().Be(UniqueConstraintViolation);
    }

    [Test]
    public void It_matches_a_job_id_exactly()
    {
        _exactMatches.Should().Be(1);
        _trailingSpaceMatches.Should().Be(0);
        _upperCaseMatches.Should().Be(0);
    }

    [Test]
    public void It_needs_the_length_guard_because_equality_ignores_trailing_spaces() =>
        _trailingSpaceMatchesWithoutLengthGuard.Should().Be(1);
}

[TestFixture]
public class Given_job_statuses_and_attempt_counts : JobSchemaTestBase
{
    private readonly Dictionary<string, int?> _statusOutcomes = [];
    private int? _negativeAttemptCount;
    private int? _zeroAttemptCount;

    [SetUp]
    public async Task Setup()
    {
        foreach (
            string status in new[] { "Pending", "InProgress", "Completed", "Error", "pending", "Unknown", "" }
        )
        {
            _statusOutcomes[status] = await TryInsertJobAsync(status: status);
        }

        _negativeAttemptCount = await TryInsertJobAsync(attemptCount: -1);
        _zeroAttemptCount = await TryInsertJobAsync(attemptCount: 0);
    }

    [Test]
    public void It_accepts_the_four_statuses()
    {
        foreach (string status in new[] { "Pending", "InProgress", "Completed", "Error" })
        {
            _statusOutcomes[status].Should().BeNull(status);
        }
    }

    [Test]
    public void It_rejects_any_other_status_including_a_case_variant()
    {
        foreach (string status in new[] { "pending", "Unknown", "" })
        {
            _statusOutcomes[status]
                .Should()
                .Be(CheckOrForeignKeyViolation, $"'{status}' is not a job status");
        }
    }

    [Test]
    public void It_rejects_a_negative_attempt_count()
    {
        _zeroAttemptCount.Should().BeNull();
        _negativeAttemptCount.Should().Be(CheckOrForeignKeyViolation);
    }
}

[TestFixture]
public class Given_jobs_without_a_next_attempt_time : JobSchemaTestBase
{
    private readonly Dictionary<string, int?> _insertOutcomes = [];
    private int? _updateToNull;

    [SetUp]
    public async Task Setup()
    {
        foreach (string status in new[] { "Pending", "InProgress", "Completed", "Error" })
        {
            _insertOutcomes[status] = await TryInsertJobAsync(status: status, withoutNextAttemptAt: true);
        }

        string jobId = Guid.NewGuid().ToString("N");
        await TryInsertJobAsync(jobId);
        try
        {
            await Connection!.ExecuteAsync(
                "UPDATE dmscs.Job SET NextAttemptAt = NULL WHERE JobId = @JobId;",
                new { JobId = jobId }
            );
        }
        catch (SqlException exception)
        {
            _updateToNull = exception.Number;
        }
    }

    [Test]
    public void It_rejects_an_active_job_without_a_next_attempt_time_on_insert()
    {
        _insertOutcomes["Pending"].Should().Be(CheckOrForeignKeyViolation);
        _insertOutcomes["InProgress"].Should().Be(CheckOrForeignKeyViolation);
    }

    [Test]
    public void It_rejects_clearing_the_next_attempt_time_of_an_active_job() =>
        _updateToNull.Should().Be(CheckOrForeignKeyViolation);

    [Test]
    public void It_accepts_a_finished_job_without_a_next_attempt_time()
    {
        _insertOutcomes["Completed"].Should().BeNull();
        _insertOutcomes["Error"].Should().BeNull();
    }
}

[TestFixture]
public class Given_job_payloads : JobSchemaTestBase
{
    private static readonly string[] _objectPayloads =
    [
        "{}",
        " {}",
        "\t{}",
        "\n{}",
        "\r\n{}",
        """{"dataStoreId":1}""",
    ];

    private static readonly string[] _nonObjectPayloads = ["[]", "1", "\"x\"", "null", "true"];

    private static readonly string[] _invalidPayloads = ["{", "", "not json"];

    private readonly Dictionary<string, int?> _outcomes = [];

    [SetUp]
    public async Task Setup()
    {
        foreach (string payload in _objectPayloads.Concat(_nonObjectPayloads).Concat(_invalidPayloads))
        {
            _outcomes[payload] = await TryInsertJobAsync(payload: payload);
        }
    }

    [Test]
    public void It_accepts_a_json_object_after_any_json_whitespace_prefix()
    {
        foreach (string payload in _objectPayloads)
        {
            _outcomes[payload].Should().BeNull($"'{payload}' is a JSON object");
        }
    }

    [Test]
    public void It_rejects_a_json_value_that_is_not_an_object()
    {
        foreach (string payload in _nonObjectPayloads)
        {
            _outcomes[payload].Should().Be(CheckOrForeignKeyViolation, $"'{payload}' is not a JSON object");
        }
    }

    [Test]
    public void It_rejects_text_that_is_not_json()
    {
        foreach (string payload in _invalidPayloads)
        {
            _outcomes[payload].Should().Be(CheckOrForeignKeyViolation, $"'{payload}' is not valid JSON");
        }
    }
}

[TestFixture]
public partial class Given_a_tenant_and_a_schedule_referenced_by_jobs : JobSchemaTestBase
{
    private int? _scheduledJobInsert;
    private int? _manualJobInsert;
    private string? _scheduleDelete;
    private string? _tenantDelete;

    [SetUp]
    public async Task Setup()
    {
        long scheduleId = await CreateScheduleAsync();
        _scheduledJobInsert = await TryInsertJobAsync(
            sourceScheduleId: scheduleId,
            scheduledOccurrence: new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Unspecified)
        );

        long tenantId = await CreateTenantAsync();
        _manualJobInsert = await TryInsertJobAsync(tenantId: tenantId);

        _scheduleDelete = await ViolatedConstraintAsync(
            "DELETE FROM dmscs.JobSchedule WHERE Id = @Id;",
            new { Id = scheduleId }
        );
        _tenantDelete = await ViolatedConstraintAsync(
            "DELETE FROM dmscs.Tenant WHERE Id = @Id;",
            new { Id = tenantId }
        );
    }

    /// <summary>Runs a statement and returns "Number:ConstraintName" when it fails, or null.</summary>
    private async Task<string?> ViolatedConstraintAsync(string sql, object parameters)
    {
        try
        {
            await Connection!.ExecuteAsync(sql, parameters);
            return null;
        }
        catch (SqlException exception)
        {
            return $"{exception.Number}:{ConstraintName().Match(exception.Message).Groups[1].Value}";
        }
    }

    [GeneratedRegex("constraint \"([^\"]+)\"")]
    private static partial Regex ConstraintName();

    [Test]
    public void It_restricts_deleting_a_schedule_that_has_jobs()
    {
        _scheduledJobInsert.Should().BeNull();
        _scheduleDelete.Should().Be($"{CheckOrForeignKeyViolation}:FK_Job_JobSchedule");
    }

    [Test]
    public void It_restricts_deleting_a_tenant_that_has_jobs()
    {
        _manualJobInsert.Should().BeNull();
        _tenantDelete.Should().Be($"{CheckOrForeignKeyViolation}:FK_Job_Tenant");
    }
}
