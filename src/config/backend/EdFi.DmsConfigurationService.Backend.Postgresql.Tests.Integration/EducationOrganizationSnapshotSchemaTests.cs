// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Shared setup for the education organization snapshot schema fixtures: ordinary data stores to hang
/// targets on, and inserts that report the PostgreSQL error code they raised instead of throwing.
/// </summary>
public abstract class EducationOrganizationSnapshotSchemaTestBase : DatabaseTest
{
    protected const string CheckViolation = "23514";
    protected const string UniqueViolation = "23505";
    protected const string ForeignKeyViolation = "23503";

    protected const string ScriptSuffix = ".0042_Create_EducationOrganizationSnapshot_Tables.sql";

    protected async Task<int> InsertDataStoreAsync(string name) =>
        await Connection!.ExecuteScalarAsync<int>(
            """INSERT INTO dmscs."DataStore" ("Name", "DataStoreType", "Provider") VALUES (@Name, 'Shared', 'postgresql') RETURNING "Id";""",
            new { Name = name }
        );

    protected async Task<string?> TryExecuteAsync(string sql, object parameters)
    {
        try
        {
            await Connection!.ExecuteAsync(sql, parameters);
            return null;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
    }

    protected Task<string?> TryInsertTargetAsync(
        int dataStoreId,
        long lastGeneration,
        long? activeGeneration = null,
        string? leaseOwner = null,
        long? leaseGeneration = null,
        bool leaseExpires = false
    ) =>
        TryExecuteAsync(
            """
            INSERT INTO dmscs."EducationOrganizationSnapshotTarget"
                ("DataStoreId", "LastGeneration", "ActiveGeneration", "LeaseOwner", "LeaseGeneration", "LeaseExpiresAt")
            VALUES (@DataStoreId, @LastGeneration, @ActiveGeneration, @LeaseOwner, @LeaseGeneration,
                CASE WHEN @LeaseExpires THEN (now() AT TIME ZONE 'UTC') + interval '5 minutes' END);
            """,
            new
            {
                DataStoreId = dataStoreId,
                LastGeneration = lastGeneration,
                ActiveGeneration = activeGeneration,
                LeaseOwner = leaseOwner,
                LeaseGeneration = leaseGeneration,
                LeaseExpires = leaseExpires,
            }
        );

    protected Task<string?> TryInsertOrganizationAsync(
        int dataStoreId,
        long generation,
        long educationOrganizationId,
        long? parentId = null,
        string nameOfInstitution = "Grand Bend High School",
        string? shortNameOfInstitution = null
    ) =>
        TryExecuteAsync(
            """
            INSERT INTO dmscs."EducationOrganizationSnapshot"
                ("DataStoreId", "Generation", "EducationOrganizationId", "NameOfInstitution", "ShortNameOfInstitution",
                 "Discriminator", "ParentId", "RefreshedAt", "SourceContractVersion")
            VALUES (@DataStoreId, @Generation, @EducationOrganizationId, @NameOfInstitution, @ShortNameOfInstitution,
                'edfi.School', @ParentId, (now() AT TIME ZONE 'UTC'), 'educationOrganizationProjection.v1');
            """,
            new
            {
                DataStoreId = dataStoreId,
                Generation = generation,
                EducationOrganizationId = educationOrganizationId,
                NameOfInstitution = nameOfInstitution,
                ShortNameOfInstitution = shortNameOfInstitution,
                ParentId = parentId,
            }
        );
}

[TestFixture]
public class Given_the_education_organization_snapshot_tables : EducationOrganizationSnapshotSchemaTestBase
{
    private const string ShapeSql = """
        SELECT 'column' AS Kind,
               table_name || '.' || column_name || ':' || data_type
                   || ':' || COALESCE(character_maximum_length::text, '')
                   || ':' || CASE WHEN is_nullable = 'YES' THEN 'null' ELSE 'not null' END AS Shape
        FROM information_schema.columns
        WHERE table_schema = 'dmscs'
          AND table_name IN ('EducationOrganizationSnapshotTarget', 'EducationOrganizationSnapshot')
        UNION ALL
        SELECT 'constraint',
               table_info.relname || '.' || constraint_info.conname || ':' || constraint_info.contype::text
                   || ':' || constraint_info.confdeltype::text
        FROM pg_constraint constraint_info
        JOIN pg_class table_info ON table_info.oid = constraint_info.conrelid
        JOIN pg_namespace schema_info ON schema_info.oid = table_info.relnamespace
        WHERE schema_info.nspname = 'dmscs'
          AND table_info.relname IN ('EducationOrganizationSnapshotTarget', 'EducationOrganizationSnapshot')
          AND constraint_info.contype <> 'n'
        UNION ALL
        SELECT 'index',
               table_info.relname || '.' || index_info.relname || ':' || index_catalog.indisunique::text || ':'
                   || (
                       SELECT string_agg(attribute_info.attname, ',' ORDER BY key_columns.ordinality)
                       FROM unnest(index_catalog.indkey) WITH ORDINALITY AS key_columns(attnum, ordinality)
                       JOIN pg_attribute attribute_info
                           ON attribute_info.attrelid = index_catalog.indrelid
                          AND attribute_info.attnum = key_columns.attnum
                   )
        FROM pg_index index_catalog
        JOIN pg_class index_info ON index_info.oid = index_catalog.indexrelid
        JOIN pg_class table_info ON table_info.oid = index_catalog.indrelid
        JOIN pg_namespace schema_info ON schema_info.oid = table_info.relnamespace
        WHERE schema_info.nspname = 'dmscs'
          AND table_info.relname IN ('EducationOrganizationSnapshotTarget', 'EducationOrganizationSnapshot')
        ORDER BY 1, 2;
        """;

