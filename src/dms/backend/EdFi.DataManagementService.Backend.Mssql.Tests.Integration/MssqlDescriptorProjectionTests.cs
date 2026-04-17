// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Integration tests for descriptor projection SQL execution against a live SQL Server database.
/// All test fixtures use the schema <c>"descprojtest"</c> with a minimal
/// <c>"StudentSchoolAssociation"</c> table that has one nullable descriptor FK column.
/// </summary>
/// <remarks>
/// DocumentIds 700–703 and DescriptorIds 901–903 are reserved for these fixtures.
/// Each fixture provisions its own isolated MSSQL database.
/// </remarks>
internal static class MssqlDescriptorProjectionFixture
{
    internal const string TestSchema = "descprojtest";
    internal const long DocumentId700 = 700L;
    internal const long DocumentId701 = 701L;
    internal const long DocumentId702 = 702L;
    internal const long DocumentId703 = 703L;
    internal const long DescriptorId901 = 901L;
    internal const long DescriptorId902 = 902L;
    internal const long DescriptorId903 = 903L;
    internal const string Uri901 = "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade";
    internal const string Uri902 = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";
    internal const string Uri903 = "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade";

    internal static readonly DbSchemaName Schema = new(TestSchema);
    internal static readonly DbTableName TableName = new(Schema, "StudentSchoolAssociation");

    internal static readonly JsonPathExpression GradeLevelDescriptorPath = new(
        "$.gradeLevelDescriptor",
        [new JsonPathSegment.Property("gradeLevelDescriptor")]
    );

    internal static readonly QualifiedResourceName GradeLevelDescriptorResource = new(
        "Ed-Fi",
        "GradeLevelDescriptor"
    );

    internal static readonly DbColumnName FkColumnName = new("GradeLevelDescriptor_DescriptorId");

