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
/// Shared schema, model, and data constants for the key-unified descriptor alias projection tests.
/// </summary>
/// <remarks>
/// Uses schema <c>"descprojaliasinttest"</c>, table <c>"UnifiedAliasResource"</c> with:
/// <list type="bullet">
/// <item><c>DocumentId bigint PRIMARY KEY</c></item>
/// <item><c>Canonical_DescriptorId bigint NULL</c> — physical storage column (no SourceJsonPath)</item>
/// <item><c>Alias1_DescriptorId bigint GENERATED ALWAYS AS ("Canonical_DescriptorId") STORED</c> — first alias ($.subject1Descriptor)</item>
/// <item><c>Alias2_DescriptorId bigint GENERATED ALWAYS AS ("Canonical_DescriptorId") STORED</c> — second alias ($.subject2Descriptor)</item>
/// </list>
/// DocumentIds 820–821 and DescriptorId 920 are reserved for these fixtures.
/// </remarks>
internal static class DescriptorProjectionAliasFixture
{
    internal const string TestSchema = "descprojaliasinttest";
    internal const long DocumentId820 = 820L;
    internal const long DocumentId821 = 821L;
    internal const long DescriptorId920 = 920L;
    internal const string Uri920 = "uri://ed-fi.org/SubjectDescriptor#Mathematics";

    internal static readonly DbSchemaName Schema = new(TestSchema);
    internal static readonly DbTableName TableName = new(Schema, "UnifiedAliasResource");

    internal static readonly DbColumnName CanonicalFkColumn = new("Canonical_DescriptorId");
    internal static readonly DbColumnName Alias1FkColumn = new("Alias1_DescriptorId");
    internal static readonly DbColumnName Alias2FkColumn = new("Alias2_DescriptorId");

    internal static readonly JsonPathExpression Subject1DescriptorPath = new(
        "$.subject1Descriptor",
        [new JsonPathSegment.Property("subject1Descriptor")]
    );

    internal static readonly JsonPathExpression Subject2DescriptorPath = new(
        "$.subject2Descriptor",
        [new JsonPathSegment.Property("subject2Descriptor")]
    );

    internal static readonly QualifiedResourceName SubjectDescriptorResource = new(
        "Ed-Fi",
        "SubjectDescriptor"
    );

