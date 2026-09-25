// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;

/// <summary>
/// Shared setup for the DMS-1437 <c>dmscs.Job</c> schema fixtures (spec §3.1). Tenants created here are
/// removed in teardown because the PostgreSQL Respawn list does not include <c>Tenant</c>; the jobs and
/// schedules that reference them go first, since both foreign keys restrict deletes.
/// </summary>
public abstract class JobSchemaTestBase : DatabaseTest
{
    protected const string CheckViolation = "23514";
    protected const string UniqueViolation = "23505";
    protected const string ForeignKeyViolation = "23503";
    protected const string InvalidTextRepresentation = "22P02";

    private readonly List<long> _tenantIds = [];

    [TearDown]
    public async Task DeleteCreatedTenants()
    {
        if (_tenantIds.Count == 0)
        {
            return;
        }

        await Connection!.ExecuteAsync(
            """
            DELETE FROM "dmscs"."Job" WHERE "TenantId" = ANY(@TenantIds);
            DELETE FROM "dmscs"."JobSchedule" WHERE "TenantId" = ANY(@TenantIds);
            DELETE FROM "dmscs"."Tenant" WHERE "Id" = ANY(@TenantIds);
            """,
            new { TenantIds = _tenantIds.ToArray() }
        );
    }

    protected async Task<long> CreateTenantAsync()
    {
        long tenantId = await Connection!.ExecuteScalarAsync<long>(
            """INSERT INTO "dmscs"."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""",
            new { Name = $"jobs-{Guid.NewGuid():N}" }
        );
        _tenantIds.Add(tenantId);
        return tenantId;
    }