    internal static RelationalResourceModel BuildResourceModel()
    {
        var tableModel = BuildTableModel();
        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation"),
            PhysicalSchema: Schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: tableModel,
            TablesInDependencyOrder: [tableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources:
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: GradeLevelDescriptorPath,
                    Table: TableName,
                    FkColumn: FkColumnName,
                    DescriptorResource: GradeLevelDescriptorResource
                ),
            ]
        );
    }

    internal static DbTableModel BuildTableModel() =>
        new(
            Table: TableName,
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_descprojtest_StudentSchoolAssociation",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: FkColumnName,
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: GradeLevelDescriptorPath,
                    TargetResource: GradeLevelDescriptorResource
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

    /// <summary>
    /// Provisions the dms schema, Document and Descriptor tables, and the test resource table.
    /// Each invocation is idempotent within a freshly-created database.
    /// </summary>
    internal static async Task ProvisionSchemaAsync(SqlConnection connection)
    {
        await ExecuteSqlAsync(
            connection,
            $"""
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'dms') EXEC('CREATE SCHEMA [dms]');
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{TestSchema}') EXEC('CREATE SCHEMA [{TestSchema}]');

            CREATE TABLE [dms].[Document] (
                [DocumentId] bigint PRIMARY KEY,
                [DocumentUuid] uniqueidentifier NOT NULL,
                [ResourceKeyId] smallint NOT NULL DEFAULT 0,
                [ContentVersion] bigint NOT NULL DEFAULT 1,
                [IdentityVersion] bigint NOT NULL DEFAULT 1,
                [ContentLastModifiedAt] datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                [IdentityLastModifiedAt] datetimeoffset NOT NULL DEFAULT sysdatetimeoffset(),
                [CreatedAt] datetimeoffset NOT NULL DEFAULT sysdatetimeoffset()
            );

            CREATE TABLE [dms].[Descriptor] (
                [DocumentId] bigint PRIMARY KEY,
                [Namespace] varchar(255) NOT NULL DEFAULT '',
                [CodeValue] varchar(50) NOT NULL DEFAULT '',
                [ShortDescription] varchar(75) NOT NULL DEFAULT '',
                [Description] varchar(1024) NULL,
                [EffectiveBeginDate] date NULL,
                [EffectiveEndDate] date NULL,
                [Discriminator] varchar(128) NOT NULL DEFAULT '',
                [Uri] varchar(306) NOT NULL
            );

            CREATE TABLE [{TestSchema}].[StudentSchoolAssociation] (
                [DocumentId] bigint PRIMARY KEY,
                [GradeLevelDescriptor_DescriptorId] bigint NULL
            );
            """
        );
    }

    internal static async Task InsertTestDataAsync(SqlConnection connection)
    {
        await ExecuteSqlAsync(
            connection,
            $"""
            INSERT INTO [dms].[Document] ([DocumentId], [DocumentUuid], [ResourceKeyId], [ContentVersion], [IdentityVersion]) VALUES
                (700, '70000000-0000-0000-0000-000000000700', 0, 1, 1),
                (701, '70100000-0000-0000-0000-000000000701', 0, 1, 1),
                (702, '70200000-0000-0000-0000-000000000702', 0, 1, 1),
                (703, '70300000-0000-0000-0000-000000000703', 0, 1, 1);

            INSERT INTO [dms].[Descriptor] ([DocumentId], [Namespace], [CodeValue], [ShortDescription], [Discriminator], [Uri]) VALUES
                (901, 'uri://ed-fi.org/GradeLevelDescriptor', 'Ninth grade', 'Ninth grade', 'edfi.GradeLevelDescriptor', '{Uri901}'),
                (902, 'uri://ed-fi.org/GradeLevelDescriptor', 'Tenth grade', 'Tenth grade', 'edfi.GradeLevelDescriptor', '{Uri902}'),
                (903, 'uri://ed-fi.org/GradeLevelDescriptor', 'Eleventh grade', 'Eleventh grade', 'edfi.GradeLevelDescriptor', '{Uri903}');
            """
        );
    }

    /// <summary>
    /// Creates the <c>#page</c> keyset temp table and inserts the supplied document IDs.
    /// The temp table persists for the session so subsequent commands can JOIN it.
    /// </summary>
    internal static async Task CreatePageTableAsync(SqlConnection connection, params long[] documentIds)
    {
        var valuesClause = string.Join(", ", documentIds.Select(id => $"({id})"));

        await ExecuteSqlAsync(
            connection,
            $"""
            IF OBJECT_ID('tempdb..[#page]') IS NOT NULL
                DROP TABLE [#page];
            CREATE TABLE [#page] ([DocumentId] bigint PRIMARY KEY);
            INSERT INTO [#page] ([DocumentId]) VALUES {valuesClause};
            """
        );
    }

    private static async Task ExecuteSqlAsync(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Verifies that hydrated descriptor rows return the correct URI string for a non-null
/// required descriptor FK column against SQL Server.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_Required_Descriptor_FK_Resolves_To_URI_Mssql
{
    private string _databaseName = null!;
    private string _connectionString = null!;
    private IReadOnlyDictionary<long, string> _lookup = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a MssqlAdmin connection string.");
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        await using var setupConn = new SqlConnection(_connectionString);
        await setupConn.OpenAsync();

        await MssqlDescriptorProjectionFixture.ProvisionSchemaAsync(setupConn);
        await MssqlDescriptorProjectionFixture.InsertTestDataAsync(setupConn);

        await using var insertCmd = new SqlCommand(
            $"""
            INSERT INTO [{MssqlDescriptorProjectionFixture.TestSchema}].[StudentSchoolAssociation]
                ([DocumentId], [GradeLevelDescriptor_DescriptorId])
            VALUES
                ({MssqlDescriptorProjectionFixture.DocumentId700}, {MssqlDescriptorProjectionFixture.DescriptorId901});
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        var resourceModel = MssqlDescriptorProjectionFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(resourceModel);

        await using var execConn = new SqlConnection(_connectionString);
        await execConn.OpenAsync();

        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            new PageKeysetSpec.Single(MssqlDescriptorProjectionFixture.DocumentId700),
            SqlDialect.Mssql,
            CancellationToken.None
        );
        _lookup = MssqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null && MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_returns_a_non_empty_lookup()
    {
        _lookup.Should().NotBeEmpty();
    }

    [Test]
    public void It_resolves_the_descriptor_id_to_the_correct_uri()
    {
        _lookup[MssqlDescriptorProjectionFixture.DescriptorId901]
            .Should()
            .Be(MssqlDescriptorProjectionFixture.Uri901);
    }

    [Test]
    public void It_stores_the_uri_exactly_as_in_dms_Descriptor()
    {
        _lookup[MssqlDescriptorProjectionFixture.DescriptorId901]
            .Should()
            .Be("uri://ed-fi.org/GradeLevelDescriptor#Ninth grade");
    }

    [Test]
    public void It_uses_mssql_dialect_quoting_in_the_compiled_plan_sql()
    {
        var resourceModel = MssqlDescriptorProjectionFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(resourceModel);

        readPlan.DescriptorProjectionPlansInOrder.Should().HaveCount(1);
        var sql = readPlan.DescriptorProjectionPlansInOrder[0].SelectByKeysetSql;

        // Validate MSSQL-dialect bracketed identifier quoting
        sql.Should().Contain("[#page]");
        sql.Should().Contain($"[{MssqlDescriptorProjectionFixture.TestSchema}]");
        sql.Should().Contain("[dms]");

        // Must NOT contain PostgreSQL double-quote syntax
        sql.Should().NotContain("\"#page\"");
        sql.Should().NotContain("\"page\"");
    }
}

/// <summary>
/// Verifies that a null descriptor FK causes the descriptor property to be absent from the
/// reconstituted JSON document when executing the full hydrate + project + reconstitute pipeline
/// against SQL Server.
/// Mirrors Task 3 (PostgreSQL null FK omission) for the MSSQL dialect.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_Null_Descriptor_FK_Omits_Property_From_Reconstituted_Document_Mssql
{
    private string _databaseName = null!;
    private string _connectionString = null!;
    private JsonNode _reconstitutedDocument = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a MssqlAdmin connection string.");
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        await using var setupConn = new SqlConnection(_connectionString);
        await setupConn.OpenAsync();

        await MssqlDescriptorProjectionFixture.ProvisionSchemaAsync(setupConn);
        await MssqlDescriptorProjectionFixture.InsertTestDataAsync(setupConn);

        // Document 701 has NULL descriptor FK
        await using var insertCmd = new SqlCommand(
            $"""
            INSERT INTO [{MssqlDescriptorProjectionFixture.TestSchema}].[StudentSchoolAssociation]
                ([DocumentId], [GradeLevelDescriptor_DescriptorId])
            VALUES
                ({MssqlDescriptorProjectionFixture.DocumentId701}, NULL);
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        var resourceModel = MssqlDescriptorProjectionFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(resourceModel);
        var keyset = new PageKeysetSpec.Single(MssqlDescriptorProjectionFixture.DocumentId701);

        await using var execConn = new SqlConnection(_connectionString);
        await execConn.OpenAsync();

        // Step 1: Hydrate — HydrationExecutor creates [#page] as part of its batch
        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            keyset,
            SqlDialect.Mssql,
            CancellationToken.None
        );

        var descriptorUriLookup = MssqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );

        // Step 3: Reconstitute (pure in-memory)
        _reconstitutedDocument = DocumentReconstituter.Reconstitute(
            documentId: MssqlDescriptorProjectionFixture.DocumentId701,
            tableRowsInDependencyOrder: hydratedPage.TableRowsInDependencyOrder,
            referenceProjectionPlans: [],
            descriptorProjectionSources: readPlan.Model.DescriptorEdgeSources,
            descriptorUriLookup: descriptorUriLookup
        );
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null && MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_returns_a_reconstituted_json_object()
    {
        _reconstitutedDocument.Should().BeOfType<JsonObject>();
    }

    [Test]
    public void It_does_not_emit_the_descriptor_property_key()
    {
        _reconstitutedDocument["gradeLevelDescriptor"].Should().BeNull();
    }

    [Test]
    public void It_does_not_emit_json_null_at_the_descriptor_path()
    {
        var gradeLevelDescriptor = _reconstitutedDocument["gradeLevelDescriptor"];
        gradeLevelDescriptor.Should().BeNull();
    }
}

/// <summary>
/// Verifies that hydrated descriptor rows return all distinct descriptor URIs for a page
/// containing multiple documents and still deduplicate shared descriptors.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_Page_With_Multiple_Documents_And_Distinct_Descriptors_Mssql
{
    private string _databaseName = null!;
    private string _connectionString = null!;
    private IReadOnlyDictionary<long, string> _lookup = null!;
    private IReadOnlyDictionary<long, string> _sharedDescriptorLookup = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a MssqlAdmin connection string.");
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        await using var setupConn = new SqlConnection(_connectionString);
        await setupConn.OpenAsync();

        await MssqlDescriptorProjectionFixture.ProvisionSchemaAsync(setupConn);
        await MssqlDescriptorProjectionFixture.InsertTestDataAsync(setupConn);

        // Three documents each referencing a distinct descriptor
        await using var insertDistinctCmd = new SqlCommand(
            $"""
            INSERT INTO [{MssqlDescriptorProjectionFixture.TestSchema}].[StudentSchoolAssociation]
                ([DocumentId], [GradeLevelDescriptor_DescriptorId])
            VALUES
                ({MssqlDescriptorProjectionFixture.DocumentId700}, {MssqlDescriptorProjectionFixture.DescriptorId901}),
                ({MssqlDescriptorProjectionFixture.DocumentId701}, {MssqlDescriptorProjectionFixture.DescriptorId902}),
                ({MssqlDescriptorProjectionFixture.DocumentId702}, {MssqlDescriptorProjectionFixture.DescriptorId903});
            """,
            setupConn
        );
        await insertDistinctCmd.ExecuteNonQueryAsync();

        var resourceModel = MssqlDescriptorProjectionFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(resourceModel);

        await using var distinctConn = new SqlConnection(_connectionString);
        await distinctConn.OpenAsync();
        var distinctHydratedPage = await HydrationExecutor.ExecuteAsync(
            distinctConn,
            readPlan,
            MssqlDescriptorProjectionPageKeysetHelper.CreatePageKeyset(
                MssqlDescriptorProjectionFixture.TestSchema,
                MssqlDescriptorProjectionFixture.DocumentId700,
                MssqlDescriptorProjectionFixture.DocumentId701,
                MssqlDescriptorProjectionFixture.DocumentId702
            ),
            SqlDialect.Mssql,
            CancellationToken.None
        );
        _lookup = MssqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            distinctHydratedPage.DescriptorRowsInPlanOrder
        );

        // --- shared descriptor run: 703 and 700 both reference descriptor 901 ---
        await using var insertSharedCmd = new SqlCommand(
            $"""
            INSERT INTO [{MssqlDescriptorProjectionFixture.TestSchema}].[StudentSchoolAssociation]
                ([DocumentId], [GradeLevelDescriptor_DescriptorId])
            VALUES
                ({MssqlDescriptorProjectionFixture.DocumentId703}, {MssqlDescriptorProjectionFixture.DescriptorId901});
            """,
            setupConn
        );
        await insertSharedCmd.ExecuteNonQueryAsync();

        await using var sharedConn = new SqlConnection(_connectionString);
        await sharedConn.OpenAsync();
        var sharedHydratedPage = await HydrationExecutor.ExecuteAsync(
            sharedConn,
            readPlan,
            MssqlDescriptorProjectionPageKeysetHelper.CreatePageKeyset(
                MssqlDescriptorProjectionFixture.TestSchema,
                MssqlDescriptorProjectionFixture.DocumentId700,
                MssqlDescriptorProjectionFixture.DocumentId703
            ),
            SqlDialect.Mssql,
            CancellationToken.None
        );
        _sharedDescriptorLookup = MssqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            sharedHydratedPage.DescriptorRowsInPlanOrder
        );
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null && MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_returns_one_entry_per_distinct_descriptor_id()
    {
        _lookup.Should().HaveCount(3);
    }

    [Test]
    public void It_contains_all_expected_descriptor_uris()
    {
        _lookup
            .Values.Should()
            .Contain(MssqlDescriptorProjectionFixture.Uri901)
            .And.Contain(MssqlDescriptorProjectionFixture.Uri902)
            .And.Contain(MssqlDescriptorProjectionFixture.Uri903);
    }

    [Test]
    public void It_maps_each_descriptor_id_to_the_correct_uri()
    {
        _lookup[MssqlDescriptorProjectionFixture.DescriptorId901]
            .Should()
            .Be(MssqlDescriptorProjectionFixture.Uri901);
        _lookup[MssqlDescriptorProjectionFixture.DescriptorId902]
            .Should()
            .Be(MssqlDescriptorProjectionFixture.Uri902);
        _lookup[MssqlDescriptorProjectionFixture.DescriptorId903]
            .Should()
            .Be(MssqlDescriptorProjectionFixture.Uri903);
    }

    [Test]
    public void It_deduplicates_a_shared_descriptor_to_a_single_lookup_entry()
    {
        // Documents 700 and 703 both reference DescriptorId 901
        _sharedDescriptorLookup.Should().HaveCount(1);
        _sharedDescriptorLookup[MssqlDescriptorProjectionFixture.DescriptorId901]
            .Should()
            .Be(MssqlDescriptorProjectionFixture.Uri901);
    }
}

