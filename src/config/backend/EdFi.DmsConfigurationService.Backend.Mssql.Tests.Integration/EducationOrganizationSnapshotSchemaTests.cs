// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Shared setup for the education organization snapshot schema fixtures: ordinary data stores to hang
/// targets on, and inserts that report the SQL Server error number they raised instead of throwing.
/// </summary>
public abstract class EducationOrganizationSnapshotSchemaTestBase : DatabaseTest
{
    protected const int CheckOrForeignKeyViolation = 547;
    protected const int PrimaryKeyViolation = 2627;

    protected const string ScriptSuffix = ".0042_Create_EducationOrganizationSnapshot_Tables.sql";

    protected async Task<int> InsertDataStoreAsync(string name) =>
        await Connection!.ExecuteScalarAsync<int>(
            "INSERT INTO dmscs.DataStore (Name, DataStoreType, Provider) OUTPUT inserted.Id VALUES (@Name, N'Shared', N'sqlserver');",
            new { Name = name }
        );

    protected async Task<int?> TryExecuteAsync(string sql, object parameters)
    {
        try
        {
            await Connection!.ExecuteAsync(sql, parameters);
            return null;
        }
        catch (SqlException exception)
        {
            return exception.Number;
        }
    }

    protected Task<int?> TryInsertTargetAsync(
        int dataStoreId,
        long lastGeneration,
        long? activeGeneration = null,
        string? leaseOwner = null,
        long? leaseGeneration = null,
        bool leaseExpires = false
    ) =>
        TryExecuteAsync(
            """
            INSERT INTO dmscs.EducationOrganizationSnapshotTarget
                (DataStoreId, LastGeneration, ActiveGeneration, LeaseOwner, LeaseGeneration, LeaseExpiresAt)
            VALUES (@DataStoreId, @LastGeneration, @ActiveGeneration, @LeaseOwner, @LeaseGeneration,
                CASE WHEN @LeaseExpires = 1 THEN DATEADD(minute, 5, SYSUTCDATETIME()) END);
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

    protected Task<int?> TryInsertOrganizationAsync(
        int dataStoreId,
        long generation,
        long educationOrganizationId,
        long? parentId = null,
        string nameOfInstitution = "Grand Bend High School",
        string? shortNameOfInstitution = null
    ) =>
        TryExecuteAsync(
            """
            INSERT INTO dmscs.EducationOrganizationSnapshot
                (DataStoreId, Generation, EducationOrganizationId, NameOfInstitution, ShortNameOfInstitution,
                 Discriminator, ParentId, RefreshedAt, SourceContractVersion)
            VALUES (@DataStoreId, @Generation, @EducationOrganizationId, @NameOfInstitution, @ShortNameOfInstitution,
                N'edfi.School', @ParentId, SYSUTCDATETIME(), N'educationOrganizationProjection.v1');
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
        SELECT N'column' COLLATE DATABASE_DEFAULT AS Kind,
               (TABLE_NAME + N'.' + COLUMN_NAME + N':' + DATA_TYPE
                   + N':' + COALESCE(CAST(CHARACTER_MAXIMUM_LENGTH AS NVARCHAR(10)), N'')
                   + N':' + CASE WHEN IS_NULLABLE = 'YES' THEN N'null' ELSE N'not null' END)
                   COLLATE DATABASE_DEFAULT AS Shape
        FROM INFORMATION_SCHEMA.COLUMNS
        WHERE TABLE_SCHEMA = 'dmscs'
          AND TABLE_NAME IN ('EducationOrganizationSnapshotTarget', 'EducationOrganizationSnapshot')
        UNION ALL
        SELECT N'constraint' COLLATE DATABASE_DEFAULT,
               (OBJECT_NAME(object_info.parent_object_id) + N'.' + object_info.name + N':' + RTRIM(object_info.type)
                   + N':' + COALESCE(foreign_key.delete_referential_action_desc, N'')
                   + N':' + CASE WHEN COALESCE(check_info.is_not_trusted, foreign_key.is_not_trusted, 0) = 1
                                 THEN N'untrusted' ELSE N'trusted' END)
                   COLLATE DATABASE_DEFAULT
        FROM sys.objects object_info
        LEFT JOIN sys.foreign_keys foreign_key ON foreign_key.object_id = object_info.object_id
        LEFT JOIN sys.check_constraints check_info ON check_info.object_id = object_info.object_id
        WHERE object_info.parent_object_id IN (
                OBJECT_ID(N'dmscs.EducationOrganizationSnapshotTarget'),
                OBJECT_ID(N'dmscs.EducationOrganizationSnapshot'))
          AND object_info.type IN ('PK', 'F', 'C')
        UNION ALL
        SELECT N'index' COLLATE DATABASE_DEFAULT,
               (OBJECT_NAME(index_info.object_id) + N'.' + index_info.name + N':'
                   + CASE WHEN index_info.is_unique = 1 THEN N'true' ELSE N'false' END + N':'
                   + STRING_AGG(column_info.name, N',') WITHIN GROUP (ORDER BY index_column.key_ordinal))
                   COLLATE DATABASE_DEFAULT
        FROM sys.indexes index_info
        JOIN sys.index_columns index_column
            ON index_column.object_id = index_info.object_id
           AND index_column.index_id = index_info.index_id
           AND index_column.is_included_column = 0
        JOIN sys.columns column_info
            ON column_info.object_id = index_column.object_id
           AND column_info.column_id = index_column.column_id
        WHERE index_info.object_id IN (
                OBJECT_ID(N'dmscs.EducationOrganizationSnapshotTarget'),
                OBJECT_ID(N'dmscs.EducationOrganizationSnapshot'))
        GROUP BY index_info.object_id, index_info.name, index_info.is_unique
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
                "EducationOrganizationSnapshotTarget.DataStoreId:int::not null",
                "EducationOrganizationSnapshotTarget.TenantId:bigint::null",
                "EducationOrganizationSnapshotTarget.LastGeneration:bigint::not null",
                "EducationOrganizationSnapshotTarget.ActiveGeneration:bigint::null",
                "EducationOrganizationSnapshotTarget.RefreshedAt:datetime2::null",
                "EducationOrganizationSnapshotTarget.SourceContractVersion:nvarchar:100:null",
                "EducationOrganizationSnapshotTarget.LeaseOwner:nvarchar:32:null",
                "EducationOrganizationSnapshotTarget.LeaseGeneration:bigint::null",
                "EducationOrganizationSnapshotTarget.LeaseExpiresAt:datetime2::null",
                "EducationOrganizationSnapshotTarget.CreatedAt:datetime2::not null",
                "EducationOrganizationSnapshotTarget.LastModifiedAt:datetime2::null",
                "EducationOrganizationSnapshot.DataStoreId:int::not null",
                "EducationOrganizationSnapshot.Generation:bigint::not null",
                "EducationOrganizationSnapshot.TenantId:bigint::null",
                "EducationOrganizationSnapshot.EducationOrganizationId:bigint::not null",
                "EducationOrganizationSnapshot.NameOfInstitution:nvarchar:256:not null",
                "EducationOrganizationSnapshot.ShortNameOfInstitution:nvarchar:256:null",
                "EducationOrganizationSnapshot.Discriminator:nvarchar:128:not null",
                "EducationOrganizationSnapshot.ParentId:bigint::null",
                "EducationOrganizationSnapshot.RefreshedAt:datetime2::not null",
                "EducationOrganizationSnapshot.SourceContractVersion:nvarchar:100:not null"
            );
    }

    [Test]
    public async Task It_uses_a_binary_collation_for_the_lease_owner()
    {
        string collation =
            await Connection!.ExecuteScalarAsync<string>(
                """
                SELECT COLLATION_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'dmscs' AND TABLE_NAME = 'EducationOrganizationSnapshotTarget'
                  AND COLUMN_NAME = 'LeaseOwner';
                """
            ) ?? "";

        collation.Should().Be("Latin1_General_BIN2");
    }

    [Test]
    public void It_declares_trusted_keys_cascades_and_checks()
    {
        _constraints
            .Should()
            .BeEquivalentTo(
                "EducationOrganizationSnapshotTarget.PK_EducationOrganizationSnapshotTarget:PK::trusted",
                "EducationOrganizationSnapshotTarget.FK_EducationOrganizationSnapshotTarget_DataStore:F:CASCADE:trusted",
                "EducationOrganizationSnapshotTarget.FK_EducationOrganizationSnapshotTarget_Tenant:F:NO_ACTION:trusted",
                "EducationOrganizationSnapshotTarget.CK_EducationOrganizationSnapshotTarget_Generations:C::trusted",
                "EducationOrganizationSnapshotTarget.CK_EducationOrganizationSnapshotTarget_Lease:C::trusted",
                "EducationOrganizationSnapshot.PK_EducationOrganizationSnapshot:PK::trusted",
                "EducationOrganizationSnapshot.FK_EducationOrganizationSnapshot_Target:F:CASCADE:trusted",
                "EducationOrganizationSnapshot.CK_EducationOrganizationSnapshot_Generation:C::trusted"
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
    private readonly Dictionary<string, int?> _outcomes = [];

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
        _outcomes["active above last"].Should().Be(CheckOrForeignKeyViolation);
        _outcomes["active zero"].Should().Be(CheckOrForeignKeyViolation);
        _outcomes["negative last"].Should().Be(CheckOrForeignKeyViolation);
        _outcomes["lease above last"].Should().Be(CheckOrForeignKeyViolation);
    }

    [Test]
    public void It_rejects_a_partial_lease()
    {
        _outcomes["owner without lease"].Should().Be(CheckOrForeignKeyViolation);
        _outcomes["lease without generation"].Should().Be(CheckOrForeignKeyViolation);
    }

    [Test]
    public void It_requires_an_existing_data_store() =>
        _outcomes["missing data store"].Should().Be(CheckOrForeignKeyViolation);

    [Test]
    public void It_keeps_one_target_per_data_store() =>
        _outcomes["second for a data store"].Should().Be(PrimaryKeyViolation);
}

