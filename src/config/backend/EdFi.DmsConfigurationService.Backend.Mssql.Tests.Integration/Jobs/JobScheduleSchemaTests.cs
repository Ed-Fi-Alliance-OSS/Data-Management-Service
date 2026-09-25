// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;

/// <summary>
/// Shared setup for the DMS-1437 <c>dmscs.JobSchedule</c> schema fixtures (spec §3.2, D-10). Tenants
/// created here are removed in teardown, schedules first, since the foreign key blocks deleting a
/// referenced tenant.
/// </summary>
public abstract class JobScheduleSchemaTestBase : DatabaseTest
{
    protected const int CheckOrForeignKeyViolation = 547;
    protected const int UniqueIndexViolation = 2601;

    private readonly List<long> _tenantIds = [];

    [TearDown]
    public async Task DeleteCreatedTenants()
    {
        foreach (long tenantId in _tenantIds)
        {
            await Connection!.ExecuteAsync(
                """
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

    /// <summary>
    /// Inserts one schedule and returns the SQL Server error number it raised, or null when it was stored.
    /// </summary>
    protected async Task<int?> TryInsertScheduleAsync(
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
                INSERT INTO dmscs.JobSchedule (
                    TenantId, ScheduleType, JobType, PayloadVersion, Payload, IntervalMinutes, Enabled, NextRunAt)
                VALUES (@TenantId, @ScheduleType, N'DataStore.RefreshEducationOrganizations', 1, @Payload,
                    @IntervalMinutes, @Enabled, SYSUTCDATETIME());
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
        catch (SqlException exception)
        {
            return exception.Number;
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
                SELECT COLUMN_NAME AS Name,
                       DATA_TYPE AS DataType,
                       CAST(CASE WHEN IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS bit) AS IsNullable,
                       CHARACTER_MAXIMUM_LENGTH AS MaxLength,
                       CAST(COLUMNPROPERTY(OBJECT_ID(N'dmscs.JobSchedule'), COLUMN_NAME, 'IsIdentity') AS bit) AS IsIdentity
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'dmscs' AND TABLE_NAME = 'JobSchedule'
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
                WHERE object_info.parent_object_id = OBJECT_ID(N'dmscs.JobSchedule')
                  AND object_info.type IN ('PK', 'F', 'C')
                ORDER BY object_info.name;
                """
            )
        ).ToArray();

        _indexes = (
            await Connection!.QueryAsync<IndexShape>(
                """
                SELECT index_info.name AS Name,
                       index_info.is_unique AS IsUnique,
                       STRING_AGG(column_info.name, ',') WITHIN GROUP (ORDER BY index_column.key_ordinal) AS ColumnsCsv,
                       index_info.filter_definition AS FilterDefinition
                FROM sys.indexes index_info
                JOIN sys.index_columns index_column
                    ON index_column.object_id = index_info.object_id
                   AND index_column.index_id = index_info.index_id
                   AND index_column.is_included_column = 0
                JOIN sys.columns column_info
                    ON column_info.object_id = index_column.object_id
                   AND column_info.column_id = index_column.column_id
                WHERE index_info.object_id = OBJECT_ID(N'dmscs.JobSchedule')
                GROUP BY index_info.name, index_info.is_unique, index_info.filter_definition
                ORDER BY index_info.name;
                """
            )
        ).ToArray();

        _defaults = await Connection!.QuerySingleAsync<InsertedDefaults>(
            """
            INSERT INTO dmscs.JobSchedule (
                ScheduleType, JobType, PayloadVersion, Payload, IntervalMinutes, Enabled, NextRunAt)
            OUTPUT inserted.Id,
                   inserted.FencingToken,
                   ABS(DATEDIFF_BIG(millisecond, inserted.CreatedAt, SYSUTCDATETIME())) AS CreatedAtMillisecondsFromUtcNow,
                   CAST(CASE WHEN inserted.LastEnqueuedOccurrence IS NULL AND inserted.LeaseOwner IS NULL
                                  AND inserted.LeaseExpiresAt IS NULL THEN 1 ELSE 0 END AS bit) AS LeaseAndOccurrenceEmpty
            VALUES (N'Probe.Defaults', N'DataStore.RefreshEducationOrganizations', 1, N'{}', 60, 1, SYSUTCDATETIME());
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
                new ColumnShape("ScheduleType", "nvarchar", false, 100, false),
                new ColumnShape("JobType", "nvarchar", false, 100, false),
                new ColumnShape("PayloadVersion", "smallint", false, null, false),
                new ColumnShape("Payload", "nvarchar", false, 4000, false),
                new ColumnShape("IntervalMinutes", "int", false, null, false),
                new ColumnShape("Enabled", "bit", false, null, false),
                new ColumnShape("NextRunAt", "datetime2", false, null, false),
                new ColumnShape("LastEnqueuedOccurrence", "datetime2", true, null, false),
                new ColumnShape("LeaseOwner", "nvarchar", true, 200, false),
                new ColumnShape("LeaseExpiresAt", "datetime2", true, null, false),
                new ColumnShape("FencingToken", "bigint", false, null, false),
                new ColumnShape("CreatedAt", "datetime2", false, null, false),
                new ColumnShape("CreatedBy", "nvarchar", true, 256, false),
                new ColumnShape("LastModifiedAt", "datetime2", true, null, false),
                new ColumnShape("ModifiedBy", "nvarchar", true, 256, false)
            );
    }

    [Test]
    public async Task It_uses_a_binary_collation_for_the_schedule_and_job_type_keys()
    {
        string[] collations = (
            await Connection!.QueryAsync<string>(
                """
                SELECT COLLATION_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'dmscs' AND TABLE_NAME = 'JobSchedule'
                  AND COLUMN_NAME IN ('ScheduleType', 'JobType');
                """
            )
        ).ToArray();

        collations.Should().Equal("Latin1_General_BIN2", "Latin1_General_BIN2");
    }

    [Test]
    public void It_declares_trusted_key_foreign_key_and_check_constraints()
    {
        _constraints
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    new ConstraintShape("CK_JobSchedule_IntervalMinutes", "C", "", false),
                    new ConstraintShape("CK_JobSchedule_Payload_Object", "C", "", false),
                    new ConstraintShape("FK_JobSchedule_Tenant", "F", "NO_ACTION", false),
                    new ConstraintShape("PK_JobSchedule", "PK", "", false),
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
                        "([TenantId] IS NULL)"
                    ),
                    new IndexShape(
                        "UX_JobSchedule_Tenant_Type",
                        true,
                        "TenantId,ScheduleType",
                        "([TenantId] IS NOT NULL)"
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
            .CreatedAtMillisecondsFromUtcNow.Should()
            .BeLessThan(5_000, "CreatedAt defaults to database UTC time");
        _defaults.LeaseAndOccurrenceEmpty.Should().BeTrue();
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

    private sealed record IndexShape(string Name, bool IsUnique, string ColumnsCsv, string? FilterDefinition);

    private sealed record InsertedDefaults(
        long Id,
        long FencingToken,
        long CreatedAtMillisecondsFromUtcNow,
        bool LeaseAndOccurrenceEmpty
    );
}

[TestFixture]
public class Given_schedules_that_share_a_schedule_type : JobScheduleSchemaTestBase
{
    private const string ScheduleType = "Probe.Refresh";

    private int? _firstTenantSchedule;
    private int? _disabledDuplicateForSameTenant;
    private int? _sameTypeForOtherTenant;
    private int? _firstSingleTenantSchedule;
    private int? _disabledDuplicateSingleTenantSchedule;
    private int? _typeDifferingOnlyByCase;

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
        _disabledDuplicateForSameTenant.Should().Be(UniqueIndexViolation);
    }

    [Test]
    public void It_rejects_a_second_single_tenant_schedule_for_the_same_type_even_when_disabled()
    {
        _firstSingleTenantSchedule.Should().BeNull();
        _disabledDuplicateSingleTenantSchedule.Should().Be(UniqueIndexViolation);
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

    private readonly Dictionary<string, int?> _outcomes = [];

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
public class Given_schedule_intervals : JobScheduleSchemaTestBase
{
    private readonly Dictionary<int, int?> _outcomes = [];

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
        _outcomes[-1].Should().Be(CheckOrForeignKeyViolation);
        _outcomes[0].Should().Be(CheckOrForeignKeyViolation);
        _outcomes[527_041].Should().Be(CheckOrForeignKeyViolation);
    }
}

[TestFixture]
public class Given_a_tenant_with_a_schedule : JobScheduleSchemaTestBase
{
    private int? _scheduleInsert;
    private int? _tenantDelete;

    [SetUp]
    public async Task Setup()
    {
        long tenantId = await CreateTenantAsync();
        _scheduleInsert = await TryInsertScheduleAsync(tenantId, "Probe.Refresh");

        try
        {
            await Connection!.ExecuteAsync(
                "DELETE FROM dmscs.Tenant WHERE Id = @TenantId;",
                new { TenantId = tenantId }
            );
        }
        catch (SqlException exception)
        {
            _tenantDelete = exception.Number;
        }
    }

    [Test]
    public void It_restricts_deleting_the_tenant()
    {
        _scheduleInsert.Should().BeNull();
        _tenantDelete.Should().Be(CheckOrForeignKeyViolation);
    }
}