    /// <summary>
    /// Builds a <see cref="DbTableModel"/> where the descriptor FK column is a
    /// <see cref="ColumnStorage.UnifiedAlias"/> pointing to a canonical storage column.
    /// Column layout (ordinals):
    /// <list type="number">
    /// <item>0: DocumentId (ParentKeyPart, Stored)</item>
    /// <item>1: Canonical_DescriptorId (DescriptorFk, Stored, no SourceJsonPath)</item>
    /// <item>2: Alias1_DescriptorId (DescriptorFk, UnifiedAlias → Canonical, SourceJsonPath=$.subject1Descriptor)</item>
    /// <item>3: Alias2_DescriptorId (DescriptorFk, UnifiedAlias → Canonical, SourceJsonPath=$.subject2Descriptor)</item>
    /// </list>
    /// </summary>
    internal static DbTableModel BuildTableModel() =>
        new(
            Table: TableName,
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_descprojaliasinttest_UnifiedAliasResource",
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
                    ColumnName: CanonicalFkColumn,
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: SubjectDescriptorResource
                ),
                new DbColumnModel(
                    ColumnName: Alias1FkColumn,
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: Subject1DescriptorPath,
                    TargetResource: SubjectDescriptorResource,
                    Storage: new ColumnStorage.UnifiedAlias(CanonicalFkColumn, PresenceColumn: null)
                ),
                new DbColumnModel(
                    ColumnName: Alias2FkColumn,
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: Subject2DescriptorPath,
                    TargetResource: SubjectDescriptorResource,
                    Storage: new ColumnStorage.UnifiedAlias(CanonicalFkColumn, PresenceColumn: null)
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
    /// Builds a <see cref="RelationalResourceModel"/> with two <see cref="DescriptorEdgeSource"/>
    /// entries, both resolving through <see cref="ColumnStorage.UnifiedAlias"/> to the same
    /// canonical storage column.
    /// </summary>
    internal static RelationalResourceModel BuildResourceModel()
    {
        var tableModel = BuildTableModel();
        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "UnifiedAliasResource"),
            PhysicalSchema: Schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: tableModel,
            TablesInDependencyOrder: [tableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources:
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: Subject1DescriptorPath,
                    Table: TableName,
                    FkColumn: Alias1FkColumn,
                    DescriptorResource: SubjectDescriptorResource
                ),
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: Subject2DescriptorPath,
                    Table: TableName,
                    FkColumn: Alias2FkColumn,
                    DescriptorResource: SubjectDescriptorResource
                ),
            ]
        );
    }

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

            CREATE TABLE {TestSchema}."UnifiedAliasResource" (
                "DocumentId" bigint PRIMARY KEY,
                "Canonical_DescriptorId" bigint NULL,
                "Alias1_DescriptorId" bigint GENERATED ALWAYS AS ("Canonical_DescriptorId") STORED,
                "Alias2_DescriptorId" bigint GENERATED ALWAYS AS ("Canonical_DescriptorId") STORED
            );
            """,
            connection
        );
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task InsertSharedDescriptorDataAsync(NpgsqlConnection connection)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            DELETE FROM dms."Descriptor" WHERE "DocumentId" = 920;
            DELETE FROM dms."Document" WHERE "DocumentId" IN (820, 821);

            INSERT INTO dms."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion", "IdentityVersion") VALUES
                (820, '82000000-0000-0000-0000-000000000820', 0, 1, 1),
                (821, '82100000-0000-0000-0000-000000000821', 0, 1, 1);

            INSERT INTO dms."Descriptor" ("DocumentId", "Namespace", "CodeValue", "ShortDescription", "Discriminator", "Uri") VALUES
                (920, 'uri://ed-fi.org/SubjectDescriptor', 'Mathematics', 'Mathematics', 'edfi.SubjectDescriptor', '{Uri920}');
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
            DELETE FROM dms."Descriptor" WHERE "DocumentId" = 920;
            DELETE FROM dms."Document" WHERE "DocumentId" IN (820, 821);
            """,
            connection
        );
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Verifies that hydrated descriptor rows for a plan whose SQL targets the canonical storage column
/// return the correct descriptor URI, even though data was written through the alias FK column path.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_Key_Unified_Alias_Descriptor_FK_Executor_Returns_URI_From_Canonical_Column
{
    private NpgsqlDataSource _dataSource = null!;
    private IReadOnlyDictionary<long, string> _lookup = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var setupConn = await _dataSource.OpenConnectionAsync();
        await DescriptorProjectionAliasFixture.ProvisionSchemaAsync(setupConn);
        await DescriptorProjectionAliasFixture.InsertSharedDescriptorDataAsync(setupConn);

        // Insert DocumentId=820 referencing Canonical_DescriptorId=920.
        // Alias columns are GENERATED ALWAYS AS and will automatically carry 920.
        await using var insertCmd = new NpgsqlCommand(
            $"""
            INSERT INTO {DescriptorProjectionAliasFixture.TestSchema}."UnifiedAliasResource"
                ("DocumentId", "Canonical_DescriptorId")
            VALUES
                ({DescriptorProjectionAliasFixture.DocumentId820}, {DescriptorProjectionAliasFixture.DescriptorId920});
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        // Compile a real plan using ReadPlanCompiler so the SQL targets the canonical column.
        var resourceModel = DescriptorProjectionAliasFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(resourceModel);

        await using var execConn = await _dataSource.OpenConnectionAsync();
        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            new PageKeysetSpec.Single(DescriptorProjectionAliasFixture.DocumentId820),
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
            await DescriptorProjectionAliasFixture.DropSchemaAsync(conn);
            await _dataSource.DisposeAsync();
        }
    }

    [Test]
    public void It_returns_a_non_empty_lookup()
    {
        _lookup.Should().NotBeEmpty();
    }

    [Test]
    public void It_resolves_the_canonical_descriptor_id_to_the_correct_uri()
    {
        _lookup[DescriptorProjectionAliasFixture.DescriptorId920]
            .Should()
            .Be(DescriptorProjectionAliasFixture.Uri920);
    }

    [Test]
    public void It_contains_exactly_one_entry_for_the_single_canonical_descriptor()
    {
        _lookup.Should().HaveCount(1);
    }
}

/// <summary>
/// Verifies that the full hydrate + descriptor reconstitution pipeline correctly emits URI
/// strings at both alias descriptor JSON paths when the canonical FK column is set, and
/// omits both paths when the canonical FK column is NULL.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_Key_Unified_Alias_Descriptor_FK_Full_Pipeline_Emits_URIs_At_Alias_Paths
{
    private NpgsqlDataSource _dataSource = null!;
    private JsonNode _reconstitutedWithDescriptor = null!;
    private JsonNode _reconstitutedWithNullDescriptor = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var setupConn = await _dataSource.OpenConnectionAsync();
        await DescriptorProjectionAliasFixture.ProvisionSchemaAsync(setupConn);
        await DescriptorProjectionAliasFixture.InsertSharedDescriptorDataAsync(setupConn);

        // DocumentId=820: Canonical_DescriptorId=920 → aliases get 920 via GENERATED.
        // DocumentId=821: Canonical_DescriptorId=NULL → aliases get NULL via GENERATED.
        await using var insertCmd = new NpgsqlCommand(
            $"""
            INSERT INTO {DescriptorProjectionAliasFixture.TestSchema}."UnifiedAliasResource"
                ("DocumentId", "Canonical_DescriptorId")
            VALUES
                ({DescriptorProjectionAliasFixture.DocumentId820}, {DescriptorProjectionAliasFixture.DescriptorId920}),
                ({DescriptorProjectionAliasFixture.DocumentId821}, NULL);
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        var resourceModel = DescriptorProjectionAliasFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(resourceModel);

        _reconstitutedWithDescriptor = await RunPipelineAsync(
            readPlan,
            new PageKeysetSpec.Single(DescriptorProjectionAliasFixture.DocumentId820),
            DescriptorProjectionAliasFixture.DocumentId820
        );

        _reconstitutedWithNullDescriptor = await RunPipelineAsync(
            readPlan,
            new PageKeysetSpec.Single(DescriptorProjectionAliasFixture.DocumentId821),
            DescriptorProjectionAliasFixture.DocumentId821
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await DescriptorProjectionAliasFixture.DropSchemaAsync(conn);
            await _dataSource.DisposeAsync();
        }
    }

    [Test]
    public void It_emits_subject1Descriptor_uri_at_alias1_path_when_canonical_is_set()
    {
        _reconstitutedWithDescriptor["subject1Descriptor"]
            ?.GetValue<string>()
            .Should()
            .Be(DescriptorProjectionAliasFixture.Uri920);
    }

    [Test]
    public void It_emits_subject2Descriptor_uri_at_alias2_path_when_canonical_is_set()
    {
        _reconstitutedWithDescriptor["subject2Descriptor"]
            ?.GetValue<string>()
            .Should()
            .Be(DescriptorProjectionAliasFixture.Uri920);
    }

    [Test]
    public void It_omits_subject1Descriptor_path_when_canonical_is_null()
    {
        _reconstitutedWithNullDescriptor["subject1Descriptor"].Should().BeNull();
    }

    [Test]
    public void It_omits_subject2Descriptor_path_when_canonical_is_null()
    {
        _reconstitutedWithNullDescriptor["subject2Descriptor"].Should().BeNull();
    }

    private async Task<JsonNode> RunPipelineAsync(
        ResourceReadPlan readPlan,
        PageKeysetSpec keyset,
        long documentId
    )
    {
        await using var execConn = await _dataSource.OpenConnectionAsync();
        await using var tx = await execConn.BeginTransactionAsync();

        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            keyset,
            SqlDialect.Pgsql,
            tx,
            CancellationToken.None
        );

        var descriptorUriLookup = PostgresqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );

        var reconstituted = DocumentReconstituter.Reconstitute(
            documentId: documentId,
            tableRowsInDependencyOrder: hydratedPage.TableRowsInDependencyOrder,
            referenceProjectionPlans: [],
            descriptorProjectionSources: readPlan.Model.DescriptorEdgeSources,
            descriptorUriLookup: descriptorUriLookup
        );

        await tx.RollbackAsync();
        return reconstituted;
    }
}
