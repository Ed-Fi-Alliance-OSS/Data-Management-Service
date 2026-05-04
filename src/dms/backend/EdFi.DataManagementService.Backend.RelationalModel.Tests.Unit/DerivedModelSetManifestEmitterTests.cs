// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using EdFi.DataManagementService.Backend.RelationalModel.Manifest;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_Descriptor_Only_Model_Set_When_Emitting_Manifest
{
    private string _manifest = default!;

    [SetUp]
    public void Setup()
    {
        var projectSchema = CommonInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var derivedSet = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _manifest = DerivedModelSetManifestEmitter.Emit(derivedSet);
    }

    [Test]
    public void It_should_produce_deterministic_output()
    {
        var projectSchema = CommonInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var derivedSet = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var firstEmission = DerivedModelSetManifestEmitter.Emit(derivedSet);
        var secondEmission = DerivedModelSetManifestEmitter.Emit(derivedSet);

        secondEmission.Should().Be(firstEmission);
        firstEmission.Should().Be(_manifest, "independent builds must produce identical manifests");
    }

    [Test]
    public void It_should_use_unix_line_endings()
    {
        _manifest.Should().NotContain("\r");
        _manifest.Should().Contain("\n");
    }

    [Test]
    public void It_should_end_with_a_trailing_newline()
    {
        _manifest.Should().EndWith("\n");
    }

    [Test]
    public void It_should_not_contain_trailing_whitespace_on_any_line()
    {
        foreach (var line in _manifest.Split('\n'))
        {
            if (line.Length > 0)
            {
                line.Should().NotMatchRegex(@"\s$", "no trailing whitespace expected");
            }
        }
    }

    [Test]
    public void It_should_include_dialect()
    {
        _manifest.Should().Contain("\"dialect\": \"pgsql\"");
    }

    [Test]
    public void It_should_include_projects_section()
    {
        _manifest.Should().Contain("\"projects\":");
        _manifest.Should().Contain("\"project_name\": \"Ed-Fi\"");
    }

    [Test]
    public void It_should_include_resources_section()
    {
        _manifest.Should().Contain("\"resources\":");
        _manifest.Should().Contain("\"resource_name\": \"GradeLevelDescriptor\"");
        _manifest.Should().Contain("\"storage_kind\": \"SharedDescriptorTable\"");
    }

    [Test]
    public void It_should_include_abstract_identity_tables_section()
    {
        _manifest.Should().Contain("\"abstract_identity_tables\":");
    }

    [Test]
    public void It_should_include_abstract_union_views_section()
    {
        _manifest.Should().Contain("\"abstract_union_views\":");
    }

    [Test]
    public void It_should_include_indexes_section()
    {
        _manifest.Should().Contain("\"indexes\":");
    }

    [Test]
    public void It_should_include_triggers_section()
    {
        _manifest.Should().Contain("\"triggers\":");
    }

    [Test]
    public void It_should_not_include_resource_details_when_not_requested()
    {
        _manifest.Should().NotContain("\"resource_details\":");
    }
}

[TestFixture]
public class Given_A_Contact_Model_Set_With_Extension_When_Emitting_Manifest
{
    private string _manifest = default!;

    [SetUp]
    public void Setup()
    {
        var coreSchema = CommonInventoryTestSchemaBuilder.BuildExtensionCoreProjectSchema();
        var extensionSchema = CommonInventoryTestSchemaBuilder.BuildExtensionProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreSchema,
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var derivedSet = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _manifest = DerivedModelSetManifestEmitter.Emit(derivedSet);
    }

    [Test]
    public void It_should_include_both_projects()
    {
        _manifest.Should().Contain("\"project_name\": \"Ed-Fi\"");
        _manifest.Should().Contain("\"project_name\": \"Sample\"");
    }

    [Test]
    public void It_should_include_contact_resource()
    {
        _manifest.Should().Contain("\"resource_name\": \"Contact\"");
        _manifest.Should().Contain("\"storage_kind\": \"RelationalTables\"");
    }

    [Test]
    public void It_should_include_indexes_for_relational_tables()
    {
        _manifest.Should().Contain("\"indexes\":");
        _manifest.Should().Contain("\"name\": \"PK_Contact\"");
        _manifest.Should().Contain("\"kind\": \"PrimaryKey\"");
        _manifest.Should().Contain("\"kind\": \"UniqueConstraint\"");
    }

    [Test]
    public void It_should_include_triggers_for_relational_tables()
    {
        _manifest.Should().Contain("\"triggers\":");
        _manifest.Should().Contain("\"kind\": \"DocumentStamping\"");
    }

    [Test]
    public void It_should_include_resource_details_when_requested()
    {
        var coreSchema = CommonInventoryTestSchemaBuilder.BuildExtensionCoreProjectSchema();
        var extensionSchema = CommonInventoryTestSchemaBuilder.BuildExtensionProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreSchema,
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var derivedSet = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var detailedManifest = DerivedModelSetManifestEmitter.Emit(
            derivedSet,
            new HashSet<QualifiedResourceName> { new("Ed-Fi", "Contact") }
        );

        detailedManifest.Should().Contain("\"resource_details\":");
        detailedManifest.Should().Contain("\"resource_name\": \"Contact\"");
        detailedManifest.Should().Contain("\"tables\":");
        detailedManifest.Should().Contain("\"constraints\":");
        detailedManifest.Should().Contain("\"document_reference_bindings\":");
        detailedManifest.Should().Contain("\"descriptor_edge_sources\":");
        detailedManifest.Should().Contain("\"extension_sites\":");
    }

