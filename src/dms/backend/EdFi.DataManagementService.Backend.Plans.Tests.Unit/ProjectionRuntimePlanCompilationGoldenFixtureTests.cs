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
public class Given_ProjectionRuntimePlanCompilationGoldenFixture
{
    private const string FixturePath = "Fixtures/runtime-plan-compilation/projection/fixture.manifest.json";
    private const string FixtureFolderName = "projection";
    private static readonly QualifiedResourceName _projectionResource = new("Ed-Fi", "ProjectionExample");

    private string _diffOutput = null!;
    private string _manifest = null!;

    [SetUp]
    public void Setup()
    {
        var result = RuntimePlanCompilationGoldenFixtureTestHelper.BuildAndDiffManifest(
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
    public void It_should_emit_reference_and_descriptor_projection_metadata_for_projection_example()
    {
        foreach (var mappingSet in ParseMappingSetObjects(_manifest))
        {
            var readPlan = RequireProjectionReadPlan(mappingSet);
            var referenceProjectionPlans = RequireArray(
                readPlan["reference_identity_projection_plans_in_dependency_order"],
                "reference_identity_projection_plans_in_dependency_order"
            );
            var descriptorProjectionPlans = RequireArray(
                readPlan["descriptor_projection_plans_in_order"],
                "descriptor_projection_plans_in_order"
            );

            var referenceProjectionPlan =
                referenceProjectionPlans.Should().ContainSingle().Subject as JsonObject
                ?? throw new InvalidOperationException(
                    "Expected reference_identity_projection_plans_in_dependency_order entry to be an object."
                );
            var referenceBinding =
                RequireArray(referenceProjectionPlan["bindings_in_order"], "bindings_in_order")
                    .Should()
                    .ContainSingle()
                    .Subject as JsonObject
                ?? throw new InvalidOperationException("Expected bindings_in_order entry to be an object.");

            RequireBool(referenceBinding, "is_identity_component").Should().BeFalse();
            RequireString(referenceBinding, "reference_object_path").Should().Be("$.sessionTermReference");

            var targetResource = RequireObject(referenceBinding["target_resource"], "target_resource");
            RequireString(targetResource, "project_name").Should().Be("Ed-Fi");
            RequireString(targetResource, "resource_name").Should().Be("SessionTerm");
            RequireInt(referenceBinding, "fk_column_ordinal").Should().Be(1);

            var identityFieldOrdinals = RequireArray(
                referenceBinding["identity_field_ordinals_in_order"],
                "identity_field_ordinals_in_order"
            );
            identityFieldOrdinals
                .Select(static fieldNode =>
                {
                    var field = RequireObject(fieldNode, "identity_field_ordinals_in_order entry");
                    return (
                        ReferenceJsonPath: RequireString(field, "reference_json_path"),
                        ColumnOrdinal: RequireInt(field, "column_ordinal")
                    );
                })
                .Should()
                .Equal(
                    ("$.sessionTermReference.schoolId", 2),
                    ("$.sessionTermReference.schoolYear", 3),
                    ("$.sessionTermReference.sessionName", 4)
                );

            var descriptorProjectionPlan =
                descriptorProjectionPlans.Should().ContainSingle().Subject as JsonObject
                ?? throw new InvalidOperationException(
                    "Expected descriptor_projection_plans_in_order entry to be an object."
                );

            RequireString(descriptorProjectionPlan, "select_by_keyset_sql_sha256").Should().HaveLength(64);

            var resultShape = RequireObject(descriptorProjectionPlan["result_shape"], "result_shape");
            RequireInt(resultShape, "descriptor_id_ordinal").Should().Be(0);
            RequireInt(resultShape, "uri_ordinal").Should().Be(1);

            var sourcesInOrder = RequireArray(
                descriptorProjectionPlan["sources_in_order"],
                "sources_in_order"
            );

            sourcesInOrder
                .Select(static sourceNode =>
                {
                    var source = RequireObject(sourceNode, "sources_in_order entry");
                    return (
                        DescriptorValuePath: RequireString(source, "descriptor_value_path"),
                        DescriptorIdColumnOrdinal: RequireInt(source, "descriptor_id_column_ordinal")
                    );
                })
                .Should()
                .Equal(("$.primarySchoolTypeDescriptor", 5), ("$.secondarySchoolTypeDescriptor", 6));
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

    private static IReadOnlyList<JsonObject> ParseMappingSetObjects(string manifest)
    {
        var root =
            JsonNode.Parse(manifest) as JsonObject
            ?? throw new InvalidOperationException("Expected manifest root to be a JSON object.");
        var mappingSets = RequireArray(root["mapping_sets"], "mapping_sets");

        return mappingSets
            .Select(static mappingSetNode => RequireObject(mappingSetNode, "mapping_sets entry"))
            .ToArray();
    }

    private static JsonObject RequireProjectionReadPlan(JsonObject mappingSet)
    {
        var resources = RequireArray(mappingSet["resources"], "resources");
        var projectionResource = resources
            .Select(static resourceNode => RequireObject(resourceNode, "resources entry"))
            .Single(resource =>
            {
                var identity = RequireObject(resource["resource"], "resource");

                return RequireString(identity, "project_name") == _projectionResource.ProjectName
                    && RequireString(identity, "resource_name") == _projectionResource.ResourceName;
            });

        return RequireObject(projectionResource["read_plan"], "read_plan");
    }

    private static JsonArray RequireArray(JsonNode? node, string name)
    {
        return node as JsonArray
            ?? throw new InvalidOperationException($"Expected '{name}' to be a JSON array.");
    }

    private static bool RequireBool(JsonObject node, string propertyName)
    {
        return node[propertyName]?.GetValue<bool>()
            ?? throw new InvalidOperationException($"Expected '{propertyName}' to be a boolean property.");
    }

    private static int RequireInt(JsonObject node, string propertyName)
    {
        return node[propertyName]?.GetValue<int>()
            ?? throw new InvalidOperationException($"Expected '{propertyName}' to be an integer property.");
    }

    private static JsonObject RequireObject(JsonNode? node, string name)
    {
        return node as JsonObject
            ?? throw new InvalidOperationException($"Expected '{name}' to be a JSON object.");
    }

    private static string RequireString(JsonObject node, string propertyName)
    {
        return node[propertyName]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Expected '{propertyName}' to be a string property.");
    }
}