    private string[] _columns = [];
    private string[] _constraints = [];
    private string[] _indexes = [];
    private string[] _replayedShape = [];

    [SetUp]
    public async Task Setup()
    {
        (string Kind, string Shape)[] shape = (
            await Connection!.QueryAsync<(string Kind, string Shape)>(ShapeSql)
        ).ToArray();
        _columns = shape.Where(item => item.Kind == "column").Select(item => item.Shape).ToArray();
        _constraints = shape.Where(item => item.Kind == "constraint").Select(item => item.Shape).ToArray();
        _indexes = shape.Where(item => item.Kind == "index").Select(item => item.Shape).ToArray();

        string resource = typeof(Deploy.DatabaseDeploy)
            .Assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(ScriptSuffix, StringComparison.Ordinal));
        await using Stream stream = typeof(Deploy.DatabaseDeploy).Assembly.GetManifestResourceStream(
            resource
        )!;
        using StreamReader reader = new(stream);
        await Connection!.ExecuteAsync(await reader.ReadToEndAsync());
        _replayedShape = (await Connection!.QueryAsync<(string Kind, string Shape)>(ShapeSql))
            .Select(item => item.Shape)
            .ToArray();
    }

    [Test]
    public void It_declares_the_planned_columns_with_portable_types()
    {
        _columns
            .Should()
            .BeEquivalentTo(
                "EducationOrganizationSnapshotTarget.DataStoreId:integer::not null",
                "EducationOrganizationSnapshotTarget.TenantId:bigint::null",
                "EducationOrganizationSnapshotTarget.LastGeneration:bigint::not null",
                "EducationOrganizationSnapshotTarget.ActiveGeneration:bigint::null",
                "EducationOrganizationSnapshotTarget.RefreshedAt:timestamp without time zone::null",
                "EducationOrganizationSnapshotTarget.SourceContractVersion:character varying:100:null",
                "EducationOrganizationSnapshotTarget.LeaseOwner:character varying:32:null",
                "EducationOrganizationSnapshotTarget.LeaseGeneration:bigint::null",
                "EducationOrganizationSnapshotTarget.LeaseExpiresAt:timestamp without time zone::null",
                "EducationOrganizationSnapshotTarget.CreatedAt:timestamp without time zone::not null",
                "EducationOrganizationSnapshotTarget.LastModifiedAt:timestamp without time zone::null",
                "EducationOrganizationSnapshot.DataStoreId:integer::not null",
                "EducationOrganizationSnapshot.Generation:bigint::not null",
                "EducationOrganizationSnapshot.TenantId:bigint::null",
                "EducationOrganizationSnapshot.EducationOrganizationId:bigint::not null",
                "EducationOrganizationSnapshot.NameOfInstitution:character varying:256:not null",
                "EducationOrganizationSnapshot.ShortNameOfInstitution:character varying:256:null",
                "EducationOrganizationSnapshot.Discriminator:character varying:128:not null",
                "EducationOrganizationSnapshot.ParentId:bigint::null",
                "EducationOrganizationSnapshot.RefreshedAt:timestamp without time zone::not null",
                "EducationOrganizationSnapshot.SourceContractVersion:character varying:100:not null"
            );
    }

