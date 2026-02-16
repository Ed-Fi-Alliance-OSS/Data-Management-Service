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
/// Test fixture for a relational model manifest emitter.
/// </summary>
[TestFixture]
public class Given_A_Relational_Model_Manifest_Emitter
{
    private RelationalModelBuildResult _buildResult = default!;
    private string _manifest = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = CreateSchema();
        var descriptorPath = JsonPathExpressionCompiler.Compile("$.schoolTypeDescriptor");
        var descriptorInfo = new DescriptorPathInfo(
            descriptorPath,
            new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor")
        );

        _buildResult = RelationalModelManifestEmitterTestContext.BuildResult(
            schema,
            context =>
            {
                context.DescriptorPathsByJsonPath = new Dictionary<string, DescriptorPathInfo>(
                    StringComparer.Ordinal
                )
                {
                    [descriptorPath.Canonical] = descriptorInfo,
                };
                context.IdentityJsonPaths = new[] { descriptorPath };
            }
        );

        _manifest = RelationalModelManifestEmitter.Emit(_buildResult);
    }

    /// <summary>
    /// It should emit byte for byte identical output.
    /// </summary>
    [Test]
    public void It_should_emit_byte_for_byte_identical_output()
    {
        var second = RelationalModelManifestEmitter.Emit(_buildResult);

        _manifest.Should().Be(second);
        _manifest.Should().NotContain("\r");
    }

    /// <summary>
    /// It should include expected inventory.
    /// </summary>
    [Test]
    public void It_should_include_expected_inventory()
    {
        var root =
            JsonNode.Parse(_manifest) as JsonObject
            ?? throw new InvalidOperationException("Expected manifest to be a JSON object.");

        var resource =
            root["resource"] as JsonObject
            ?? throw new InvalidOperationException("Expected resource to be a JSON object.");

        resource["project_name"]!.GetValue<string>().Should().Be("Ed-Fi");
        resource["resource_name"]!.GetValue<string>().Should().Be("School");
        root["physical_schema"]!.GetValue<string>().Should().Be("edfi");
        root["storage_kind"]!.GetValue<string>().Should().Be("RelationalTables");

        var tables =
            root["tables"] as JsonArray
            ?? throw new InvalidOperationException("Expected tables to be a JSON array.");

        tables.Count.Should().Be(2);
        var tableNames = tables
            .Select(table => table?["name"]?.GetValue<string>())
            .Where(name => name is not null)
            .ToArray();
        tableNames.Should().Equal("School", "SchoolAddress");

        var descriptorEdges =
            root["descriptor_edge_sources"] as JsonArray
            ?? throw new InvalidOperationException("Expected descriptor edges to be a JSON array.");
        descriptorEdges.Count.Should().Be(1);

        var extensionSites =
            root["extension_sites"] as JsonArray
            ?? throw new InvalidOperationException("Expected extension sites to be a JSON array.");
        extensionSites.Count.Should().Be(2);
    }

    /// <summary>
    /// It should emit descriptor storage kind.
    /// </summary>
    [Test]
    public void It_should_emit_descriptor_storage_kind()
    {
        var descriptorSchema = CreateDescriptorSchema();
        var buildResult = RelationalModelManifestEmitterTestContext.BuildResult(
            descriptorSchema,
            context =>
            {
                context.ResourceName = "AcademicSubjectDescriptor";
                context.IsDescriptorResource = true;
            }
        );

        var manifest = RelationalModelManifestEmitter.Emit(buildResult);

        var root =
            JsonNode.Parse(manifest) as JsonObject
            ?? throw new InvalidOperationException("Expected manifest to be a JSON object.");

        root["storage_kind"]!.GetValue<string>().Should().Be("SharedDescriptorTable");

        var tables =
            root["tables"] as JsonArray
            ?? throw new InvalidOperationException("Expected tables to be a JSON array.");

        tables.Should().BeEmpty("descriptor resources do not create per-resource tables");
    }

    /// <summary>
    /// It should emit key columns before non key columns.
    /// </summary>
    [Test]
    public void It_should_emit_key_columns_before_non_key_columns()
    {
        var root =
            JsonNode.Parse(_manifest) as JsonObject
            ?? throw new InvalidOperationException("Expected manifest to be a JSON object.");

        var tables =
            root["tables"] as JsonArray
            ?? throw new InvalidOperationException("Expected tables to be a JSON array.");

        var rootColumns = GetColumnNames(tables, "School");
        rootColumns.Should().StartWith("DocumentId");

        var addressColumns = GetColumnNames(tables, "SchoolAddress");
        addressColumns.Should().StartWith("School_DocumentId", "Ordinal");
    }

    /// <summary>
    /// It should emit default key-unification metadata.
    /// </summary>
    [Test]
    public void It_should_emit_default_key_unification_metadata()
    {
        var root =
            JsonNode.Parse(_manifest) as JsonObject
            ?? throw new InvalidOperationException("Expected manifest to be a JSON object.");

        var tables =
            root["tables"] as JsonArray
            ?? throw new InvalidOperationException("Expected tables to be a JSON array.");

        foreach (
            var table in tables
                .Select(tableNode => tableNode as JsonObject)
                .Where(tableNode => tableNode is not null)
        )
        {
            var keyUnificationClasses =
                table!["key_unification_classes"] as JsonArray
                ?? throw new InvalidOperationException(
                    "Expected key_unification_classes to be a JSON array."
                );

            keyUnificationClasses.Should().BeEmpty();

            var columns =
                table["columns"] as JsonArray
                ?? throw new InvalidOperationException("Expected columns to be a JSON array.");

            foreach (
                var column in columns
                    .Select(columnNode => columnNode as JsonObject)
                    .Where(columnNode => columnNode is not null)
            )
            {
                var storage =
                    column!["storage"] as JsonObject
                    ?? throw new InvalidOperationException("Expected storage to be a JSON object.");

                storage["kind"]!.GetValue<string>().Should().Be("Stored");
            }
        }
    }

    /// <summary>
    /// It should emit all or none nullability constraints.
    /// </summary>
    [Test]
    public void It_should_emit_all_or_none_nullability_constraints()
    {
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "School");
        var jsonScope = JsonPathExpressionCompiler.Compile("$");
        var keyColumn = new DbKeyColumn(
            RelationalNameConventions.DocumentIdColumnName,
            ColumnKind.ParentKeyPart
        );

        var fkColumn = new DbColumnName("Student_DocumentId");
        var dependentColumns = new[]
        {
            new DbColumnName("Student_StudentUniqueId"),
            new DbColumnName("Student_SchoolId"),
        };

        var columns = new[]
        {
            new DbColumnModel(
                RelationalNameConventions.DocumentIdColumnName,
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new DbColumnModel(
                fkColumn,
                ColumnKind.DocumentFk,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.studentReference"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "Student")
            ),
            new DbColumnModel(
                dependentColumns[0],
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String, MaxLength: 32),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.studentReference.studentUniqueId"),
                TargetResource: null
            ),
            new DbColumnModel(
                dependentColumns[1],
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.studentReference.schoolId"),
                TargetResource: null
            ),
        };

        var constraints = new TableConstraint[]
        {
            new TableConstraint.AllOrNoneNullability(
                "CK_School_Student_DocumentId_AllOrNone",
                fkColumn,
                dependentColumns
            ),
        };

        var table = new DbTableModel(
            tableName,
            jsonScope,
            new TableKey($"PK_{tableName.Name}", [keyColumn]),
            columns,
            constraints
        );

        var resourceModel = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "School"),
            schema,
            ResourceStorageKind.RelationalTables,
            table,
            new[] { table },
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
        );

        var manifest = RelationalModelManifestEmitter.Emit(resourceModel, Array.Empty<ExtensionSite>());

        var root =
            JsonNode.Parse(manifest) as JsonObject
            ?? throw new InvalidOperationException("Expected manifest to be a JSON object.");

        var tables =
            root["tables"] as JsonArray
            ?? throw new InvalidOperationException("Expected tables to be a JSON array.");

        var tableNode =
            tables.Single() as JsonObject
            ?? throw new InvalidOperationException("Expected table to be a JSON object.");

        var constraintNodes =
            tableNode["constraints"] as JsonArray
            ?? throw new InvalidOperationException("Expected constraints to be a JSON array.");

        var constraint =
            constraintNodes.Single() as JsonObject
            ?? throw new InvalidOperationException("Expected constraint to be a JSON object.");

        constraint["kind"]!.GetValue<string>().Should().Be("AllOrNoneNullability");
        constraint["name"]!.GetValue<string>().Should().Be("CK_School_Student_DocumentId_AllOrNone");
        constraint["fk_column"]!.GetValue<string>().Should().Be("Student_DocumentId");

        var dependentColumnNodes =
            constraint["dependent_columns"] as JsonArray
            ?? throw new InvalidOperationException("Expected dependent_columns to be a JSON array.");

        dependentColumnNodes
            .Select(column => column?.GetValue<string>())
            .Should()
            .Equal("Student_StudentUniqueId", "Student_SchoolId");
    }

    /// <summary>
    /// It should emit null-or-true constraints.
    /// </summary>
    [Test]
    public void It_should_emit_null_or_true_constraints()
    {
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "School");
        var jsonScope = JsonPathExpressionCompiler.Compile("$");
        var keyColumn = new DbKeyColumn(
            RelationalNameConventions.DocumentIdColumnName,
            ColumnKind.ParentKeyPart
        );
        var presenceColumn = new DbColumnName("FiscalYear_Present");

        var columns = new[]
        {
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
        };

        var constraints = new TableConstraint[]
        {
            new TableConstraint.NullOrTrue("CK_School_FiscalYear_Present_NullOrTrue", presenceColumn),
        };

        var table = new DbTableModel(
            tableName,
            jsonScope,
            new TableKey($"PK_{tableName.Name}", [keyColumn]),
            columns,
            constraints
        );

        var resourceModel = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "School"),
            schema,
            ResourceStorageKind.RelationalTables,
            table,
            new[] { table },
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
        );

        var manifest = RelationalModelManifestEmitter.Emit(resourceModel, Array.Empty<ExtensionSite>());

        var root =
            JsonNode.Parse(manifest) as JsonObject
            ?? throw new InvalidOperationException("Expected manifest to be a JSON object.");

        var tables =
            root["tables"] as JsonArray
            ?? throw new InvalidOperationException("Expected tables to be a JSON array.");

        var tableNode =
            tables.Single() as JsonObject
            ?? throw new InvalidOperationException("Expected table to be a JSON object.");

        var constraintNodes =
            tableNode["constraints"] as JsonArray
            ?? throw new InvalidOperationException("Expected constraints to be a JSON array.");

        var constraint =
            constraintNodes.Single() as JsonObject
            ?? throw new InvalidOperationException("Expected constraint to be a JSON object.");

        constraint["kind"]!.GetValue<string>().Should().Be("NullOrTrue");
        constraint["name"]!.GetValue<string>().Should().Be("CK_School_FiscalYear_Present_NullOrTrue");
        constraint["column"]!.GetValue<string>().Should().Be("FiscalYear_Present");
    }

    /// <summary>
    /// Create schema.
    /// </summary>
    private static JsonObject CreateSchema()
    {
        var extensionSchema = CreateExtensionSchema();
        var addressExtensionSchema = CreateExtensionSchema();

        var addressItemProperties = new JsonObject
        {
            ["streetNumberName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
            ["_ext"] = addressExtensionSchema,
        };

        var addressesSchema = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "object", ["properties"] = addressItemProperties },
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolTypeDescriptor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 306 },
                ["addresses"] = addressesSchema,
                ["_ext"] = extensionSchema,
            },
            ["required"] = new JsonArray("schoolTypeDescriptor"),
        };
    }

    /// <summary>
    /// Create descriptor schema.
    /// </summary>
    private static JsonObject CreateDescriptorSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["namespace"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                ["shortDescription"] = new JsonObject { ["type"] = "string", ["maxLength"] = 75 },
            },
            ["required"] = new JsonArray("namespace", "codeValue", "shortDescription"),
        };
    }

    /// <summary>
    /// Create extension schema.
    /// </summary>
    private static JsonObject CreateExtensionSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["tpdm"] = new JsonObject() },
        };
    }

    /// <summary>
    /// Get column names.
    /// </summary>
    private static IReadOnlyList<string> GetColumnNames(JsonArray tables, string tableName)
    {
        var table =
            tables
                .Select(tableNode => tableNode as JsonObject)
                .Single(tableNode =>
                    string.Equals(tableNode?["name"]?.GetValue<string>(), tableName, StringComparison.Ordinal)
                )
            ?? throw new InvalidOperationException($"Expected table '{tableName}'.");

        var columns =
            table["columns"] as JsonArray
            ?? throw new InvalidOperationException($"Expected columns array on {tableName}.");

        return columns
            .Select(columnNode => columnNode?["name"]?.GetValue<string>())
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();
    }
}

