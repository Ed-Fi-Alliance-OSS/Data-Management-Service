// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_AuthoritativeDs52_RuntimePlanCompilation_GoldenFixture
{
    private const string FixturePath =
        "../Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json";
    private const string FixtureRootFolderName = "authoritative";
    private const string FixtureFolderName = "ds-5.2";

    private string _diffOutput = null!;
    private string _manifest = null!;

    [SetUp]
    public void Setup()
    {
        var result = RuntimePlanCompilationGoldenFixtureTestHelper.BuildAndDiffManifest(
            FixtureRootFolderName,
            FixtureFolderName,
            BuildManifest
        );

        _manifest = result.Manifest;
        _diffOutput = result.DiffOutput;
    }

    [Test]
    public void It_should_match_the_expected_manifest()
    {
        if (!string.IsNullOrWhiteSpace(_diffOutput))
        {
            Assert.Fail(_diffOutput);
        }
    }

    [Test]
    public void It_should_emit_delete_by_parent_hashes_for_non_root_table_plans()
    {
        var childTablePlans = ParseMappingSets(_manifest)
            .SelectMany(ReadResources)
            .Select(ReadOptionalWritePlan)
            .Where(writePlan => writePlan is not null)
            .SelectMany(writePlan => ReadTablePlans(writePlan!).Skip(1))
            .ToArray();

        childTablePlans.Should().NotBeEmpty();

        foreach (var childTablePlan in childTablePlans)
        {
            ReadRequiredString(childTablePlan, "delete_by_parent_sql_sha256");
        }
    }

    [Test]
    public void It_should_emit_null_write_plan_for_descriptor_resources()
    {
        foreach (var mappingSet in ParseMappingSets(_manifest))
        {
            var descriptorResources = ReadResources(mappingSet).Where(IsDescriptorResource).ToArray();

            descriptorResources.Should().NotBeEmpty();

            foreach (var descriptorResource in descriptorResources)
            {
                descriptorResource["write_plan"].Should().BeNull();
            }
        }
    }

    private static string BuildManifest()
    {
        var compiler = new MappingSetCompiler();
        var mappingSets = new[]
        {
            compiler.Compile(RuntimePlanFixtureModelSetBuilder.Build(FixturePath, SqlDialect.Pgsql)),
            compiler.Compile(RuntimePlanFixtureModelSetBuilder.Build(FixturePath, SqlDialect.Mssql)),
        };

        return MappingSetManifestJsonEmitter.Emit(mappingSets);
    }

    private static IReadOnlyList<JsonObject> ParseMappingSets(string manifest)
    {
        var rootNode = JsonNode.Parse(manifest);

        if (rootNode is not JsonObject rootObject)
        {
            throw new InvalidOperationException("Manifest root must be a JSON object.");
        }

        var mappingSets = ReadRequiredArray(rootObject["mapping_sets"], "mapping_sets");

        return mappingSets
            .Select(mappingSetNode => ReadRequiredObject(mappingSetNode, "mapping_sets entry"))
            .ToArray();
    }

    private static IReadOnlyList<JsonObject> ReadResources(JsonObject mappingSet)
    {
        var resources = ReadRequiredArray(mappingSet["resources"], "resources");

        return resources
            .Select(resourceNode => ReadRequiredObject(resourceNode, "resources entry"))
            .ToArray();
    }

    private static JsonObject? ReadOptionalWritePlan(JsonObject resourceEntry)
    {
        return resourceEntry["write_plan"] switch
        {
            JsonObject writePlan => writePlan,
            null => null,
            _ => throw new InvalidOperationException(
                "Manifest property 'write_plan' must be an object or null."
            ),
        };
    }

    private static IReadOnlyList<JsonObject> ReadTablePlans(JsonObject writePlan)
    {
        var tablePlans = ReadRequiredArray(
            writePlan["table_plans_in_dependency_order"],
            "table_plans_in_dependency_order"
        );

        return tablePlans
            .Select(tablePlanNode =>
                ReadRequiredObject(tablePlanNode, "table_plans_in_dependency_order entry")
            )
            .ToArray();
    }

    private static bool IsDescriptorResource(JsonObject resourceEntry)
    {
        var resourceIdentity = ReadRequiredObject(resourceEntry["resource"], "resource");
        var resourceName = ReadRequiredString(resourceIdentity, "resource_name");

        return resourceName.EndsWith("Descriptor", StringComparison.Ordinal);
    }

    private static JsonObject ReadRequiredObject(JsonNode? node, string propertyName)
    {
        return node switch
        {
            JsonObject jsonObject => jsonObject,
            null => throw new InvalidOperationException($"Manifest property '{propertyName}' is required."),
            _ => throw new InvalidOperationException(
                $"Manifest property '{propertyName}' must be an object."
            ),
        };
    }

    private static JsonArray ReadRequiredArray(JsonNode? node, string propertyName)
    {
        return node switch
        {
            JsonArray jsonArray => jsonArray,
            null => throw new InvalidOperationException($"Manifest property '{propertyName}' is required."),
            _ => throw new InvalidOperationException(
                $"Manifest property '{propertyName}' must be a JSON array."
            ),
        };
    }

    private static string ReadRequiredString(JsonObject node, string propertyName)
    {
        var value = node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            null => throw new InvalidOperationException($"Manifest property '{propertyName}' is required."),
            _ => throw new InvalidOperationException($"Manifest property '{propertyName}' must be a string."),
        };

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Manifest property '{propertyName}' must be non-empty.");
        }

        return value;
    }
}