    [Test]
    public void It_declares_the_keys_cascades_and_checks()
    {
        _constraints
            .Should()
            .BeEquivalentTo(
                "EducationOrganizationSnapshotTarget.PK_EducationOrganizationSnapshotTarget:p: ",
                "EducationOrganizationSnapshotTarget.FK_EducationOrganizationSnapshotTarget_DataStore:f:c",
                "EducationOrganizationSnapshotTarget.FK_EducationOrganizationSnapshotTarget_Tenant:f:a",
                "EducationOrganizationSnapshotTarget.CK_EducationOrganizationSnapshotTarget_Generations:c: ",
                "EducationOrganizationSnapshotTarget.CK_EducationOrganizationSnapshotTarget_Lease:c: ",
                "EducationOrganizationSnapshot.PK_EducationOrganizationSnapshot:p: ",
                "EducationOrganizationSnapshot.FK_EducationOrganizationSnapshot_Target:f:c",
                "EducationOrganizationSnapshot.CK_EducationOrganizationSnapshot_Generation:c: "
            );
    }

    [Test]
    public void It_declares_the_key_and_tenant_indexes()
    {
        _indexes
            .Should()
            .BeEquivalentTo(
                "EducationOrganizationSnapshotTarget.PK_EducationOrganizationSnapshotTarget:true:DataStoreId",
                "EducationOrganizationSnapshotTarget.IX_EducationOrganizationSnapshotTarget_TenantId:false:TenantId",
                "EducationOrganizationSnapshot.PK_EducationOrganizationSnapshot:true:DataStoreId,Generation,EducationOrganizationId"
            );
    }

    [Test]
    public void It_leaves_the_shape_unchanged_when_the_script_runs_again() =>
        _replayedShape.Should().Equal(_columns.Concat(_constraints).Concat(_indexes));
}

[TestFixture]
public class Given_snapshot_target_rows : EducationOrganizationSnapshotSchemaTestBase
{
    private readonly Dictionary<string, string?> _outcomes = [];

    [SetUp]
    public async Task Setup()
    {
        string lease = Guid.NewGuid().ToString("N");
        int duplicate = await InsertDataStoreAsync("duplicate");

        _outcomes["unpublished"] = await TryInsertTargetAsync(await InsertDataStoreAsync("unpublished"), 0);
        _outcomes["published with a live lease"] = await TryInsertTargetAsync(
            await InsertDataStoreAsync("leased"),
            2,
            1,
            lease,
            2,
            true
        );
        _outcomes["active above last"] = await TryInsertTargetAsync(
            await InsertDataStoreAsync("ahead"),
            1,
            2
        );
        _outcomes["active zero"] = await TryInsertTargetAsync(await InsertDataStoreAsync("zero"), 1, 0);
        _outcomes["negative last"] = await TryInsertTargetAsync(await InsertDataStoreAsync("negative"), -1);
        _outcomes["owner without lease"] = await TryInsertTargetAsync(
            await InsertDataStoreAsync("partial"),
            1,
            leaseOwner: lease
        );
        _outcomes["lease without generation"] = await TryInsertTargetAsync(
            await InsertDataStoreAsync("no-generation"),
            1,
            leaseOwner: lease,
            leaseExpires: true
        );
        _outcomes["lease above last"] = await TryInsertTargetAsync(
            await InsertDataStoreAsync("lease-ahead"),
            1,
            leaseOwner: lease,
            leaseGeneration: 2,
            leaseExpires: true
        );
        _outcomes["missing data store"] = await TryInsertTargetAsync(int.MaxValue, 0);
        _outcomes["first for a data store"] = await TryInsertTargetAsync(duplicate, 0);
        _outcomes["second for a data store"] = await TryInsertTargetAsync(duplicate, 0);
    }

    [Test]
    public void It_accepts_unpublished_and_leased_targets()
    {
        _outcomes["unpublished"].Should().BeNull();
        _outcomes["published with a live lease"].Should().BeNull();
        _outcomes["first for a data store"].Should().BeNull();
    }

    [Test]
    public void It_rejects_generations_outside_the_allocated_range()
    {
        _outcomes["active above last"].Should().Be(CheckViolation);
        _outcomes["active zero"].Should().Be(CheckViolation);
        _outcomes["negative last"].Should().Be(CheckViolation);
        _outcomes["lease above last"].Should().Be(CheckViolation);
    }

