// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;

/// <summary>
/// Shared setup for the DMS-1437 <c>dmscs.JobSchedule</c> schema fixtures (spec §3.2, D-10). Tenants
/// created here are removed in teardown because the PostgreSQL Respawn list does not include
/// <c>Tenant</c>; the schedules that reference them go first, since the foreign key restricts deletes.
/// </summary>
public abstract class JobScheduleSchemaTestBase : DatabaseTest
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

    /// <summary>
    /// Inserts one schedule and returns the PostgreSQL error code it raised, or null when it was stored.
    /// </summary>
    protected async Task<string?> TryInsertScheduleAsync(
        long? tenantId,
        string scheduleType,
        string payload = """{"dataStoreId":1}""",
        int intervalMinutes = 60,
        bool enabled = true
    )
    {
        try
        {
            await Connection!.ExecuteAsync(
                """
                INSERT INTO "dmscs"."JobSchedule" (
                    "TenantId", "ScheduleType", "JobType", "PayloadVersion", "Payload", "IntervalMinutes",
                    "Enabled", "NextRunAt")
                VALUES (@TenantId, @ScheduleType, 'DataStore.RefreshEducationOrganizations', 1, @Payload,
                    @IntervalMinutes, @Enabled, (now() AT TIME ZONE 'UTC'));
                """,
                new
                {
                    TenantId = tenantId,
                    ScheduleType = scheduleType,
                    Payload = payload,
                    IntervalMinutes = intervalMinutes,
                    Enabled = enabled,
                }
            );
            return null;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
    }
}