    [Test]
    public void It_should_emit_identity_metadata_in_resource_details_tables()
    {
        var coreSchema = CommonInventoryTestSchemaBuilder.BuildExtensionCoreProjectSchema();
        var extensionSchema = CommonInventoryTestSchemaBuilder.BuildExtensionProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreSchema,
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var derivedSet = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var detailedManifest = DerivedModelSetManifestEmitter.Emit(
            derivedSet,
            new HashSet<QualifiedResourceName> { new("Ed-Fi", "Contact") }
        );
        var root =
            JsonNode.Parse(detailedManifest) as JsonObject
            ?? throw new InvalidOperationException("Expected manifest to be a JSON object.");
        var resourceDetails =
            root["resource_details"] as JsonArray
            ?? throw new InvalidOperationException("Expected resource_details to be a JSON array.");
        var resourceDetail =
            resourceDetails.Should().ContainSingle().Subject as JsonObject
            ?? throw new InvalidOperationException("Expected resource detail to be a JSON object.");
        var tables =
            resourceDetail["tables"] as JsonArray
            ?? throw new InvalidOperationException("Expected tables to be a JSON array.");

        tables.Should().NotBeEmpty();

        foreach (var table in tables)
        {
            var identityMetadata =
                table?["identity_metadata"] as JsonObject
                ?? throw new InvalidOperationException("Expected identity_metadata to be a JSON object.");

            identityMetadata["table_kind"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            identityMetadata["physical_row_identity_columns"].Should().NotBeNull();
            identityMetadata["root_scope_locator_columns"].Should().NotBeNull();
            identityMetadata["immediate_parent_scope_locator_columns"].Should().NotBeNull();
            identityMetadata["semantic_identity_bindings"].Should().NotBeNull();
        }
    }
}

[TestFixture]
public class Given_A_Model_Set_With_NullOrTrue_Constraint_When_Emitting_Detailed_Manifest
{
    private string _manifest = default!;

    [SetUp]
    public void Setup()
    {
        var derivedSet = CreateDerivedModelSetWithNullOrTrueConstraint();

        _manifest = DerivedModelSetManifestEmitter.Emit(
            derivedSet,
            new HashSet<QualifiedResourceName> { new("Ed-Fi", "School") }
        );
    }

    [Test]
    public void It_should_emit_null_or_true_constraints()
    {
        var root =
            JsonNode.Parse(_manifest) as JsonObject
            ?? throw new InvalidOperationException("Expected manifest to be a JSON object.");

        var resourceDetails =
            root["resource_details"] as JsonArray
            ?? throw new InvalidOperationException("Expected resource_details to be a JSON array.");

        var resourceDetail =
            resourceDetails.Single() as JsonObject
            ?? throw new InvalidOperationException("Expected resource detail entry to be a JSON object.");

        var tables =
            resourceDetail["tables"] as JsonArray
            ?? throw new InvalidOperationException("Expected tables to be a JSON array.");

        var table =
            tables.Single() as JsonObject
            ?? throw new InvalidOperationException("Expected table to be a JSON object.");

        var constraints =
            table["constraints"] as JsonArray
            ?? throw new InvalidOperationException("Expected constraints to be a JSON array.");

        var constraint =
            constraints.Single() as JsonObject
            ?? throw new InvalidOperationException("Expected constraint to be a JSON object.");

        constraint["kind"]!.GetValue<string>().Should().Be("NullOrTrue");
        constraint["name"]!.GetValue<string>().Should().Be("CK_School_FiscalYear_Present_NullOrTrue");
        constraint["column"]!.GetValue<string>().Should().Be("FiscalYear_Present");
    }

    private static DerivedRelationalModelSet CreateDerivedModelSetWithNullOrTrueConstraint()
    {
        var schema = new DbSchemaName("edfi");
        var resource = new QualifiedResourceName("Ed-Fi", "School");

        var resourceKeyEntry = new ResourceKeyEntry(
            ResourceKeyId: 1,
            Resource: resource,
            ResourceVersion: "1.0.0",
            IsAbstractResource: false
        );

        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "hash",
            ResourceKeyCount: 1,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo(
                    ProjectEndpointName: "ed-fi",
                    ProjectName: "Ed-Fi",
                    ProjectVersion: "1.0.0",
                    IsExtensionProject: false,
                    ProjectHash: "hash"
                ),
            ],
            ResourceKeysInIdOrder: [resourceKeyEntry]
        );

        var projectSchema = new ProjectSchemaInfo(
            ProjectEndpointName: "ed-fi",
            ProjectName: "Ed-Fi",
            ProjectVersion: "1.0.0",
            IsExtensionProject: false,
            PhysicalSchema: schema
        );