    protected async Task<long> CreateScheduleAsync(long? tenantId = null) =>
        await Connection!.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dmscs"."JobSchedule" (
                "TenantId", "ScheduleType", "JobType", "PayloadVersion", "Payload", "IntervalMinutes", "Enabled",
                "NextRunAt")
            VALUES (@TenantId, @ScheduleType, 'DataStore.RefreshEducationOrganizations', 1, '{}', 60, TRUE,
                (now() AT TIME ZONE 'UTC'))
            RETURNING "Id";
            """,
            new { TenantId = tenantId, ScheduleType = $"Probe.{Guid.NewGuid():N}" }
        );

    /// <summary>
    /// Inserts one job and returns the PostgreSQL error code it raised, or null when it was stored. Active
    /// jobs get <c>NextAttemptAt = now</c> unless <paramref name="withoutNextAttemptAt"/> is set.
    /// </summary>
    protected async Task<string?> TryInsertJobAsync(
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
            await using NpgsqlCommand insert = new(
                """
                INSERT INTO "dmscs"."Job" (
                    "JobId", "TenantId", "JobType", "PayloadVersion", "Payload", "SourceScheduleId",
                    "ScheduledOccurrence", "Status", "NextAttemptAt", "AttemptCount")
                VALUES (@JobId, @TenantId, 'DataStore.RefreshEducationOrganizations', 1, @Payload, @SourceScheduleId,
                    @ScheduledOccurrence, @Status,
                    CASE WHEN @WithoutNextAttemptAt THEN NULL ELSE (now() AT TIME ZONE 'UTC') END, @AttemptCount);
                """,
                Connection
            );
            insert.Parameters.AddWithValue("JobId", jobId ?? Guid.NewGuid().ToString("N"));
            insert.Parameters.Add(Nullable("TenantId", NpgsqlDbType.Bigint, tenantId));
            insert.Parameters.AddWithValue("Payload", payload);
            insert.Parameters.Add(Nullable("SourceScheduleId", NpgsqlDbType.Bigint, sourceScheduleId));
            insert.Parameters.Add(
                Nullable("ScheduledOccurrence", NpgsqlDbType.Timestamp, scheduledOccurrence)
            );
            insert.Parameters.AddWithValue("Status", status);
            insert.Parameters.AddWithValue("WithoutNextAttemptAt", withoutNextAttemptAt);
            insert.Parameters.AddWithValue("AttemptCount", attemptCount);
            await insert.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
    }

    /// <summary>Runs a statement and returns "SqlState:ConstraintName" when it fails, or null.</summary>
    protected async Task<string?> ViolatedConstraintAsync(string sql, object parameters)
    {
        try
        {
            await Connection!.ExecuteAsync(sql, parameters);
            return null;
        }
        catch (PostgresException exception)
        {
            return $"{exception.SqlState}:{exception.ConstraintName}";
        }
    }

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, object? value) =>
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
                SELECT column_name AS Name,
                       data_type AS DataType,
                       is_nullable = 'YES' AS IsNullable,
                       character_maximum_length AS MaxLength,
                       is_identity = 'YES' AS IsIdentity
                FROM information_schema.columns
                WHERE table_schema = 'dmscs' AND table_name = 'Job'
                ORDER BY ordinal_position;
                """
            )
        ).ToArray();

        _constraints = (
            await Connection!.QueryAsync<ConstraintShape>(
                """
                SELECT constraint_info.conname AS Name,
                       constraint_info.contype::text AS ConstraintType,
                       constraint_info.confdeltype::text AS DeleteRule
                FROM pg_constraint constraint_info
                WHERE constraint_info.conrelid = '"dmscs"."Job"'::regclass
                ORDER BY constraint_info.conname;
                """
            )
        ).ToArray();

        _indexes = (
            await Connection!.QueryAsync<IndexShape>(
                """
                SELECT index_info.relname AS Name,
                       index_catalog.indisunique AS IsUnique,
                       (
                           SELECT string_agg(attribute_info.attname, ',' ORDER BY key_columns.ordinality)
                           FROM unnest(index_catalog.indkey) WITH ORDINALITY AS key_columns(attnum, ordinality)
                           JOIN pg_attribute attribute_info
                               ON attribute_info.attrelid = index_catalog.indrelid
                              AND attribute_info.attnum = key_columns.attnum
                       ) AS ColumnsCsv,
                       pg_get_expr(index_catalog.indpred, index_catalog.indrelid) AS Predicate
                FROM pg_index index_catalog
                JOIN pg_class index_info ON index_info.oid = index_catalog.indexrelid
                WHERE index_catalog.indrelid = '"dmscs"."Job"'::regclass
                ORDER BY index_info.relname;
                """
            )
        ).ToArray();

        _defaults = await Connection!.QuerySingleAsync<InsertedDefaults>(
            """
            INSERT INTO "dmscs"."Job" ("JobId", "JobType", "PayloadVersion", "Payload", "Status", "NextAttemptAt")
            VALUES (@JobId, 'DataStore.RefreshEducationOrganizations', 1, '{}', 'Pending', (now() AT TIME ZONE 'UTC'))
            RETURNING "Id",
                      "AttemptCount",
                      "FencingToken",
                      abs(extract(epoch from ("CreatedAt" - (now() AT TIME ZONE 'UTC')))) AS CreatedAtSecondsFromUtcNow,
                      "FinishedAt" IS NULL AND "LeaseOwner" IS NULL AND "LeaseExpiresAt" IS NULL
                          AND "ErrorMessage" IS NULL AS OutcomeAndLeaseEmpty;
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
                new ColumnShape("JobId", "character varying", false, 150, false),
                new ColumnShape("TenantId", "bigint", true, null, false),
                new ColumnShape("JobType", "character varying", false, 100, false),
                new ColumnShape("PayloadVersion", "smallint", false, null, false),
                new ColumnShape("Payload", "character varying", false, 4000, false),
                new ColumnShape("SourceScheduleId", "bigint", true, null, false),
                new ColumnShape("ScheduledOccurrence", "timestamp without time zone", true, null, false),
                new ColumnShape("Status", "character varying", false, 20, false),
                new ColumnShape("CreatedAt", "timestamp without time zone", false, null, false),
                new ColumnShape("FinishedAt", "timestamp without time zone", true, null, false),
                new ColumnShape("NextAttemptAt", "timestamp without time zone", true, null, false),
                new ColumnShape("LeaseExpiresAt", "timestamp without time zone", true, null, false),
                new ColumnShape("ErrorMessage", "character varying", true, 1000, false),
                new ColumnShape("AttemptCount", "integer", false, null, false),
                new ColumnShape("LeaseOwner", "character varying", true, 200, false),
                new ColumnShape("FencingToken", "bigint", false, null, false),
                new ColumnShape("CreatedBy", "character varying", true, 256, false),
                new ColumnShape("LastModifiedAt", "timestamp without time zone", true, null, false),
                new ColumnShape("ModifiedBy", "character varying", true, 256, false)
            );
    }

    [Test]
    public void It_declares_the_key_unique_foreign_key_and_check_constraints()
    {
        _constraints
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    new ConstraintShape("CK_Job_AttemptCount", "c", " "),
                    new ConstraintShape("CK_Job_NextAttemptAt_Active", "c", " "),
                    new ConstraintShape("CK_Job_Occurrence_Pairing", "c", " "),
                    new ConstraintShape("CK_Job_Payload_Object", "c", " "),
                    new ConstraintShape("CK_Job_Status", "c", " "),
                    new ConstraintShape("FK_Job_JobSchedule", "f", "r"),
                    new ConstraintShape("FK_Job_Tenant", "f", "r"),
                    new ConstraintShape("PK_Job", "p", " "),
                    new ConstraintShape("UX_Job_JobId", "u", " "),
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
                        "((\"Status\")::text = ANY ((ARRAY['Pending'::character varying, 'InProgress'::character varying])::text[]))"
                    ),
                    new IndexShape(
                        "IX_Job_Retention",
                        false,
                        "Status,FinishedAt",
                        "((\"Status\")::text = ANY ((ARRAY['Completed'::character varying, 'Error'::character varying])::text[]))"
                    ),
                    new IndexShape("IX_Job_TenantId", false, "TenantId", null),
                    new IndexShape("PK_Job", true, "Id", null),
                    new IndexShape("UX_Job_JobId", true, "JobId", null),
                    new IndexShape(
                        "UX_Job_SourceScheduleId_ScheduledOccurrence",
                        true,
                        "SourceScheduleId,ScheduledOccurrence",
                        "((\"SourceScheduleId\" IS NOT NULL) AND (\"ScheduledOccurrence\" IS NOT NULL))"
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
            .CreatedAtSecondsFromUtcNow.Should()
            .BeLessThan(5, "CreatedAt defaults to database UTC time");
        _defaults.OutcomeAndLeaseEmpty.Should().BeTrue();
    }

    private sealed record ColumnShape(
        string Name,
        string DataType,
        bool IsNullable,
        int? MaxLength,
        bool IsIdentity
    );

    private sealed record ConstraintShape(string Name, string ConstraintType, string DeleteRule);

    private sealed record IndexShape(string Name, bool IsUnique, string ColumnsCsv, string? Predicate);

    private sealed record InsertedDefaults(
        long Id,
        int AttemptCount,
        long FencingToken,
        decimal CreatedAtSecondsFromUtcNow,
        bool OutcomeAndLeaseEmpty
    );
}

[TestFixture]
public class Given_manual_and_scheduled_jobs : JobSchemaTestBase
{
    private static readonly DateTime _occurrence = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Unspecified);

    private string? _firstManualJob;
    private string? _secondManualJob;
    private string? _scheduledJob;
    private string? _sameOccurrenceAgain;
    private string? _nextOccurrence;
    private string? _scheduleWithoutOccurrence;
    private string? _occurrenceWithoutSchedule;

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
        _sameOccurrenceAgain.Should().Be(UniqueViolation);
    }

    [Test]
    public void It_accepts_the_next_occurrence_of_the_same_schedule() => _nextOccurrence.Should().BeNull();

    [Test]
    public void It_rejects_a_half_set_occurrence_pair()
    {
        _scheduleWithoutOccurrence.Should().Be(CheckViolation);
        _occurrenceWithoutSchedule.Should().Be(CheckViolation);
    }
}

[TestFixture]
public class Given_job_identifiers : JobSchemaTestBase
{
    private const string JobId = "3f2a9c1e7b5d4e6f8a0b1c2d3e4f5a6b";

    private string? _firstInsert;
    private string? _duplicateInsert;
    private long _exactMatches;
    private long _trailingSpaceMatches;
    private long _upperCaseMatches;

    [SetUp]
    public async Task Setup()
    {
        _firstInsert = await TryInsertJobAsync(JobId);
        _duplicateInsert = await TryInsertJobAsync(JobId);

        _exactMatches = await CountByJobIdAsync(JobId);
        _trailingSpaceMatches = await CountByJobIdAsync(JobId + " ");
        _upperCaseMatches = await CountByJobIdAsync(JobId.ToUpperInvariant());
    }

    private Task<long> CountByJobIdAsync(string jobId) =>
        Connection!.ExecuteScalarAsync<long>(
            """SELECT count(*) FROM "dmscs"."Job" WHERE "JobId" = @JobId;""",
            new { JobId = jobId }
        );

    [Test]
    public void It_rejects_a_duplicate_job_id()
    {
        _firstInsert.Should().BeNull();
        _duplicateInsert.Should().Be(UniqueViolation);
    }

    [Test]
    public void It_matches_a_job_id_exactly()
    {
        _exactMatches.Should().Be(1);
        _trailingSpaceMatches.Should().Be(0);
        _upperCaseMatches.Should().Be(0);
    }
}

[TestFixture]
public class Given_job_statuses_and_attempt_counts : JobSchemaTestBase
{
    private readonly Dictionary<string, string?> _statusOutcomes = [];
    private string? _negativeAttemptCount;
    private string? _zeroAttemptCount;

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
            _statusOutcomes[status].Should().Be(CheckViolation, $"'{status}' is not a job status");
        }
    }

    [Test]
    public void It_rejects_a_negative_attempt_count()
    {
        _zeroAttemptCount.Should().BeNull();
        _negativeAttemptCount.Should().Be(CheckViolation);
    }
}

[TestFixture]
public class Given_statuses_with_a_trailing_space : JobSchemaTestBase
{
    private static readonly string[] _statuses = ["Pending", "InProgress", "Completed", "Error"];

    private readonly Dictionary<string, string?> _exactInserts = [];
    private readonly Dictionary<string, string?> _paddedInserts = [];
    private readonly Dictionary<string, string?> _paddedUpdates = [];

    [SetUp]
    public async Task Setup()
    {
        foreach (string status in _statuses)
        {
            _paddedInserts[status] = await ViolatedConstraintAsync(
                """
                INSERT INTO "dmscs"."Job" ("JobId", "JobType", "PayloadVersion", "Payload", "Status", "NextAttemptAt")
                VALUES (@JobId, 'DataStore.RefreshEducationOrganizations', 1, '{}', @Status, (now() AT TIME ZONE 'UTC'));
                """,
                new { JobId = Guid.NewGuid().ToString("N"), Status = status + " " }
            );

            string jobId = Guid.NewGuid().ToString("N");
            _exactInserts[status] = await TryInsertJobAsync(jobId, status: status);
            _paddedUpdates[status] = await ViolatedConstraintAsync(
                """UPDATE "dmscs"."Job" SET "Status" = @Status WHERE "JobId" = @JobId;""",
                new { JobId = jobId, Status = status + " " }
            );
        }
    }

    [Test]
    public void It_rejects_inserting_each_status_with_a_trailing_space()
    {
        foreach (string status in _statuses)
        {
            _paddedInserts[status]
                .Should()
                .Be($"{CheckViolation}:CK_Job_Status", $"'{status} ' is not a job status");
        }
    }

    [Test]
    public void It_rejects_updating_a_job_to_each_status_with_a_trailing_space()
    {
        foreach (string status in _statuses)
        {
            _exactInserts[status].Should().BeNull(status);
            _paddedUpdates[status]
                .Should()
                .Be($"{CheckViolation}:CK_Job_Status", $"'{status} ' is not a job status");
        }
    }
}

[TestFixture]
public class Given_jobs_without_a_next_attempt_time : JobSchemaTestBase
{
    private readonly Dictionary<string, string?> _insertOutcomes = [];
    private string? _updateToNull;

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
                """UPDATE "dmscs"."Job" SET "NextAttemptAt" = NULL WHERE "JobId" = @JobId;""",
                new { JobId = jobId }
            );
        }
        catch (PostgresException exception)
        {
            _updateToNull = exception.SqlState;
        }
    }

    [Test]
    public void It_rejects_an_active_job_without_a_next_attempt_time_on_insert()
    {
        _insertOutcomes["Pending"].Should().Be(CheckViolation);
        _insertOutcomes["InProgress"].Should().Be(CheckViolation);
    }

    [Test]
    public void It_rejects_clearing_the_next_attempt_time_of_an_active_job() =>
        _updateToNull.Should().Be(CheckViolation);

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

    private readonly Dictionary<string, string?> _outcomes = [];

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
            _outcomes[payload].Should().Be(CheckViolation, $"'{payload}' is not a JSON object");
        }
    }

    [Test]
    public void It_rejects_text_that_is_not_json()
    {
        foreach (string payload in _invalidPayloads)
        {
            _outcomes[payload]
                .Should()
                .Be(InvalidTextRepresentation, $"'{payload}' does not parse, so the jsonb cast fails");
        }
    }
}

[TestFixture]
public class Given_a_tenant_and_a_schedule_referenced_by_jobs : JobSchemaTestBase
{
    private string? _scheduledJobInsert;
    private string? _manualJobInsert;
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
            """DELETE FROM "dmscs"."JobSchedule" WHERE "Id" = @Id;""",
            new { Id = scheduleId }
        );
        _tenantDelete = await ViolatedConstraintAsync(
            """DELETE FROM "dmscs"."Tenant" WHERE "Id" = @Id;""",
            new { Id = tenantId }
        );
    }

    [Test]
    public void It_restricts_deleting_a_schedule_that_has_jobs()
    {
        _scheduledJobInsert.Should().BeNull();
        _scheduleDelete.Should().Be($"{ForeignKeyViolation}:FK_Job_JobSchedule");
    }

    [Test]
    public void It_restricts_deleting_a_tenant_that_has_jobs()
    {
        _manualJobInsert.Should().BeNull();
        _tenantDelete.Should().Be($"{ForeignKeyViolation}:FK_Job_Tenant");
    }
}
