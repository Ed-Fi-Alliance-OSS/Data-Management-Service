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
/// Shared schema, model, and data constants for SQL Server descriptor projection integration tests
/// that cover a resource with both required and optional descriptor foreign keys.
/// </summary>
internal static class MssqlDescriptorProjectionPipelineFixture
{
    internal const string TestSchema = "descprojpipelinemssqltest";
    internal const long DocumentId810 = 810L;
    internal const long DocumentId811 = 811L;
    internal const long DescriptorId910 = 910L;
    internal const long DescriptorId911 = 911L;
    internal const string Uri910 = "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts";
    internal const string Uri911 = "uri://ed-fi.org/InstructionLanguageDescriptor#English";

    internal static readonly DbSchemaName Schema = new(TestSchema);
    internal static readonly DbTableName TableName = new(Schema, "CourseOffering");

    internal static readonly JsonPathExpression AcademicSubjectDescriptorPath = new(
        "$.academicSubjectDescriptor",
        [new JsonPathSegment.Property("academicSubjectDescriptor")]
    );

    internal static readonly JsonPathExpression InstructionLanguageDescriptorPath = new(
        "$.instructionLanguageDescriptor",
        [new JsonPathSegment.Property("instructionLanguageDescriptor")]
    );

    internal static readonly QualifiedResourceName AcademicSubjectDescriptorResource = new(
        "Ed-Fi",
        "AcademicSubjectDescriptor"
    );

    internal static readonly QualifiedResourceName InstructionLanguageDescriptorResource = new(
        "Ed-Fi",
        "InstructionLanguageDescriptor"
    );

    internal static readonly DbColumnName AcademicSubjectFkColumn = new(
        "AcademicSubjectDescriptor_DescriptorId"
    );

    internal static readonly DbColumnName InstructionLanguageFkColumn = new(
        "InstructionLanguageDescriptor_DescriptorId"
    );