        var keyColumn = new DbKeyColumn(
            RelationalNameConventions.DocumentIdColumnName,
            ColumnKind.ParentKeyPart
        );
        var presenceColumn = new DbColumnName("FiscalYear_Present");

        var table = new DbTableModel(
            new DbTableName(schema, "School"),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey("PK_School", [keyColumn]),
            Columns:
            [
                new DbColumnModel(
                    RelationalNameConventions.DocumentIdColumnName,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    presenceColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Boolean),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            Constraints:
            [
                new TableConstraint.NullOrTrue("CK_School_FiscalYear_Present_NullOrTrue", presenceColumn),
            ]
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            table,
            TablesInDependencyOrder: [table],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        var concreteResource = new ConcreteResourceModel(
            resourceKeyEntry,
            ResourceStorageKind.RelationalTables,
            relationalModel
        );

        return new DerivedRelationalModelSet(
            effectiveSchema,
            SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder: [projectSchema],
            ConcreteResourcesInNameOrder: [concreteResource],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );
    }
}

[TestFixture]
public class Given_An_Index_With_Include_Columns_When_Emitting_Manifest
{
    private string _manifest = default!;

    [SetUp]
    public void Setup()
    {
        var schema = new DbSchemaName("edfi");
        var table = new DbTableName(schema, "StudentSchoolAssociation");

        var indexWithInclude = new DbIndexInfo(
            new DbIndexName("IX_StudentSchoolAssociation_SchoolId_Unified_Auth"),
            table,
            KeyColumns: [new DbColumnName("SchoolId_Unified")],
            IsUnique: false,
            Kind: DbIndexKind.Authorization,
            IncludeColumns: [new DbColumnName("Student_DocumentId")]
        );

        var indexWithoutInclude = new DbIndexInfo(
            new DbIndexName("IX_StudentSchoolAssociation_PlainKey"),
            table,
            KeyColumns: [new DbColumnName("PlainKey")],
            IsUnique: false,
            Kind: DbIndexKind.ForeignKeySupport,
            IncludeColumns: null
        );

        var indexWithEmptyInclude = new DbIndexInfo(
            new DbIndexName("IX_StudentSchoolAssociation_EmptyInclude"),
            table,
            KeyColumns: [new DbColumnName("EmptyIncludeKey")],
            IsUnique: false,
            Kind: DbIndexKind.ForeignKeySupport,
            IncludeColumns: []
        );

        var derivedSet = BuildEmptyModelSetWithIndexes([
            indexWithInclude,
            indexWithoutInclude,
            indexWithEmptyInclude,
        ]);

        _manifest = DerivedModelSetManifestEmitter.Emit(derivedSet);
    }

    [Test]
    public void It_should_round_trip_include_columns_when_present()
    {
        var entry = SingleIndexByName("IX_StudentSchoolAssociation_SchoolId_Unified_Auth");

        var includeColumns =
            entry["include_columns"] as JsonArray
            ?? throw new InvalidOperationException("Expected include_columns to be a JSON array.");

        includeColumns.Select(c => c!.GetValue<string>()).Should().Equal("Student_DocumentId");
    }

    [Test]
    public void It_should_omit_include_columns_when_null()
    {
        var entry = SingleIndexByName("IX_StudentSchoolAssociation_PlainKey");
        entry.ContainsKey("include_columns").Should().BeFalse();
    }

    [Test]
    public void It_should_omit_include_columns_when_empty()
    {
        var entry = SingleIndexByName("IX_StudentSchoolAssociation_EmptyInclude");
        entry.ContainsKey("include_columns").Should().BeFalse();
    }

    private JsonObject SingleIndexByName(string indexName)
    {
        var root =
            JsonNode.Parse(_manifest) as JsonObject
            ?? throw new InvalidOperationException("Expected manifest to be a JSON object.");
        var indexes =
            root["indexes"] as JsonArray
            ?? throw new InvalidOperationException("Expected indexes to be a JSON array.");

        return indexes.OfType<JsonObject>().Single(i => i["name"]!.GetValue<string>() == indexName);
    }

    private static DerivedRelationalModelSet BuildEmptyModelSetWithIndexes(IReadOnlyList<DbIndexInfo> indexes)
    {
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "hash",
            ResourceKeyCount: 0,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo(
                    ProjectEndpointName: "ed-fi",
                    ProjectName: "Ed-Fi",
                    ProjectVersion: "1.0.0",
                    IsExtensionProject: false,
                    ProjectHash: "hash"
                ),
            ],
            ResourceKeysInIdOrder: []
        );

        return new DerivedRelationalModelSet(
            effectiveSchema,
            SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder:
            [
                new ProjectSchemaInfo(
                    ProjectEndpointName: "ed-fi",
                    ProjectName: "Ed-Fi",
                    ProjectVersion: "1.0.0",
                    IsExtensionProject: false,
                    PhysicalSchema: new DbSchemaName("edfi")
                ),
            ],
            ConcreteResourcesInNameOrder: [],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: indexes,
            TriggersInCreateOrder: []
        );
    }
}
