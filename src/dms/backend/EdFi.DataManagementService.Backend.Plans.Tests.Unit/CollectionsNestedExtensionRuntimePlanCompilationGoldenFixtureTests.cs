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
    public void It_should_emit_write_plan_inventory_and_explicit_null_read_plan_for_school()
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
            schoolResource["read_plan"].Should().BeNull();

            var writePlan = schoolResource["write_plan"] as JsonObject;
            writePlan.Should().NotBeNull();

            var tablePlans = writePlan!["table_plans_in_dependency_order"] as JsonArray;
            tablePlans.Should().NotBeNull();
            tablePlans!.Count.Should().BeGreaterThan(1);

            var rootTable = tablePlans[0] as JsonObject;
            rootTable.Should().NotBeNull();
            rootTable!["delete_by_parent_sql_sha256"].Should().BeNull();

            foreach (var tablePlanNode in tablePlans.Skip(1))
            {
                var tablePlan = tablePlanNode as JsonObject;
                tablePlan.Should().NotBeNull();

                var deleteByParentHashNode = tablePlan!["delete_by_parent_sql_sha256"];
                deleteByParentHashNode.Should().NotBeNull();
                deleteByParentHashNode!.GetValue<string>().Should().NotBeNullOrWhiteSpace();

                var batching = tablePlan["bulk_insert_batching"] as JsonObject;
                batching.Should().NotBeNull();

                batching!["max_rows_per_batch"]!.GetValue<int>().Should().BeGreaterThan(0);
                batching["parameters_per_row"]!.GetValue<int>().Should().BeGreaterThan(0);
                batching["max_parameters_per_command"]!.GetValue<int>().Should().BeGreaterThan(0);
            }
        }
    }

    private static string BuildManifest()
    {
        var compiler = new MappingSetCompiler();
        var mappingSets = new[]
        {
            compiler.Compile(ThinSliceFixtureModelSetBuilder.Build(FixturePath, SqlDialect.Pgsql)),
            compiler.Compile(ThinSliceFixtureModelSetBuilder.Build(FixturePath, SqlDialect.Mssql)),
        };

        return ThinSliceMappingSetManifestJsonEmitter.Emit(mappingSets);
    }
}
