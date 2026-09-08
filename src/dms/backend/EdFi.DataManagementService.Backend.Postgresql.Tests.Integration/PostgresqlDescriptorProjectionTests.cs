// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Integration tests for descriptor projection SQL execution against a live PostgreSQL database.
/// All test fixtures share a common schema <c>"descprojtest"</c> with a minimal
/// <c>"StudentSchoolAssociation"</c> table that has one nullable descriptor FK column.
/// </summary>
/// <remarks>
/// DocumentIds 700–703 and DescriptorIds 901–903 are reserved for these fixtures.
/// The dms."Descriptor" table is expected to exist (created by DatabaseSetupFixture or DDL migration).
/// </remarks>
internal static class DescriptorProjectionFixture
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

    /// <summary>
    /// Builds the descriptor projection plan SQL for the test schema.
    /// The SQL uses the "page" keyset temp table (Postgresql dialect).
    /// </summary>
    internal static DescriptorProjectionPlan BuildDescriptorProjectionPlan() =>
        new(
            SelectByKeysetSql: $"""
            SELECT
                p."DescriptorId",
                d."Uri"
            FROM
                (
                    SELECT DISTINCT t0."{FkColumnName.Value}" AS "DescriptorId"
                    FROM "{TestSchema}"."StudentSchoolAssociation" t0
                    INNER JOIN "page" k ON t0."DocumentId" = k."DocumentId"
                    WHERE t0."{FkColumnName.Value}" IS NOT NULL
                ) p
            INNER JOIN "dms"."Descriptor" d ON d."DocumentId" = p."DescriptorId"
            ORDER BY
                p."DescriptorId" ASC
            ;

            """,
            ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
            SourcesInOrder:
            [
                new DescriptorProjectionSource(
                    DescriptorValuePath: GradeLevelDescriptorPath,
                    Table: TableName,
                    DescriptorResource: GradeLevelDescriptorResource,
                    DescriptorIdColumnOrdinal: 1
                ),
            ]
        );

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

    internal static ResourceReadPlan BuildReadPlan(DbTableModel tableModel) =>
        new(
            Model: new RelationalResourceModel(
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
            ),
            KeysetTable: KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            TablePlansInDependencyOrder:
            [
                new TableReadPlan(
                    TableModel: tableModel,
                    SelectByKeysetSql: $"""
                    SELECT
                        r."DocumentId",
                        r."{FkColumnName.Value}"
                    FROM "{TestSchema}"."StudentSchoolAssociation" r
                    INNER JOIN "page" k ON r."DocumentId" = k."DocumentId"
                    ORDER BY
                        r."DocumentId" ASC
                    ;

                    """
                ),
            ],
            ReferenceIdentityProjectionPlansInDependencyOrder: [],
            DescriptorProjectionPlansInOrder: [BuildDescriptorProjectionPlan()]
        );

    internal static async Task ProvisionSchemaAsync(NpgsqlConnection connection)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            DROP SCHEMA IF EXISTS {TestSchema} CASCADE;
            CREATE SCHEMA {TestSchema};
            CREATE SCHEMA IF NOT EXISTS dms;

            CREATE TABLE IF NOT EXISTS dms."Document" (
                "DocumentId" bigint PRIMARY KEY,
                "DocumentUuid" uuid NOT NULL,
                "ResourceKeyId" smallint NOT NULL DEFAULT 0,
                "ContentVersion" bigint NOT NULL DEFAULT 1,
                "IdentityVersion" bigint NOT NULL DEFAULT 1,
                "ContentLastModifiedAt" timestamptz NOT NULL DEFAULT now(),
                "IdentityLastModifiedAt" timestamptz NOT NULL DEFAULT now(),
                "CreatedAt" timestamptz NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS dms."Descriptor" (
                "DocumentId" bigint PRIMARY KEY,
                "Namespace" varchar(255) NOT NULL DEFAULT '',
                "CodeValue" varchar(50) NOT NULL DEFAULT '',
                "ShortDescription" varchar(75) NOT NULL DEFAULT '',
                "Description" varchar(1024) NULL,
                "EffectiveBeginDate" date NULL,
                "EffectiveEndDate" date NULL,
                "Discriminator" varchar(128) NOT NULL DEFAULT '',
                "Uri" varchar(306) NOT NULL
            );

            CREATE TABLE {TestSchema}."StudentSchoolAssociation" (
                "DocumentId" bigint PRIMARY KEY,
                "GradeLevelDescriptor_DescriptorId" bigint NULL
            );
            """,
            connection
        );
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task InsertTestDataAsync(NpgsqlConnection connection)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            DELETE FROM dms."Descriptor" WHERE "DocumentId" IN (901, 902, 903);
            DELETE FROM dms."Document" WHERE "DocumentId" IN (700, 701, 702, 703);

            INSERT INTO dms."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion", "IdentityVersion") VALUES
                (700, '70000000-0000-0000-0000-000000000700', 0, 1, 1),
                (701, '70100000-0000-0000-0000-000000000701', 0, 1, 1),
                (702, '70200000-0000-0000-0000-000000000702', 0, 1, 1),
                (703, '70300000-0000-0000-0000-000000000703', 0, 1, 1);

            INSERT INTO dms."Descriptor" ("DocumentId", "Namespace", "CodeValue", "ShortDescription", "Discriminator", "Uri") VALUES
                (901, 'uri://ed-fi.org/GradeLevelDescriptor', 'Ninth grade', 'Ninth grade', 'edfi.GradeLevelDescriptor', '{Uri901}'),
                (902, 'uri://ed-fi.org/GradeLevelDescriptor', 'Tenth grade', 'Tenth grade', 'edfi.GradeLevelDescriptor', '{Uri902}'),
                (903, 'uri://ed-fi.org/GradeLevelDescriptor', 'Eleventh grade', 'Eleventh grade', 'edfi.GradeLevelDescriptor', '{Uri903}');
            """,
            connection
        );
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task DropSchemaAsync(NpgsqlConnection connection)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            DROP SCHEMA IF EXISTS {TestSchema} CASCADE;
            DELETE FROM dms."Descriptor" WHERE "DocumentId" IN (901, 902, 903);
            DELETE FROM dms."Document" WHERE "DocumentId" IN (700, 701, 702, 703);
            """,
            connection
        );
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Verifies that hydrated descriptor rows return the correct URI string for a non-null
/// required descriptor FK column.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_Required_Descriptor_FK_Resolves_To_URI
{
    private NpgsqlDataSource _dataSource = null!;
    private IReadOnlyDictionary<long, string> _lookup = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var setupConn = await _dataSource.OpenConnectionAsync();
        await DescriptorProjectionFixture.ProvisionSchemaAsync(setupConn);
        await DescriptorProjectionFixture.InsertTestDataAsync(setupConn);

        await using var insertCmd = new NpgsqlCommand(
            $"""
            INSERT INTO {DescriptorProjectionFixture.TestSchema}."StudentSchoolAssociation"
                ("DocumentId", "GradeLevelDescriptor_DescriptorId")
            VALUES
                ({DescriptorProjectionFixture.DocumentId700}, {DescriptorProjectionFixture.DescriptorId901});
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        var tableModel = DescriptorProjectionFixture.BuildTableModel();
        var readPlan = DescriptorProjectionFixture.BuildReadPlan(tableModel);

        await using var execConn = await _dataSource.OpenConnectionAsync();
        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            new PageKeysetSpec.Single(DescriptorProjectionFixture.DocumentId700),
            SqlDialect.Pgsql,
            CancellationToken.None
        );
        _lookup = PostgresqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await DescriptorProjectionFixture.DropSchemaAsync(conn);
            await _dataSource.DisposeAsync();
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
        _lookup[DescriptorProjectionFixture.DescriptorId901].Should().Be(DescriptorProjectionFixture.Uri901);
    }

    [Test]
    public void It_stores_the_uri_exactly_as_in_dms_Descriptor()
    {
        _lookup[DescriptorProjectionFixture.DescriptorId901]
            .Should()
            .Be("uri://ed-fi.org/GradeLevelDescriptor#Ninth grade");
    }
}

