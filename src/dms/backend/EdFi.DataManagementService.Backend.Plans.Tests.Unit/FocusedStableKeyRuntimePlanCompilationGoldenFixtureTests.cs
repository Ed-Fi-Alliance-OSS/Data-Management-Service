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
public class Given_FocusedStableKeyExtensionChildCollections_RuntimePlanCompilation_GoldenFixture
{
    private const string FixturePath =
        "Fixtures/runtime-plan-compilation/focused-stable-key/positive/extension-child-collections/fixture.manifest.json";
    private const string FixtureRootFolderName = "runtime-plan-compilation/focused-stable-key/positive";
    private const string FixtureFolderName = "extension-child-collections";

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
    public void It_should_emit_school_write_and_read_plans_for_both_dialects()
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
            schoolResource["read_plan"].Should().NotBeNull();
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
