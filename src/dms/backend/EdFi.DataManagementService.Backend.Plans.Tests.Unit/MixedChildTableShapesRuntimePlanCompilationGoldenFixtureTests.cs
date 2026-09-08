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
public class Given_MixedChildTableShapes_RuntimePlanCompilation_GoldenFixture
{
    private const string FixturePath =
        "Fixtures/runtime-plan-compilation/mixed-child-table-shapes/fixture.manifest.json";
    private const string FixtureFolderName = "mixed-child-table-shapes";

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
    public void It_should_emit_collection_and_non_collection_child_table_contracts_for_school()
    {
        var root = JsonNode.Parse(_manifest) as JsonObject;
        root.Should().NotBeNull();

        var mappingSets = root!["mapping_sets"] as JsonArray;
        mappingSets.Should().NotBeNull();
        mappingSets!.Count.Should().Be(2);

        foreach (var mappingSetNode in mappingSets)
        {
            var mappingSet = mappingSetNode as JsonObject;
            mappingSet.Should().NotBeNull();

            var resources = mappingSet!["resources"] as JsonArray;
            resources.Should().NotBeNull();

            var schoolResource = resources!
                .Select(resourceNode =>
                    resourceNode as JsonObject
                    ?? throw new InvalidOperationException(
                        "Expected manifest resources entries to be JSON objects."
                    )
                )
                .Single(resource =>
                {
                    var identity =
                        resource["resource"] as JsonObject
                        ?? throw new InvalidOperationException(
                            "Expected manifest resource identity to be a JSON object."
                        );

                    return identity["project_name"]?.GetValue<string>() == "Ed-Fi"
                        && identity["resource_name"]?.GetValue<string>() == "School";
                });

            var writePlan = schoolResource["write_plan"] as JsonObject;
            writePlan.Should().NotBeNull();

            var tablePlans = writePlan!["table_plans_in_dependency_order"] as JsonArray;
            tablePlans.Should().NotBeNull();
            tablePlans!.Count.Should().Be(3);

            var rootTable = tablePlans[0] as JsonObject;
            rootTable.Should().NotBeNull();
            rootTable!["delete_by_parent_sql_sha256"].Should().BeNull();
            rootTable["collection_merge_plan"].Should().BeNull();

            var childTablePlans = tablePlans
                .Skip(1)
                .Select(tablePlanNode =>
                    tablePlanNode as JsonObject
                    ?? throw new InvalidOperationException(
                        "Expected manifest table_plans_in_dependency_order entries to be JSON objects."
                    )
                )
                .ToArray();

            childTablePlans.Should().HaveCount(2);

            var collectionChildTablePlans = childTablePlans
                .Where(tablePlan => tablePlan["collection_merge_plan"] is JsonObject)
                .ToArray();
            var nonCollectionChildTablePlans = childTablePlans
                .Where(tablePlan => tablePlan["collection_merge_plan"] is null)
                .ToArray();

            collectionChildTablePlans.Should().ContainSingle();
            nonCollectionChildTablePlans.Should().ContainSingle();

            var collectionTablePlan = collectionChildTablePlans[0];
            var collectionTable = collectionTablePlan["table"] as JsonObject;
            collectionTable.Should().NotBeNull();
            collectionTable!["schema"]!.GetValue<string>().Should().Be("edfi");
            collectionTable["name"]!.GetValue<string>().Should().Be("SchoolAddress");
            collectionTablePlan["update_sql_sha256"].Should().BeNull();
            collectionTablePlan["delete_by_parent_sql_sha256"].Should().BeNull();

            var collectionMergePlan = collectionTablePlan["collection_merge_plan"] as JsonObject;
            collectionMergePlan.Should().NotBeNull();
            collectionMergePlan!["update_by_stable_row_identity_sql_sha256"]!
                .GetValue<string>()
                .Should()
                .NotBeNullOrWhiteSpace();
            collectionMergePlan["delete_by_stable_row_identity_sql_sha256"]!
                .GetValue<string>()
                .Should()
                .NotBeNullOrWhiteSpace();

            var compareBindingIndexes = collectionMergePlan["compare_binding_indexes_in_order"] as JsonArray;
            compareBindingIndexes.Should().NotBeNull();
            compareBindingIndexes!.Count.Should().BeGreaterThan(0);

            var nonCollectionTablePlan = nonCollectionChildTablePlans[0];
            var nonCollectionTable = nonCollectionTablePlan["table"] as JsonObject;
            nonCollectionTable.Should().NotBeNull();
            nonCollectionTable!["schema"]!.GetValue<string>().Should().Be("sample");
            nonCollectionTable["name"]!.GetValue<string>().Should().Be("SchoolExtension");
            nonCollectionTablePlan["collection_merge_plan"].Should().BeNull();
            nonCollectionTablePlan["update_sql_sha256"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            nonCollectionTablePlan["delete_by_parent_sql_sha256"]!
                .GetValue<string>()
                .Should()
                .NotBeNullOrWhiteSpace();
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
}