    internal static RelationalResourceModel BuildResourceModel()
    {
        var tableModel = BuildTableModel();
        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "CourseOffering"),
            PhysicalSchema: Schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: tableModel,
            TablesInDependencyOrder: [tableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources:
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: AcademicSubjectDescriptorPath,
                    Table: TableName,
                    FkColumn: AcademicSubjectFkColumn,
                    DescriptorResource: AcademicSubjectDescriptorResource
                ),
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: InstructionLanguageDescriptorPath,
                    Table: TableName,
                    FkColumn: InstructionLanguageFkColumn,
                    DescriptorResource: InstructionLanguageDescriptorResource
                ),
            ]
        );
    }

    internal static DbTableModel BuildTableModel() =>
        new(
            Table: TableName,
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                "PK_descprojpipelinemssqltest_CourseOffering",
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
                    ColumnName: AcademicSubjectFkColumn,
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: AcademicSubjectDescriptorPath,
                    TargetResource: AcademicSubjectDescriptorResource
                ),
                new DbColumnModel(
                    ColumnName: InstructionLanguageFkColumn,
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: InstructionLanguageDescriptorPath,
                    TargetResource: InstructionLanguageDescriptorResource
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

            CREATE TABLE [{TestSchema}].[CourseOffering] (
                [DocumentId] bigint PRIMARY KEY,
                [AcademicSubjectDescriptor_DescriptorId] bigint NOT NULL,
                [InstructionLanguageDescriptor_DescriptorId] bigint NULL
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
                (810, '81000000-0000-0000-0000-000000000810', 0, 1, 1),
                (811, '81100000-0000-0000-0000-000000000811', 0, 1, 1);

            INSERT INTO [dms].[Descriptor] ([DocumentId], [Namespace], [CodeValue], [ShortDescription], [Discriminator], [Uri]) VALUES
                (910, 'uri://ed-fi.org/AcademicSubjectDescriptor', 'English Language Arts', 'English Language Arts', 'edfi.AcademicSubjectDescriptor', '{Uri910}'),
                (911, 'uri://ed-fi.org/InstructionLanguageDescriptor', 'English', 'English', 'edfi.InstructionLanguageDescriptor', '{Uri911}');
            """
        );
    }

    private static async Task ExecuteSqlAsync(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_Mssql_Reconstitution_With_Both_Descriptor_Fks_Set
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

        await MssqlDescriptorProjectionPipelineFixture.ProvisionSchemaAsync(setupConn);
        await MssqlDescriptorProjectionPipelineFixture.InsertTestDataAsync(setupConn);

        await using var insertCmd = new SqlCommand(
            $"""
            INSERT INTO [{MssqlDescriptorProjectionPipelineFixture.TestSchema}].[CourseOffering]
                ([DocumentId], [AcademicSubjectDescriptor_DescriptorId], [InstructionLanguageDescriptor_DescriptorId])
            VALUES
                ({MssqlDescriptorProjectionPipelineFixture.DocumentId810},
                 {MssqlDescriptorProjectionPipelineFixture.DescriptorId910},
                 {MssqlDescriptorProjectionPipelineFixture.DescriptorId911});
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        var resourceModel = MssqlDescriptorProjectionPipelineFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(resourceModel);

        await using var execConn = new SqlConnection(_connectionString);
        await execConn.OpenAsync();

        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            new PageKeysetSpec.Single(MssqlDescriptorProjectionPipelineFixture.DocumentId810),
            SqlDialect.Mssql,
            CancellationToken.None
        );

        var descriptorUriLookup = MssqlDescriptorProjectionPipelineTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );

        _reconstitutedDocument = DocumentReconstituter.Reconstitute(
            MssqlDescriptorProjectionPipelineFixture.DocumentId810,
            hydratedPage.TableRowsInDependencyOrder,
            readPlan.ReferenceIdentityProjectionPlansInDependencyOrder,
            readPlan.Model.DescriptorEdgeSources,
            descriptorUriLookup
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
    public void It_returns_a_reconstituted_document()
    {
        _reconstitutedDocument.Should().NotBeNull();
    }

    [Test]
    public void It_emits_the_required_descriptor_uri_at_the_correct_json_path()
    {
        var doc = _reconstitutedDocument.AsObject();
        doc["academicSubjectDescriptor"]
            .Should()
            .NotBeNull()
            .And.Subject.As<JsonValue>()
            .GetValue<string>()
            .Should()
            .Be(MssqlDescriptorProjectionPipelineFixture.Uri910);
    }

    [Test]
    public void It_emits_the_optional_descriptor_uri_at_the_correct_json_path()
    {
        var doc = _reconstitutedDocument.AsObject();
        doc["instructionLanguageDescriptor"]
            .Should()
            .NotBeNull()
            .And.Subject.As<JsonValue>()
            .GetValue<string>()
            .Should()
            .Be(MssqlDescriptorProjectionPipelineFixture.Uri911);
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_Mssql_Reconstitution_With_Optional_Descriptor_Fk_Null
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

        await MssqlDescriptorProjectionPipelineFixture.ProvisionSchemaAsync(setupConn);
        await MssqlDescriptorProjectionPipelineFixture.InsertTestDataAsync(setupConn);

        await using var insertCmd = new SqlCommand(
            $"""
            INSERT INTO [{MssqlDescriptorProjectionPipelineFixture.TestSchema}].[CourseOffering]
                ([DocumentId], [AcademicSubjectDescriptor_DescriptorId], [InstructionLanguageDescriptor_DescriptorId])
            VALUES
                ({MssqlDescriptorProjectionPipelineFixture.DocumentId811},
                 {MssqlDescriptorProjectionPipelineFixture.DescriptorId910},
                 NULL);
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        var resourceModel = MssqlDescriptorProjectionPipelineFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(resourceModel);

        await using var execConn = new SqlConnection(_connectionString);
        await execConn.OpenAsync();

        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            new PageKeysetSpec.Single(MssqlDescriptorProjectionPipelineFixture.DocumentId811),
            SqlDialect.Mssql,
            CancellationToken.None
        );

        var descriptorUriLookup = MssqlDescriptorProjectionPipelineTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );

        _reconstitutedDocument = DocumentReconstituter.Reconstitute(
            MssqlDescriptorProjectionPipelineFixture.DocumentId811,
            hydratedPage.TableRowsInDependencyOrder,
            readPlan.ReferenceIdentityProjectionPlansInDependencyOrder,
            readPlan.Model.DescriptorEdgeSources,
            descriptorUriLookup
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
    public void It_returns_a_reconstituted_document()
    {
        _reconstitutedDocument.Should().NotBeNull();
    }

    [Test]
    public void It_emits_the_required_descriptor_uri_at_the_correct_json_path()
    {
        var doc = _reconstitutedDocument.AsObject();
        doc["academicSubjectDescriptor"]
            .Should()
            .NotBeNull()
            .And.Subject.As<JsonValue>()
            .GetValue<string>()
            .Should()
            .Be(MssqlDescriptorProjectionPipelineFixture.Uri910);
    }

    [Test]
    public void It_does_not_emit_the_optional_descriptor_property_key()
    {
        _reconstitutedDocument.AsObject()["instructionLanguageDescriptor"].Should().BeNull();
    }
}

file static class MssqlDescriptorProjectionPipelineTestHelper
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
