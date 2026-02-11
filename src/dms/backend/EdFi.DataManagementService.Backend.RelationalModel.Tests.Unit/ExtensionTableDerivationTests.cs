// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for extension table derivation.
/// </summary>
[TestFixture]
public class Given_Extension_Table_Derivation
{
    private RelationalResourceModel _schoolModel = default!;
    private DbTableModel _schoolExtensionRoot = default!;
    private DbTableModel _schoolExtensionAddress = default!;
    private DbTableModel _schoolExtensionSponsor = default!;
    private DbTableModel _schoolBaseRoot = default!;
    private DbTableModel _schoolBaseAddress = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ExtensionTableTestSchemaBuilder.BuildCoreProjectSchema();
        var extensionProjectSchema = ExtensionTableTestSchemaBuilder.BuildExtensionProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(
            new[] { coreProject, extensionProject }
        );
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ExtensionTableDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _schoolModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ProjectName == "Ed-Fi"
                && model.ResourceKey.Resource.ResourceName == "School"
            )
            .RelationalModel;

        _schoolBaseRoot = _schoolModel.TablesInDependencyOrder.Single(table =>
            table.Table.Schema.Value == "edfi" && table.Table.Name == "School"
        );
        _schoolBaseAddress = _schoolModel.TablesInDependencyOrder.Single(table =>
            table.Table.Schema.Value == "edfi" && table.Table.Name == "SchoolAddress"
        );
        _schoolExtensionRoot = _schoolModel.TablesInDependencyOrder.Single(table =>
            table.Table.Schema.Value == "sample" && table.Table.Name == "SchoolExtension"
        );
        _schoolExtensionAddress = _schoolModel.TablesInDependencyOrder.Single(table =>
            table.Table.Schema.Value == "sample" && table.Table.Name == "SchoolExtensionAddress"
        );
        _schoolExtensionSponsor = _schoolModel.TablesInDependencyOrder.Single(table =>
            table.Table.Schema.Value == "sample"
            && table.Table.Name == "SchoolExtensionAddressSponsorReference"
        );
    }

    /// <summary>
    /// It should create extension tables with expected names.
    /// </summary>
    [Test]
    public void It_should_create_extension_tables_with_expected_names()
    {
        _schoolExtensionRoot.JsonScope.Canonical.Should().Be("$._ext.sample");
        _schoolExtensionAddress.JsonScope.Canonical.Should().Be("$._ext.sample.addresses[*]._ext.sample");
        _schoolExtensionSponsor
            .JsonScope.Canonical.Should()
            .Be("$._ext.sample.addresses[*]._ext.sample.sponsorReferences[*]");
    }

    /// <summary>
    /// It should align extension keys to base scopes.
    /// </summary>
    [Test]
    public void It_should_align_extension_keys_to_base_scopes()
    {
        _schoolExtensionRoot
            .Key.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal(_schoolBaseRoot.Key.Columns.Select(column => column.ColumnName.Value));

        _schoolExtensionAddress
            .Key.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal(_schoolBaseAddress.Key.Columns.Select(column => column.ColumnName.Value));

        _schoolExtensionSponsor
            .Key.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal("School_DocumentId", "AddressOrdinal", "Ordinal");
    }

    /// <summary>
    /// It should create fk to base tables with cascade.
    /// </summary>
    [Test]
    public void It_should_create_fk_to_base_tables_with_cascade()
    {
        var rootFk = _schoolExtensionRoot
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.TargetTable.Name == "School");

        rootFk.TargetTable.Schema.Value.Should().Be("edfi");
        rootFk.TargetTable.Name.Should().Be("School");
        rootFk.OnDelete.Should().Be(ReferentialAction.Cascade);

        var addressFk = _schoolExtensionAddress
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.TargetTable.Name == "SchoolAddress");

        addressFk.TargetTable.Schema.Value.Should().Be("edfi");
        addressFk.TargetTable.Name.Should().Be("SchoolAddress");
        addressFk.OnDelete.Should().Be(ReferentialAction.Cascade);
    }

    /// <summary>
    /// It should bind descriptor columns in extension tables.
    /// </summary>
    [Test]
    public void It_should_bind_descriptor_columns_in_extension_tables()
    {
        var descriptorColumn = _schoolExtensionRoot.Columns.Single(column =>
            column.ColumnName.Value == "FavoriteDescriptor_DescriptorId"
        );

        descriptorColumn.Kind.Should().Be(ColumnKind.DescriptorFk);
        descriptorColumn.IsNullable.Should().BeTrue();

        var descriptorFk = _schoolExtensionRoot
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns.Single().Value == "FavoriteDescriptor_DescriptorId");

        descriptorFk.TargetTable.Schema.Value.Should().Be("dms");
        descriptorFk.TargetTable.Name.Should().Be("Descriptor");
    }
}

