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
public class Given_CollectionsNestedExtension_RuntimePlanCompilation_GoldenFixture
{
    private const string FixturePath =
        "Fixtures/runtime-plan-compilation/collections-nested-extension/fixture.manifest.json";
    private const string FixtureFolderName = "collections-nested-extension";

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
    public void It_should_emit_write_and_read_plan_inventory_for_school()
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

            schoolResource["write_plan"].Should().NotBeNull();

            var writePlan = schoolResource["write_plan"] as JsonObject;
            writePlan.Should().NotBeNull();

            var tablePlans = writePlan!["table_plans_in_dependency_order"] as JsonArray;
            tablePlans.Should().NotBeNull();
            tablePlans!.Count.Should().BeGreaterThan(1);

            var rootTable = tablePlans[0] as JsonObject;
            rootTable.Should().NotBeNull();
            rootTable!["delete_by_parent_sql_sha256"].Should().BeNull();
            rootTable["collection_merge_plan"].Should().BeNull();

            var collectionChildTablePlans = 0;
            var nonCollectionChildTablePlans = 0;

            foreach (var tablePlanNode in tablePlans.Skip(1))
            {
                var tablePlan = tablePlanNode as JsonObject;
                tablePlan.Should().NotBeNull();

                var collectionMergePlan = tablePlan!["collection_merge_plan"] as JsonObject;

                if (collectionMergePlan is not null)
                {
                    collectionChildTablePlans++;
                    tablePlan["delete_by_parent_sql_sha256"].Should().BeNull();
                    tablePlan["update_sql_sha256"].Should().BeNull();
                    collectionMergePlan["update_by_stable_row_identity_sql_sha256"]!
                        .GetValue<string>()
                        .Should()
                        .NotBeNullOrWhiteSpace();
                    collectionMergePlan["delete_by_stable_row_identity_sql_sha256"]!
                        .GetValue<string>()
                        .Should()
                        .NotBeNullOrWhiteSpace();

                    var compareBindingIndexes =
                        collectionMergePlan["compare_binding_indexes_in_order"] as JsonArray;
                    compareBindingIndexes.Should().NotBeNull();
                    compareBindingIndexes!.Count.Should().BeGreaterThan(0);
                }
                else
                {
                    nonCollectionChildTablePlans++;
                    var deleteByParentHashNode = tablePlan["delete_by_parent_sql_sha256"];
                    deleteByParentHashNode.Should().NotBeNull();
                    deleteByParentHashNode!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
                }

                var batching = tablePlan["bulk_insert_batching"] as JsonObject;
                batching.Should().NotBeNull();

                batching!["max_rows_per_batch"]!.GetValue<int>().Should().BeGreaterThan(0);
                batching["parameters_per_row"]!.GetValue<int>().Should().BeGreaterThan(0);
                batching["max_parameters_per_command"]!.GetValue<int>().Should().BeGreaterThan(0);
            }

            collectionChildTablePlans.Should().BeGreaterThan(0);
            nonCollectionChildTablePlans.Should().BeGreaterThan(0);

            var readPlan = schoolResource["read_plan"] as JsonObject;
            readPlan.Should().NotBeNull();

            var readTablePlans = readPlan!["table_plans_in_dependency_order"] as JsonArray;
            readTablePlans.Should().NotBeNull();
            readTablePlans!.Count.Should().Be(tablePlans.Count);
            readTablePlans
                .Select(static tablePlanNode =>
                {
                    var tablePlan = tablePlanNode as JsonObject;
                    tablePlan.Should().NotBeNull();

                    var table = tablePlan!["table"] as JsonObject;
                    table.Should().NotBeNull();

                    return $"{table!["schema"]!.GetValue<string>()}.{table["name"]!.GetValue<string>()}";
                })
                .Should()
                .Equal(
                    tablePlans.Select(tablePlanNode =>
                    {
                        var tablePlan = tablePlanNode as JsonObject;
                        tablePlan.Should().NotBeNull();

                        var table = tablePlan!["table"] as JsonObject;
                        table.Should().NotBeNull();

                        return $"{table!["schema"]!.GetValue<string>()}.{table["name"]!.GetValue<string>()}";
                    })
                );

            var referenceIdentityProjectionPlans =
                readPlan["reference_identity_projection_plans_in_dependency_order"] as JsonArray;
            referenceIdentityProjectionPlans.Should().NotBeNull();
            referenceIdentityProjectionPlans.Should().BeEmpty();

            var descriptorProjectionPlans = readPlan["descriptor_projection_plans_in_order"] as JsonArray;
            descriptorProjectionPlans.Should().NotBeNull();
            descriptorProjectionPlans.Should().BeEmpty();
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
