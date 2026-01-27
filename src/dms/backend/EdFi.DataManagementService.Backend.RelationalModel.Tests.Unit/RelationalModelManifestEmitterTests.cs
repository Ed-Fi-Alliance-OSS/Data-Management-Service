// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_Relational_Model_Manifest_Emitter
{
    private RelationalModelBuildResult _buildResult = default!;
    private string _manifest = default!;

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

    [Test]
    public void It_should_emit_byte_for_byte_identical_output()
    {
        var second = RelationalModelManifestEmitter.Emit(_buildResult);

        _manifest.Should().Be(second);
        _manifest.Should().NotContain("\r");
    }

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

    [Test]
    public void It_should_emit_descriptor_storage_kind()
    {
        var descriptorSchema = CreateSchema();
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
    }

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
        };
    }

    private static JsonObject CreateExtensionSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["tpdm"] = new JsonObject() },
        };
    }

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

[TestFixture]
public class Given_Schemas_With_And_Without_AdditionalProperties
{
    private string _manifestWithAdditionalProperties = default!;
    private string _manifestWithoutAdditionalProperties = default!;

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

    [Test]
    public void It_should_emit_identical_manifests()
    {
        _manifestWithAdditionalProperties.Should().Be(_manifestWithoutAdditionalProperties);
    }

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

internal static class RelationalModelManifestEmitterTestContext
{
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

        var deriveColumns = new DeriveColumnsAndDescriptorEdgesStep();
        deriveColumns.Execute(context);

        var canonicalize = new CanonicalizeOrderingStep();
        canonicalize.Execute(context);

        return context.BuildResult();
    }
}