/// <summary>
/// Test fixture for multiple extension projects extending the same core resource.
/// Verifies that two extension projects (Sample and TPDM) can simultaneously
/// extend School, each producing tables in their own schema namespace.
/// </summary>
[TestFixture]
public class Given_Multiple_Extension_Projects_Extending_Same_Resource
{
    private RelationalResourceModel _schoolModel = default!;
    private DbTableModel _schoolBaseRoot = default!;
    private DbTableModel _sampleExtensionRoot = default!;
    private DbTableModel _sampleExtensionAddress = default!;
    private DbTableModel _tpdmExtensionRoot = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ExtensionTableTestSchemaBuilder.BuildCoreProjectSchema();
        var sampleExtensionSchema = ExtensionTableTestSchemaBuilder.BuildSimpleSampleExtensionProjectSchema();
        var tpdmExtensionSchema = ExtensionTableTestSchemaBuilder.BuildTpdmExtensionProjectSchema();

        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var sampleProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            sampleExtensionSchema,
            isExtensionProject: true
        );
        var tpdmProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            tpdmExtensionSchema,
            isExtensionProject: true
        );

        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            sampleProject,
            tpdmProject,
        ]);
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ExtensionTableDerivationPass(),
        ]);

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _schoolModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ProjectName == "Ed-Fi"
                && model.ResourceKey.Resource.ResourceName == "School"
            )
            .RelationalModel;

        _schoolBaseRoot = _schoolModel.TablesInDependencyOrder.Single(table =>
            table.Table.Schema.Value == "edfi" && table.Table.Name == "School"
        );
        _sampleExtensionRoot = _schoolModel.TablesInDependencyOrder.Single(table =>
            table.Table.Schema.Value == "sample" && table.Table.Name == "SchoolExtension"
        );
        _sampleExtensionAddress = _schoolModel.TablesInDependencyOrder.Single(table =>
            table.Table.Schema.Value == "sample" && table.Table.Name == "SchoolExtensionAddress"
        );
        _tpdmExtensionRoot = _schoolModel.TablesInDependencyOrder.Single(table =>
            table.Table.Schema.Value == "tpdm" && table.Table.Name == "SchoolExtension"
        );
    }

    [Test]
    public void It_should_create_extension_tables_for_both_projects()
    {
        var extensionTables = _schoolModel
            .TablesInDependencyOrder.Where(table =>
                table.Table.Schema.Value == "sample" || table.Table.Schema.Value == "tpdm"
            )
            .ToList();

        extensionTables.Should().HaveCount(3);
        extensionTables
            .Should()
            .Contain(t => t.Table.Schema.Value == "sample" && t.Table.Name == "SchoolExtension");
        extensionTables
            .Should()
            .Contain(t => t.Table.Schema.Value == "sample" && t.Table.Name == "SchoolExtensionAddress");
        extensionTables
            .Should()
            .Contain(t => t.Table.Schema.Value == "tpdm" && t.Table.Name == "SchoolExtension");
    }

    [Test]
    public void It_should_align_sample_extension_root_key_to_base()
    {
        _sampleExtensionRoot
            .Key.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal(_schoolBaseRoot.Key.Columns.Select(column => column.ColumnName.Value));
    }

    [Test]
    public void It_should_align_tpdm_extension_root_key_to_base()
    {
        _tpdmExtensionRoot
            .Key.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal(_schoolBaseRoot.Key.Columns.Select(column => column.ColumnName.Value));
    }

    [Test]
    public void It_should_key_sample_extension_address_by_base_address_keys()
    {
        var baseAddress = _schoolModel.TablesInDependencyOrder.Single(table =>
            table.Table.Schema.Value == "edfi" && table.Table.Name == "SchoolAddress"
        );

        _sampleExtensionAddress
            .Key.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal(baseAddress.Key.Columns.Select(column => column.ColumnName.Value));
    }

    [Test]
    public void It_should_create_cascade_fk_from_sample_extension_root_to_base()
    {
        var fk = _sampleExtensionRoot
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.TargetTable.Name == "School");

        fk.TargetTable.Schema.Value.Should().Be("edfi");
        fk.OnDelete.Should().Be(ReferentialAction.Cascade);
    }

    [Test]
    public void It_should_create_cascade_fk_from_tpdm_extension_root_to_base()
    {
        var fk = _tpdmExtensionRoot
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.TargetTable.Name == "School");

        fk.TargetTable.Schema.Value.Should().Be("edfi");
        fk.OnDelete.Should().Be(ReferentialAction.Cascade);
    }

    [Test]
    public void It_should_include_sample_scalar_column_in_sample_extension_root()
    {
        _sampleExtensionRoot.Columns.Should().Contain(column => column.ColumnName.Value == "FavoriteColor");
    }

    [Test]
    public void It_should_include_tpdm_scalar_column_in_tpdm_extension_root()
    {
        _tpdmExtensionRoot
            .Columns.Should()
            .Contain(column => column.ColumnName.Value == "CertificationTitle");
    }

    [Test]
    public void It_should_include_sample_scalar_column_in_sample_extension_address()
    {
        _sampleExtensionAddress.Columns.Should().Contain(column => column.ColumnName.Value == "Complex");
    }

    [Test]
    public void It_should_use_project_specific_schemas_not_base_schema()
    {
        _sampleExtensionRoot.Table.Schema.Value.Should().Be("sample");
        _sampleExtensionAddress.Table.Schema.Value.Should().Be("sample");
        _tpdmExtensionRoot.Table.Schema.Value.Should().Be("tpdm");
    }

    [Test]
    public void It_should_not_leak_columns_across_extension_projects()
    {
        _tpdmExtensionRoot.Columns.Should().NotContain(column => column.ColumnName.Value == "FavoriteColor");
        _sampleExtensionRoot
            .Columns.Should()
            .NotContain(column => column.ColumnName.Value == "CertificationTitle");
    }
}