/// <summary>
/// Test fixture for schemas with and without additional properties.
/// </summary>
[TestFixture]
public class Given_Schemas_With_And_Without_AdditionalProperties
{
    private string _manifestWithAdditionalProperties = default!;
    private string _manifestWithoutAdditionalProperties = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schemaWithoutAdditionalProperties = CreateSchema(includeAdditionalProperties: false);
        var schemaWithAdditionalProperties = CreateSchema(includeAdditionalProperties: true);

        _manifestWithoutAdditionalProperties = RelationalModelManifestEmitter.Emit(
            RelationalModelManifestEmitterTestContext.BuildResult(schemaWithoutAdditionalProperties)
        );
        _manifestWithAdditionalProperties = RelationalModelManifestEmitter.Emit(
            RelationalModelManifestEmitterTestContext.BuildResult(schemaWithAdditionalProperties)
        );
    }

    /// <summary>
    /// It should emit identical manifests.
    /// </summary>
    [Test]
    public void It_should_emit_identical_manifests()
    {
        _manifestWithAdditionalProperties.Should().Be(_manifestWithoutAdditionalProperties);
    }

    /// <summary>
    /// Create schema.
    /// </summary>
    private static JsonObject CreateSchema(bool includeAdditionalProperties)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string", ["maxLength"] = 100 },
                ["periods"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["code"] = new JsonObject { ["type"] = "string", ["maxLength"] = 5 },
                        },
                    },
                },
            },
        };

        if (includeAdditionalProperties)
        {
            schema["additionalProperties"] = JsonValue.Create(true);
        }

        return schema;
    }
}

/// <summary>
/// Test type relational model manifest emitter test context.
/// </summary>
internal static class RelationalModelManifestEmitterTestContext
{
    /// <summary>
    /// Build result.
    /// </summary>
    public static RelationalModelBuildResult BuildResult(
        JsonObject schema,
        Action<RelationalModelBuilderContext>? configure = null
    )
    {
        var context = new RelationalModelBuilderContext
        {
            ProjectName = "Ed-Fi",
            ProjectEndpointName = "ed-fi",
            ResourceName = "School",
            JsonSchemaForInsert = schema,
        };

        configure?.Invoke(context);

        var deriveTables = new DeriveTableScopesAndKeysStep();
        deriveTables.Execute(context);

        var discoverExtensions = new DiscoverExtensionSitesStep();
        discoverExtensions.Execute(context);

        var deriveColumns = new DeriveColumnsAndBindDescriptorEdgesStep();
        deriveColumns.Execute(context);

        var canonicalize = new CanonicalizeOrderingStep();
        canonicalize.Execute(context);

        return context.BuildResult();
    }
}