    [Test]
    public void It_rejects_a_partial_lease()
    {
        _outcomes["owner without lease"].Should().Be(CheckViolation);
        _outcomes["lease without generation"].Should().Be(CheckViolation);
    }

    [Test]
    public void It_requires_an_existing_data_store() =>
        _outcomes["missing data store"].Should().Be(ForeignKeyViolation);

    [Test]
    public void It_keeps_one_target_per_data_store() =>
        _outcomes["second for a data store"].Should().Be(UniqueViolation);
}

[TestFixture]
public class Given_snapshot_organization_rows : EducationOrganizationSnapshotSchemaTestBase
{
    private const long BeyondDoublePrecision = 9_007_199_254_740_993;
    private static readonly string _longestReaderName = string.Concat(Enumerable.Repeat("\U0001F3EB", 75));

    private readonly Dictionary<string, string?> _outcomes = [];
    private (long EducationOrganizationId, long? ParentId, string? ShortName) _wideRow;
    private string _storedLongestName = "";

    [SetUp]
    public async Task Setup()
    {
        int dataStoreId = await InsertDataStoreAsync("organizations");
        (await TryInsertTargetAsync(dataStoreId, 2, 1)).Should().BeNull();

        _outcomes["first"] = await TryInsertOrganizationAsync(dataStoreId, 1, 255901001, 255901);
        _outcomes["same organization in the same generation"] = await TryInsertOrganizationAsync(
            dataStoreId,
            1,
            255901001,
            255901
        );
        _outcomes["same organization in another generation"] = await TryInsertOrganizationAsync(
            dataStoreId,
            2,
            255901001,
            255901
        );
        _outcomes["generation zero"] = await TryInsertOrganizationAsync(dataStoreId, 0, 255901002);
        _outcomes["missing target"] = await TryInsertOrganizationAsync(
            await InsertDataStoreAsync("no-target"),
            1,
            255901003
        );
        _outcomes["wide identifiers"] = await TryInsertOrganizationAsync(
            dataStoreId,
            1,
            BeyondDoublePrecision,
            long.MaxValue - 1,
            shortNameOfInstitution: "Wide"
        );
        _outcomes["no parent or short name"] = await TryInsertOrganizationAsync(dataStoreId, 1, 255901);
        _outcomes["longest reader name"] = await TryInsertOrganizationAsync(
            dataStoreId,
            1,
            255901004,
            nameOfInstitution: _longestReaderName
        );

        _wideRow = await Connection!.QuerySingleAsync<(long, long?, string?)>(
            """
            SELECT "EducationOrganizationId", "ParentId", "ShortNameOfInstitution"
            FROM dmscs."EducationOrganizationSnapshot"
            WHERE "DataStoreId" = @DataStoreId AND "Generation" = 1 AND "EducationOrganizationId" = @Id;
            """,
            new { DataStoreId = dataStoreId, Id = BeyondDoublePrecision }
        );
        _storedLongestName =
            await Connection!.ExecuteScalarAsync<string>(
                """SELECT "NameOfInstitution" FROM dmscs."EducationOrganizationSnapshot" WHERE "EducationOrganizationId" = 255901004;"""
            ) ?? "";
    }

    [Test]
    public void It_allows_an_organization_once_per_generation()
    {
        _outcomes["first"].Should().BeNull();
        _outcomes["same organization in the same generation"].Should().Be(UniqueViolation);
        _outcomes["same organization in another generation"].Should().BeNull();
    }

    [Test]
    public void It_rejects_a_generation_below_one() =>
        _outcomes["generation zero"].Should().Be(CheckViolation);

    [Test]
    public void It_requires_a_target_row() => _outcomes["missing target"].Should().Be(ForeignKeyViolation);

    [Test]
    public void It_keeps_int64_identifiers_exact()
    {
        _outcomes["wide identifiers"].Should().BeNull();
        _wideRow.Should().Be((BeyondDoublePrecision, long.MaxValue - 1, "Wide"));
    }

    [Test]
    public void It_accepts_a_missing_parent_and_short_name() =>
        _outcomes["no parent or short name"].Should().BeNull();

    [Test]
    public void It_stores_the_longest_name_the_reader_accepts()
    {
        _outcomes["longest reader name"].Should().BeNull();
        _storedLongestName.Should().Be(_longestReaderName);
    }
}