/// <summary>
/// Test type extension table test schema builder.
/// </summary>
internal static class ExtensionTableTestSchemaBuilder
{
    /// <summary>
    /// Build core project schema.
    /// </summary>
    internal static JsonObject BuildCoreProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["schools"] = BuildSchoolSchema(),
                ["programs"] = BuildProgramSchema(),
                ["schoolTypeDescriptors"] = BuildDescriptorSchema(),
            },
        };
    }

    /// <summary>
    /// Build extension project schema.
    /// </summary>
    internal static JsonObject BuildExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["schools"] = BuildSchoolExtensionSchema() },
        };
    }

    /// <summary>
    /// Build a simplified Sample extension project schema with scalar-only extensions.
    /// Used by the multi-project test to avoid descriptor/reference complexity.
    /// </summary>
    internal static JsonObject BuildSimpleSampleExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["schools"] = BuildSimpleSampleSchoolExtensionSchema() },
        };
    }

    /// <summary>
    /// Build a TPDM extension project schema extending School with a root-level scalar.
    /// </summary>
    internal static JsonObject BuildTpdmExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "TPDM",
            ["projectEndpointName"] = "tpdm",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["schools"] = BuildTpdmSchoolExtensionSchema() },
        };
    }

    /// <summary>
    /// Build school schema.
    /// </summary>
    private static JsonObject BuildSchoolSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolId"] = new JsonObject { ["type"] = "integer", ["format"] = "int64" },
                ["name"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["street"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                        },
                    },
                },
            },
            ["required"] = new JsonArray { "schoolId" },
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.schoolId",
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build program schema.
    /// </summary>
    private static JsonObject BuildProgramSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["programName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
            },
            ["required"] = new JsonArray { "programName" },
        };

        return new JsonObject
        {
            ["resourceName"] = "Program",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["ProgramName"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["isPartOfIdentity"] = true,
                    ["path"] = "$.programName",
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.programName" },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build descriptor schema.
    /// </summary>
    private static JsonObject BuildDescriptorSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolTypeDescriptorId"] = new JsonObject { ["type"] = "integer" },
                ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
            },
            ["required"] = new JsonArray { "schoolTypeDescriptorId" },
        };

        return new JsonObject
        {
            ["resourceName"] = "SchoolTypeDescriptor",
            ["isDescriptor"] = true,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject(),
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build school extension schema.
    /// </summary>
    private static JsonObject BuildSchoolExtensionSchema()
    {
        var extensionAddressItems = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["complex"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                                ["sponsorReferences"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["items"] = new JsonObject
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new JsonObject
                                        {
                                            ["programReference"] = new JsonObject
                                            {
                                                ["type"] = "object",
                                                ["properties"] = new JsonObject
                                                {
                                                    ["programName"] = new JsonObject
                                                    {
                                                        ["type"] = "string",
                                                        ["maxLength"] = 20,
                                                    },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                            ["required"] = new JsonArray { "complex" },
                        },
                    },
                },
            },
        };

        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["favoriteDescriptor"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["maxLength"] = 20,
                                },
                                ["addresses"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["items"] = extensionAddressItems,
                                },
                            },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = true,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["FavoriteDescriptor"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = true,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "SchoolTypeDescriptor",
                    ["path"] = "$._ext.sample.favoriteDescriptor",
                },
                ["Program"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Program",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.programName",
                            ["referenceJsonPath"] =
                                "$._ext.sample.addresses[*]._ext.sample.sponsorReferences[*].programReference.programName",
                        },
                    },
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build a simplified Sample school extension schema with scalar-only fields.
    /// Root-level favoriteColor and collection-level complex on addresses.
    /// </summary>
    private static JsonObject BuildSimpleSampleSchoolExtensionSchema()
    {
        var extensionAddressItems = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["complex"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                            },
                        },
                    },
                },
            },
        };

        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["favoriteColor"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["maxLength"] = 30,
                                },
                                ["addresses"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["items"] = extensionAddressItems,
                                },
                            },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = true,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject(),
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build a TPDM school extension schema with a root-level scalar (certificationTitle).
    /// </summary>
    private static JsonObject BuildTpdmSchoolExtensionSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["tpdm"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["certificationTitle"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["maxLength"] = 60,
                                },
                            },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = true,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject(),
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }
}