[TestFixture]
public class Given_snapshot_organization_rows : EducationOrganizationSnapshotSchemaTestBase
{
    private const long BeyondDoublePrecision = 9_007_199_254_740_993;
    private static readonly string _longestReaderName = string.Concat(Enumerable.Repeat("\U0001F3EB", 75));

    private readonly Dictionary<string, int?> _outcomes = [];
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
            SELECT EducationOrganizationId, ParentId, ShortNameOfInstitution
            FROM dmscs.EducationOrganizationSnapshot
            WHERE DataStoreId = @DataStoreId AND Generation = 1 AND EducationOrganizationId = @Id;
            """,
            new { DataStoreId = dataStoreId, Id = BeyondDoublePrecision }
        );
        _storedLongestName =
            await Connection!.ExecuteScalarAsync<string>(
                "SELECT NameOfInstitution FROM dmscs.EducationOrganizationSnapshot WHERE EducationOrganizationId = 255901004;"
            ) ?? "";
    }

    [Test]
    public void It_allows_an_organization_once_per_generation()
    {
        _outcomes["first"].Should().BeNull();
        _outcomes["same organization in the same generation"].Should().Be(PrimaryKeyViolation);
        _outcomes["same organization in another generation"].Should().BeNull();
    }

    [Test]
    public void It_rejects_a_generation_below_one() =>
        _outcomes["generation zero"].Should().Be(CheckOrForeignKeyViolation);

    [Test]
    public void It_requires_a_target_row() =>
        _outcomes["missing target"].Should().Be(CheckOrForeignKeyViolation);

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
