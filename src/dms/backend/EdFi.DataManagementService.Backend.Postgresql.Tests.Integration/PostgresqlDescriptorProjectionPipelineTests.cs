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
/// Shared schema, model, and data constants for PostgreSQL descriptor projection integration tests
/// that cover a resource with both required and optional descriptor foreign keys.
/// </summary>
internal static class DescriptorProjectionPipelineFixture
{
    internal const string TestSchema = "descprojpipelinetest";
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
                "PK_descprojpipelinetest_CourseOffering",
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

    internal static async Task ProvisionSchemaAsync(NpgsqlConnection connection)
    {
        await ExecuteSqlAsync(
            connection,
            $"""
            CREATE SCHEMA IF NOT EXISTS dms;
            DROP SCHEMA IF EXISTS "{TestSchema}" CASCADE;
            CREATE SCHEMA IF NOT EXISTS "{TestSchema}";

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

            CREATE TABLE "{TestSchema}"."CourseOffering" (
                "DocumentId" bigint PRIMARY KEY,
                "AcademicSubjectDescriptor_DescriptorId" bigint NOT NULL,
                "InstructionLanguageDescriptor_DescriptorId" bigint NULL
            );
            """
        );
    }

    internal static async Task DropSchemaAsync(NpgsqlConnection connection)
    {
        await ExecuteSqlAsync(
            connection,
            $"""
            DROP SCHEMA IF EXISTS "{TestSchema}" CASCADE;

            DELETE FROM dms."Descriptor"
            WHERE "DocumentId" IN ({DescriptorId910}, {DescriptorId911});

            DELETE FROM dms."Document"
            WHERE "DocumentId" IN ({DocumentId810}, {DocumentId811});
            """
        );
    }

    internal static async Task InsertTestDataAsync(NpgsqlConnection connection)
    {
        await ExecuteSqlAsync(
            connection,
            $"""
            DELETE FROM dms."Descriptor"
            WHERE "DocumentId" IN ({DescriptorId910}, {DescriptorId911});

            DELETE FROM dms."Document"
            WHERE "DocumentId" IN ({DocumentId810}, {DocumentId811});

            INSERT INTO dms."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion", "IdentityVersion") VALUES
                ({DocumentId810}, '81000000-0000-0000-0000-000000000810', 0, 1, 1),
                ({DocumentId811}, '81100000-0000-0000-0000-000000000811', 0, 1, 1);

            INSERT INTO dms."Descriptor" ("DocumentId", "Namespace", "CodeValue", "ShortDescription", "Discriminator", "Uri") VALUES
                ({DescriptorId910}, 'uri://ed-fi.org/AcademicSubjectDescriptor', 'English Language Arts', 'English Language Arts', 'edfi.AcademicSubjectDescriptor', '{Uri910}'),
                ({DescriptorId911}, 'uri://ed-fi.org/InstructionLanguageDescriptor', 'English', 'English', 'edfi.InstructionLanguageDescriptor', '{Uri911}');
            """
        );
    }

    private static async Task ExecuteSqlAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_Postgresql_Reconstitution_With_Both_Descriptor_Fks_Set
{
    private NpgsqlDataSource _dataSource = null!;
    private JsonNode _reconstitutedDocument = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var setupConn = await _dataSource.OpenConnectionAsync();
        await DescriptorProjectionPipelineFixture.ProvisionSchemaAsync(setupConn);
        await DescriptorProjectionPipelineFixture.InsertTestDataAsync(setupConn);

        await using var insertCmd = new NpgsqlCommand(
            $"""
            INSERT INTO "{DescriptorProjectionPipelineFixture.TestSchema}"."CourseOffering"
                ("DocumentId", "AcademicSubjectDescriptor_DescriptorId", "InstructionLanguageDescriptor_DescriptorId")
            VALUES
                ({DescriptorProjectionPipelineFixture.DocumentId810},
                 {DescriptorProjectionPipelineFixture.DescriptorId910},
                 {DescriptorProjectionPipelineFixture.DescriptorId911});
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        var resourceModel = DescriptorProjectionPipelineFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(resourceModel);

        await using var execConn = await _dataSource.OpenConnectionAsync();
        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            new PageKeysetSpec.Single(DescriptorProjectionPipelineFixture.DocumentId810),
            SqlDialect.Pgsql,
            CancellationToken.None
        );

        var descriptorUriLookup = PostgresqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );

        _reconstitutedDocument = DocumentReconstituter.Reconstitute(
            DescriptorProjectionPipelineFixture.DocumentId810,
            hydratedPage.TableRowsInDependencyOrder,
            readPlan.ReferenceIdentityProjectionPlansInDependencyOrder,
            readPlan.Model.DescriptorEdgeSources,
            descriptorUriLookup
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await DescriptorProjectionPipelineFixture.DropSchemaAsync(conn);
            await _dataSource.DisposeAsync();
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
            .Be(DescriptorProjectionPipelineFixture.Uri910);
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
            .Be(DescriptorProjectionPipelineFixture.Uri911);
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_Postgresql_Reconstitution_With_Optional_Descriptor_Fk_Null
{
    private NpgsqlDataSource _dataSource = null!;
    private JsonNode _reconstitutedDocument = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var setupConn = await _dataSource.OpenConnectionAsync();
        await DescriptorProjectionPipelineFixture.ProvisionSchemaAsync(setupConn);
        await DescriptorProjectionPipelineFixture.InsertTestDataAsync(setupConn);

        await using var insertCmd = new NpgsqlCommand(
            $"""
            INSERT INTO "{DescriptorProjectionPipelineFixture.TestSchema}"."CourseOffering"
                ("DocumentId", "AcademicSubjectDescriptor_DescriptorId", "InstructionLanguageDescriptor_DescriptorId")
            VALUES
                ({DescriptorProjectionPipelineFixture.DocumentId811},
                 {DescriptorProjectionPipelineFixture.DescriptorId910},
                 NULL);
            """,
            setupConn
        );
        await insertCmd.ExecuteNonQueryAsync();

        var resourceModel = DescriptorProjectionPipelineFixture.BuildResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(resourceModel);

        await using var execConn = await _dataSource.OpenConnectionAsync();
        var hydratedPage = await HydrationExecutor.ExecuteAsync(
            execConn,
            readPlan,
            new PageKeysetSpec.Single(DescriptorProjectionPipelineFixture.DocumentId811),
            SqlDialect.Pgsql,
            CancellationToken.None
        );

        var descriptorUriLookup = PostgresqlDescriptorProjectionTestHelper.BuildDescriptorUriLookup(
            hydratedPage.DescriptorRowsInPlanOrder
        );

        _reconstitutedDocument = DocumentReconstituter.Reconstitute(
            DescriptorProjectionPipelineFixture.DocumentId811,
            hydratedPage.TableRowsInDependencyOrder,
            readPlan.ReferenceIdentityProjectionPlansInDependencyOrder,
            readPlan.Model.DescriptorEdgeSources,
            descriptorUriLookup
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource is not null)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await DescriptorProjectionPipelineFixture.DropSchemaAsync(conn);
            await _dataSource.DisposeAsync();
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
            .Be(DescriptorProjectionPipelineFixture.Uri910);
    }

    [Test]
    public void It_does_not_emit_the_optional_descriptor_property_key()
    {
        _reconstitutedDocument.AsObject()["instructionLanguageDescriptor"].Should().BeNull();
    }
}