[TestFixture]
public class Given_the_JobSchedule_table : JobScheduleSchemaTestBase
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
                WHERE table_schema = 'dmscs' AND table_name = 'JobSchedule'
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
                WHERE constraint_info.conrelid = '"dmscs"."JobSchedule"'::regclass
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
                WHERE index_catalog.indrelid = '"dmscs"."JobSchedule"'::regclass
                ORDER BY index_info.relname;
                """
            )
        ).ToArray();

        _defaults = await Connection!.QuerySingleAsync<InsertedDefaults>(
            """
            INSERT INTO "dmscs"."JobSchedule" (
                "ScheduleType", "JobType", "PayloadVersion", "Payload", "IntervalMinutes", "Enabled", "NextRunAt")
            VALUES ('Probe.Defaults', 'DataStore.RefreshEducationOrganizations', 1, '{}', 60, TRUE,
                (now() AT TIME ZONE 'UTC'))
            RETURNING "Id",
                      "FencingToken",
                      abs(extract(epoch from ("CreatedAt" - (now() AT TIME ZONE 'UTC')))) AS CreatedAtSecondsFromUtcNow,
                      "LastEnqueuedOccurrence" IS NULL AND "LeaseOwner" IS NULL AND "LeaseExpiresAt" IS NULL AS LeaseAndOccurrenceEmpty;
            """
        );
    }

    [Test]
    public void It_declares_the_planned_columns_in_order()
    {
        _columns
            .Should()
            .Equal(
                new ColumnShape("Id", "bigint", false, null, true),
                new ColumnShape("TenantId", "bigint", true, null, false),
                new ColumnShape("ScheduleType", "character varying", false, 100, false),
                new ColumnShape("JobType", "character varying", false, 100, false),
                new ColumnShape("PayloadVersion", "smallint", false, null, false),
                new ColumnShape("Payload", "character varying", false, 4000, false),
                new ColumnShape("IntervalMinutes", "integer", false, null, false),
                new ColumnShape("Enabled", "boolean", false, null, false),
                new ColumnShape("NextRunAt", "timestamp without time zone", false, null, false),
                new ColumnShape("LastEnqueuedOccurrence", "timestamp without time zone", true, null, false),
                new ColumnShape("LeaseOwner", "character varying", true, 200, false),
                new ColumnShape("LeaseExpiresAt", "timestamp without time zone", true, null, false),
                new ColumnShape("FencingToken", "bigint", false, null, false),
                new ColumnShape("CreatedAt", "timestamp without time zone", false, null, false),
                new ColumnShape("CreatedBy", "character varying", true, 256, false),
                new ColumnShape("LastModifiedAt", "timestamp without time zone", true, null, false),
                new ColumnShape("ModifiedBy", "character varying", true, 256, false)
            );
    }

    [Test]
    public void It_declares_the_key_foreign_key_and_check_constraints()
    {
        _constraints
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    new ConstraintShape("CK_JobSchedule_IntervalMinutes", "c", " "),
                    new ConstraintShape("CK_JobSchedule_Payload_Object", "c", " "),
                    new ConstraintShape("FK_JobSchedule_Tenant", "f", "r"),
                    new ConstraintShape("PK_JobSchedule", "p", " "),
                }
            );
    }

    [Test]
    public void It_declares_the_unique_and_lookup_indexes()
    {
        _indexes
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    new IndexShape("IX_JobSchedule_Due", false, "Enabled,NextRunAt", null),
                    new IndexShape("IX_JobSchedule_TenantId", false, "TenantId", null),
                    new IndexShape("PK_JobSchedule", true, "Id", null),
                    new IndexShape(
                        "UX_JobSchedule_SingleTenant_Type",
                        true,
                        "ScheduleType",
                        "(\"TenantId\" IS NULL)"
                    ),
                    new IndexShape(
                        "UX_JobSchedule_Tenant_Type",
                        true,
                        "TenantId,ScheduleType",
                        "(\"TenantId\" IS NOT NULL)"
                    ),
                }
            );
    }

    [Test]
    public void It_generates_the_identity_and_the_audit_and_fencing_defaults()
    {
        _defaults.Id.Should().BePositive();
        _defaults.FencingToken.Should().Be(0);
        _defaults
            .CreatedAtSecondsFromUtcNow.Should()
            .BeLessThan(5, "CreatedAt defaults to database UTC time");
        _defaults.LeaseAndOccurrenceEmpty.Should().BeTrue();
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
        long FencingToken,
        decimal CreatedAtSecondsFromUtcNow,
        bool LeaseAndOccurrenceEmpty
    );
}

[TestFixture]
public class Given_schedules_that_share_a_schedule_type : JobScheduleSchemaTestBase
{
    private const string ScheduleType = "Probe.Refresh";

    private string? _firstTenantSchedule;
    private string? _disabledDuplicateForSameTenant;
    private string? _sameTypeForOtherTenant;
    private string? _firstSingleTenantSchedule;
    private string? _disabledDuplicateSingleTenantSchedule;
    private string? _typeDifferingOnlyByCase;

    [SetUp]
    public async Task Setup()
    {
        long tenantA = await CreateTenantAsync();
        long tenantB = await CreateTenantAsync();

        _firstTenantSchedule = await TryInsertScheduleAsync(tenantA, ScheduleType);
        _disabledDuplicateForSameTenant = await TryInsertScheduleAsync(tenantA, ScheduleType, enabled: false);
        _sameTypeForOtherTenant = await TryInsertScheduleAsync(tenantB, ScheduleType);
        _firstSingleTenantSchedule = await TryInsertScheduleAsync(null, ScheduleType);
        _disabledDuplicateSingleTenantSchedule = await TryInsertScheduleAsync(
            null,
            ScheduleType,
            enabled: false
        );
        _typeDifferingOnlyByCase = await TryInsertScheduleAsync(tenantA, ScheduleType.ToLowerInvariant());
    }

    [Test]
    public void It_rejects_a_second_schedule_for_the_same_tenant_and_type_even_when_disabled()
    {
        _firstTenantSchedule.Should().BeNull();
        _disabledDuplicateForSameTenant.Should().Be(UniqueViolation);
    }

    [Test]
    public void It_rejects_a_second_single_tenant_schedule_for_the_same_type_even_when_disabled()
    {
        _firstSingleTenantSchedule.Should().BeNull();
        _disabledDuplicateSingleTenantSchedule.Should().Be(UniqueViolation);
    }

    [Test]
    public void It_allows_the_same_type_for_another_tenant() => _sameTypeForOtherTenant.Should().BeNull();

    [Test]
    public void It_treats_schedule_types_that_differ_only_by_case_as_distinct() =>
        _typeDifferingOnlyByCase.Should().BeNull();
}

[TestFixture]
public class Given_schedule_payloads : JobScheduleSchemaTestBase
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
        int index = 0;
        foreach (string payload in _objectPayloads.Concat(_nonObjectPayloads).Concat(_invalidPayloads))
        {
            _outcomes[payload] = await TryInsertScheduleAsync(null, $"Probe.Payload{index++}", payload);
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
public class Given_schedule_intervals : JobScheduleSchemaTestBase
{
    private readonly Dictionary<int, string?> _outcomes = [];

    [SetUp]
    public async Task Setup()
    {
        foreach (int intervalMinutes in new[] { -1, 0, 1, 527_040, 527_041 })
        {
            _outcomes[intervalMinutes] = await TryInsertScheduleAsync(
                null,
                $"Probe.Interval{intervalMinutes}",
                intervalMinutes: intervalMinutes
            );
        }
    }

    [Test]
    public void It_accepts_one_minute_through_one_leap_year()
    {
        _outcomes[1].Should().BeNull();
        _outcomes[527_040].Should().BeNull();
    }

    [Test]
    public void It_rejects_intervals_outside_the_bounds()
    {
        _outcomes[-1].Should().Be(CheckViolation);
        _outcomes[0].Should().Be(CheckViolation);
        _outcomes[527_041].Should().Be(CheckViolation);
    }
}

[TestFixture]
public class Given_a_tenant_with_a_schedule : JobScheduleSchemaTestBase
{
    private string? _scheduleInsert;
    private string? _tenantDelete;

    [SetUp]
    public async Task Setup()
    {
        long tenantId = await CreateTenantAsync();
        _scheduleInsert = await TryInsertScheduleAsync(tenantId, "Probe.Refresh");

        try
        {
            await Connection!.ExecuteAsync(
                """DELETE FROM "dmscs"."Tenant" WHERE "Id" = @TenantId;""",
                new { TenantId = tenantId }
            );
        }
        catch (PostgresException exception)
        {
            _tenantDelete = exception.SqlState;
        }
    }

    [Test]
    public void It_restricts_deleting_the_tenant()
    {
        _scheduleInsert.Should().BeNull();
        _tenantDelete.Should().Be(ForeignKeyViolation);
    }
}