/// <summary>
/// Verifies that a null descriptor FK causes the descriptor property to be absent from the
/// reconstituted JSON document (no JSON null emitted, no missing-URI exception).
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_Null_Descriptor_FK_Omits_Property_From_Reconstituted_Document
{
    private NpgsqlDataSource _dataSource = null!;
    private JsonNode _reconstitutedDocument = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var setupConn = await _dataSource.OpenConnectionAsync();
        await DescriptorProjectionFixture.ProvisionSchemaAsync(setupConn);
        await DescriptorProjectionFixture.InsertTestDataAsync(setupConn);

        // Document 701 has a NULL descriptor FK
        await using var insertCmd = new NpgsqlCommand(
            $"""
            INSERT INTO {DescriptorProjectionFixture.TestSchema}."StudentSchoolAssociation"
                ("DocumentId", "GradeLevelDescriptor_DescriptorId")
            VALUES
                ({DescriptorProjectionFixture.DocumentId701}, NULL);
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        var tableModel = DescriptorProjectionFixture.BuildTableModel();
        var readPlan = DescriptorProjectionFixture.BuildReadPlan(tableModel);
        var keyset = new PageKeysetSpec.Single(DescriptorProjectionFixture.DocumentId701);

        await using var execConn = await _dataSource.OpenConnectionAsync();
        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            keyset,
            SqlDialect.Pgsql,
            CancellationToken.None
        );
        var descriptorUriLookup = PostgresqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );

        _reconstitutedDocument = DocumentReconstituter.Reconstitute(
            documentId: DescriptorProjectionFixture.DocumentId701,
            tableRowsInDependencyOrder: hydratedPage.TableRowsInDependencyOrder,
            referenceProjectionPlans: [],
            descriptorProjectionSources: readPlan.Model.DescriptorEdgeSources,
            descriptorUriLookup: descriptorUriLookup
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await DescriptorProjectionFixture.DropSchemaAsync(conn);
            await _dataSource.DisposeAsync();
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
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_Page_With_Multiple_Documents_And_Distinct_Descriptors
{
    private NpgsqlDataSource _dataSource = null!;
    private IReadOnlyDictionary<long, string> _lookup = null!;
    private IReadOnlyDictionary<long, string> _sharedDescriptorLookup = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var setupConn = await _dataSource.OpenConnectionAsync();
        await DescriptorProjectionFixture.ProvisionSchemaAsync(setupConn);
        await DescriptorProjectionFixture.InsertTestDataAsync(setupConn);

        // Three documents each referencing a distinct descriptor
        await using var insertDistinctCmd = new NpgsqlCommand(
            $"""
            INSERT INTO {DescriptorProjectionFixture.TestSchema}."StudentSchoolAssociation"
                ("DocumentId", "GradeLevelDescriptor_DescriptorId")
            VALUES
                ({DescriptorProjectionFixture.DocumentId700}, {DescriptorProjectionFixture.DescriptorId901}),
                ({DescriptorProjectionFixture.DocumentId701}, {DescriptorProjectionFixture.DescriptorId902}),
                ({DescriptorProjectionFixture.DocumentId702}, {DescriptorProjectionFixture.DescriptorId903});
            """,
            setupConn
        );
        await insertDistinctCmd.ExecuteNonQueryAsync();

        var tableModel = DescriptorProjectionFixture.BuildTableModel();
        var readPlan = DescriptorProjectionFixture.BuildReadPlan(tableModel);

        await using var distinctConn = await _dataSource.OpenConnectionAsync();
        var distinctHydratedPage = await HydrationExecutor.ExecuteAsync(
            distinctConn,
            readPlan,
            PostgresqlDescriptorProjectionPageKeysetHelper.CreatePageKeyset(
                DescriptorProjectionFixture.TestSchema,
                DescriptorProjectionFixture.DocumentId700,
                DescriptorProjectionFixture.DocumentId701,
                DescriptorProjectionFixture.DocumentId702
            ),
            SqlDialect.Pgsql,
            CancellationToken.None
        );
        _lookup = PostgresqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            distinctHydratedPage.DescriptorRowsInPlanOrder
        );

        // --- shared descriptor run: 703 and 700 both reference descriptor 901 ---
        await using var insertSharedCmd = new NpgsqlCommand(
            $"""
            INSERT INTO {DescriptorProjectionFixture.TestSchema}."StudentSchoolAssociation"
                ("DocumentId", "GradeLevelDescriptor_DescriptorId")
            VALUES
                ({DescriptorProjectionFixture.DocumentId703}, {DescriptorProjectionFixture.DescriptorId901});
            """,
            setupConn
        );
        await insertSharedCmd.ExecuteNonQueryAsync();

        await using var sharedConn = await _dataSource.OpenConnectionAsync();
        var sharedHydratedPage = await HydrationExecutor.ExecuteAsync(
            sharedConn,
            readPlan,
            PostgresqlDescriptorProjectionPageKeysetHelper.CreatePageKeyset(
                DescriptorProjectionFixture.TestSchema,
                DescriptorProjectionFixture.DocumentId700,
                DescriptorProjectionFixture.DocumentId703
            ),
            SqlDialect.Pgsql,
            CancellationToken.None
        );
        _sharedDescriptorLookup = PostgresqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            sharedHydratedPage.DescriptorRowsInPlanOrder
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await DescriptorProjectionFixture.DropSchemaAsync(conn);
            await _dataSource.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_one_entry_per_distinct_descriptor_id_across_the_page()
    {
        _lookup.Should().HaveCount(3);
    }

    [Test]
    public void It_resolves_all_distinct_descriptor_uris()
    {
        _lookup[DescriptorProjectionFixture.DescriptorId901].Should().Be(DescriptorProjectionFixture.Uri901);
        _lookup[DescriptorProjectionFixture.DescriptorId902].Should().Be(DescriptorProjectionFixture.Uri902);
        _lookup[DescriptorProjectionFixture.DescriptorId903].Should().Be(DescriptorProjectionFixture.Uri903);
    }

    [Test]
    public void It_deduplicates_to_one_entry_when_two_documents_share_the_same_descriptor()
    {
        _sharedDescriptorLookup.Should().HaveCount(1);
        _sharedDescriptorLookup[DescriptorProjectionFixture.DescriptorId901]
            .Should()
            .Be(DescriptorProjectionFixture.Uri901);
    }
}

/// <summary>
/// Verifies that reconstituting a document whose descriptor FK was first set to a non-null value
/// and then cleared to NULL correctly omits the descriptor property from the output.
/// This specifically exercises the update-to-null path, which is distinct from an FK that
/// was never populated.
/// Task 3 (cleared-to-null acceptance case): PostgreSQL full pipeline.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_Descriptor_FK_Cleared_To_Null_Omits_Property_From_Reconstituted_Document
{
    private NpgsqlDataSource _dataSource = null!;
    private JsonNode _reconstitutedDocument = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var setupConn = await _dataSource.OpenConnectionAsync();
        await DescriptorProjectionFixture.ProvisionSchemaAsync(setupConn);
        await DescriptorProjectionFixture.InsertTestDataAsync(setupConn);

        // Insert document 702 with a non-null descriptor FK first
        await using var insertCmd = new NpgsqlCommand(
            $"""
            INSERT INTO {DescriptorProjectionFixture.TestSchema}."StudentSchoolAssociation"
                ("DocumentId", "GradeLevelDescriptor_DescriptorId")
            VALUES
                ({DescriptorProjectionFixture.DocumentId702}, {DescriptorProjectionFixture.DescriptorId901});
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        // Simulate an update that clears the descriptor FK to NULL
        await using var updateCmd = new NpgsqlCommand(
            $"""
            UPDATE {DescriptorProjectionFixture.TestSchema}."StudentSchoolAssociation"
            SET "GradeLevelDescriptor_DescriptorId" = NULL
            WHERE "DocumentId" = {DescriptorProjectionFixture.DocumentId702};
            """,
            setupConn
        );
        await updateCmd.ExecuteNonQueryAsync();

        var tableModel = DescriptorProjectionFixture.BuildTableModel();
        var readPlan = DescriptorProjectionFixture.BuildReadPlan(tableModel);
        var keyset = new PageKeysetSpec.Single(DescriptorProjectionFixture.DocumentId702);

        await using var execConn = await _dataSource.OpenConnectionAsync();
        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            keyset,
            SqlDialect.Pgsql,
            CancellationToken.None
        );
        var descriptorUriLookup = PostgresqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );

        _reconstitutedDocument = DocumentReconstituter.Reconstitute(
            documentId: DescriptorProjectionFixture.DocumentId702,
            tableRowsInDependencyOrder: hydratedPage.TableRowsInDependencyOrder,
            referenceProjectionPlans: [],
            descriptorProjectionSources: readPlan.Model.DescriptorEdgeSources,
            descriptorUriLookup: descriptorUriLookup
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await DescriptorProjectionFixture.DropSchemaAsync(conn);
            await _dataSource.DisposeAsync();
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
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_Multi_Document_Page_Created_Via_Query_Keyset_Returns_All_Descriptor_URIs
{
    private NpgsqlDataSource _dataSource = null!;
    private IReadOnlyDictionary<long, string> _lookup = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var setupConn = await _dataSource.OpenConnectionAsync();
        await DescriptorProjectionFixture.ProvisionSchemaAsync(setupConn);
        await DescriptorProjectionFixture.InsertTestDataAsync(setupConn);

        // Three documents each referencing a distinct descriptor
        await using var insertCmd = new NpgsqlCommand(
            $"""
            INSERT INTO {DescriptorProjectionFixture.TestSchema}."StudentSchoolAssociation"
                ("DocumentId", "GradeLevelDescriptor_DescriptorId")
            VALUES
                ({DescriptorProjectionFixture.DocumentId700}, {DescriptorProjectionFixture.DescriptorId901}),
                ({DescriptorProjectionFixture.DocumentId701}, {DescriptorProjectionFixture.DescriptorId902}),
                ({DescriptorProjectionFixture.DocumentId702}, {DescriptorProjectionFixture.DescriptorId903});
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        var tableModel = DescriptorProjectionFixture.BuildTableModel();
        var readPlan = DescriptorProjectionFixture.BuildReadPlan(tableModel);

        // Use a real query keyset so HydrationExecutor materializes the page from the query,
        // not from a single-document INSERT.
        var keyset = new PageKeysetSpec.Query(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: $"""
                SELECT r."DocumentId"
                FROM "{DescriptorProjectionFixture.TestSchema}"."StudentSchoolAssociation" r
                ORDER BY r."DocumentId"
                LIMIT @limit OFFSET @offset
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

        await using var execConn = await _dataSource.OpenConnectionAsync();
        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            keyset,
            SqlDialect.Pgsql,
            CancellationToken.None
        );
        _lookup = PostgresqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await DescriptorProjectionFixture.DropSchemaAsync(conn);
            await _dataSource.DisposeAsync();
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
        _lookup[DescriptorProjectionFixture.DescriptorId901].Should().Be(DescriptorProjectionFixture.Uri901);
        _lookup[DescriptorProjectionFixture.DescriptorId902].Should().Be(DescriptorProjectionFixture.Uri902);
        _lookup[DescriptorProjectionFixture.DescriptorId903].Should().Be(DescriptorProjectionFixture.Uri903);
    }
}

internal static class PostgresqlDescriptorProjectionTestHelper
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

internal static class PostgresqlDescriptorProjectionPageKeysetHelper
{
    internal static PageKeysetSpec.Query CreatePageKeyset(string schemaName, params long[] documentIds)
    {
        return new PageKeysetSpec.Query(
            new PageDocumentIdSqlPlan(
                PageDocumentIdSql: $"""
                SELECT r."DocumentId"
                FROM "{schemaName}"."StudentSchoolAssociation" r
                WHERE r."DocumentId" IN ({string.Join(", ", documentIds)})
                ORDER BY r."DocumentId"
                """,
                TotalCountSql: null,
                PageParametersInOrder: [],
                TotalCountParametersInOrder: null
            ),
            new Dictionary<string, object?>()
        );
    }
}