/// <summary>
/// Verifies that reconstituting a document whose descriptor FK was first set to a non-null value
/// and then cleared to NULL correctly omits the descriptor property from the output.
/// This specifically exercises the update-to-null path, which is distinct from an FK that
/// was never populated.
/// Task 3 (cleared-to-null acceptance case): MSSQL dialect mirror.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_Descriptor_FK_Cleared_To_Null_Omits_Property_From_Reconstituted_Document_Mssql
{
    private string _databaseName = null!;
    private string _connectionString = null!;
    private JsonNode _reconstitutedDocument = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a MssqlAdmin connection string.");
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        await using var setupConn = new SqlConnection(_connectionString);
        await setupConn.OpenAsync();

        await MssqlDescriptorProjectionFixture.ProvisionSchemaAsync(setupConn);
        await MssqlDescriptorProjectionFixture.InsertTestDataAsync(setupConn);

        // Insert document 702 with a non-null descriptor FK first
        await using var insertCmd = new SqlCommand(
            $"""
            INSERT INTO [{MssqlDescriptorProjectionFixture.TestSchema}].[StudentSchoolAssociation]
                ([DocumentId], [GradeLevelDescriptor_DescriptorId])
            VALUES
                ({MssqlDescriptorProjectionFixture.DocumentId702}, {MssqlDescriptorProjectionFixture.DescriptorId901});
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        // Simulate an update that clears the descriptor FK to NULL
        await using var updateCmd = new SqlCommand(
            $"""
            UPDATE [{MssqlDescriptorProjectionFixture.TestSchema}].[StudentSchoolAssociation]
            SET [GradeLevelDescriptor_DescriptorId] = NULL
            WHERE [DocumentId] = {MssqlDescriptorProjectionFixture.DocumentId702};
            """,
            setupConn
        );
        await updateCmd.ExecuteNonQueryAsync();

        var resourceModel = MssqlDescriptorProjectionFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(resourceModel);
        var keyset = new PageKeysetSpec.Single(MssqlDescriptorProjectionFixture.DocumentId702);

        await using var execConn = new SqlConnection(_connectionString);
        await execConn.OpenAsync();

        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            keyset,
            SqlDialect.Mssql,
            CancellationToken.None
        );

        var descriptorUriLookup = MssqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );

        _reconstitutedDocument = DocumentReconstituter.Reconstitute(
            documentId: MssqlDescriptorProjectionFixture.DocumentId702,
            tableRowsInDependencyOrder: hydratedPage.TableRowsInDependencyOrder,
            referenceProjectionPlans: [],
            descriptorProjectionSources: readPlan.Model.DescriptorEdgeSources,
            descriptorUriLookup: descriptorUriLookup
        );
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null && MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_returns_a_reconstituted_json_object()
    {
        _reconstitutedDocument.Should().BeOfType<JsonObject>();
    }

    [Test]
    public void It_does_not_emit_the_descriptor_property_key()
    {
        _reconstitutedDocument["gradeLevelDescriptor"].Should().BeNull();
    }
}

/// <summary>
/// Verifies that hydrated descriptor rows correctly resolve all descriptor URIs when the page is
/// materialized from a real <see cref="PageKeysetSpec.Query"/> via <see cref="HydrationExecutor"/>.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_Multi_Document_Page_Created_Via_Query_Keyset_Returns_All_Descriptor_URIs_Mssql
{
    private string _databaseName = null!;
    private string _connectionString = null!;
    private IReadOnlyDictionary<long, string> _lookup = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a MssqlAdmin connection string.");
        }

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        await using var setupConn = new SqlConnection(_connectionString);
        await setupConn.OpenAsync();

        await MssqlDescriptorProjectionFixture.ProvisionSchemaAsync(setupConn);
        await MssqlDescriptorProjectionFixture.InsertTestDataAsync(setupConn);

        // Three documents each referencing a distinct descriptor
        await using var insertCmd = new SqlCommand(
            $"""
            INSERT INTO [{MssqlDescriptorProjectionFixture.TestSchema}].[StudentSchoolAssociation]
                ([DocumentId], [GradeLevelDescriptor_DescriptorId])
            VALUES
                ({MssqlDescriptorProjectionFixture.DocumentId700}, {MssqlDescriptorProjectionFixture.DescriptorId901}),
                ({MssqlDescriptorProjectionFixture.DocumentId701}, {MssqlDescriptorProjectionFixture.DescriptorId902}),
                ({MssqlDescriptorProjectionFixture.DocumentId702}, {MssqlDescriptorProjectionFixture.DescriptorId903});
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        var resourceModel = MssqlDescriptorProjectionFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(resourceModel);

        // Use a real query keyset so HydrationExecutor materializes [#page] from the query.
        var keyset = new PageKeysetSpec.Query(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: $"""
                SELECT r.[DocumentId]
                FROM [{MssqlDescriptorProjectionFixture.TestSchema}].[StudentSchoolAssociation] r
                ORDER BY r.[DocumentId]
                OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
                """,
                TotalCountSql: null,
                PageParametersInOrder:
                [
                    new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
                ],
                TotalCountParametersInOrder: null
            ),
            new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = 25L }
        );

        await using var execConn = new SqlConnection(_connectionString);
        await execConn.OpenAsync();

        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            keyset,
            SqlDialect.Mssql,
            CancellationToken.None
        );
        _lookup = MssqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_databaseName is not null && MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public void It_returns_one_entry_per_distinct_descriptor_id_across_the_page()
    {
        _lookup.Should().HaveCount(3);
    }

    [Test]
    public void It_resolves_all_three_descriptor_uris()
    {
        _lookup[MssqlDescriptorProjectionFixture.DescriptorId901]
            .Should()
            .Be(MssqlDescriptorProjectionFixture.Uri901);
        _lookup[MssqlDescriptorProjectionFixture.DescriptorId902]
            .Should()
            .Be(MssqlDescriptorProjectionFixture.Uri902);
        _lookup[MssqlDescriptorProjectionFixture.DescriptorId903]
            .Should()
            .Be(MssqlDescriptorProjectionFixture.Uri903);
    }
}

file static class MssqlDescriptorProjectionTestHelper
{
    internal static IReadOnlyDictionary<long, string> BuildDescriptorUriLookup(
        IReadOnlyList<HydratedDescriptorRows> descriptorRowsInPlanOrder
    )
    {
        Dictionary<long, string> lookup = [];

        foreach (var descriptorRows in descriptorRowsInPlanOrder)
        {
            foreach (var row in descriptorRows.Rows)
            {
                lookup.TryAdd(row.DescriptorId, row.Uri);
            }
        }

        return lookup;
    }
}

file static class MssqlDescriptorProjectionPageKeysetHelper
{
    internal static PageKeysetSpec.Query CreatePageKeyset(string schemaName, params long[] documentIds)
    {
        return new PageKeysetSpec.Query(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: $"""
                SELECT r.[DocumentId]
                FROM [{schemaName}].[StudentSchoolAssociation] r
                WHERE r.[DocumentId] IN ({string.Join(", ", documentIds)})
                ORDER BY r.[DocumentId]
                OFFSET 0 ROWS
                """,
                TotalCountSql: null,
                PageParametersInOrder: [],
                TotalCountParametersInOrder: null
            ),
            new Dictionary<string, object?>()
        );
    }
}
