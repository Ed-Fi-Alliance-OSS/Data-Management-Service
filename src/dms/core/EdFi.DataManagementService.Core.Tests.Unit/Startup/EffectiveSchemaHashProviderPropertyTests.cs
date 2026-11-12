// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Startup.SchemaHashPropertyScenarios;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
[Category("SchemaHashProperty")]
public class Given_Generated_Effective_Schemas
{
    [Test]
    public void It_matches_the_manifest_oracle_despite_recursive_property_and_extension_reordering(
        [Values(0, 1, 4)] int extensionCount
    )
    {
        Generate(extensionCount)
            .Sample(
                scenario =>
                {
                    var nodes = Build(scenario, includeOpenApi: false);
                    var reordered = Permute(nodes, scenario);
                    var before = Snapshot(reordered);
                    var expected = ExpectedHash(nodes);
                    var provider = Provider();
                    provider.ComputeHash(nodes).Should().Be(expected);
                    // Exercise the provider directly, so normalizer sorting cannot conceal a provider regression.
                    provider.ComputeHash(reordered).Should().Be(expected);
                    var reversed = reordered with
                    {
                        ExtensionApiSchemaRootNodes = reordered
                            .ExtensionApiSchemaRootNodes.Reverse()
                            .ToArray(),
                    };
                    provider.ComputeHash(reversed).Should().Be(expected);
                    provider.ComputeHash(Normalize(reordered)).Should().Be(expected);
                    Snapshot(reordered).Should().Equal(before);
                },
                iter: 100,
                threads: 1,
                print: s => JsonSerializer.Serialize(s)
            );
        TestContext.Out.WriteLine($"CsCheck 5.0.1: extensions={extensionCount}: 100 samples");
    }

    [Test]
    public void It_includes_ordered_arrays_numeric_spellings_and_non_openapi_content_in_the_manifest(
        [Values(false, true)] bool changeExtension,
        [Values("array-order", "numeric-spelling", "nested-openapi-name", "project-version")] string change
    )
    {
        Generate(4)
            .Sample(
                scenario =>
                {
                    var nodes = Build(scenario, includeOpenApi: false);
                    var target = changeExtension
                        ? nodes.ExtensionApiSchemaRootNodes[3]
                        : nodes.CoreApiSchemaRootNode;
                    var project = target["projectSchema"]!;
                    project["ordered"] = new JsonArray(scenario.Revision, 0);
                    project["number"] = JsonNode.Parse("1.00");
                    var provider = Provider();
                    provider.ComputeHash(nodes).Should().Be(ExpectedHash(nodes));
                    switch (change)
                    {
                        case "array-order":
                            project["ordered"] = new JsonArray(0, scenario.Revision);
                            break;
                        case "numeric-spelling":
                            project["number"] = JsonNode.Parse("1e0");
                            break;
                        case "nested-openapi-name":
                            project["resourceSchemas"]!["resource0"]!["nested"]!["openApiFragments"] = -1;
                            break;
                        case "project-version":
                            project["projectVersion"] = $"2.{scenario.Revision}.0";
                            break;
                    }
                    // Exact SHA-256 oracle rather than a universal collision-freedom assumption.
                    var expected = ExpectedHash(nodes);
                    provider.ComputeHash(nodes).Should().Be(expected);
                    provider.ComputeHash(Normalize(nodes)).Should().Be(expected);
                },
                iter: 100,
                threads: 1,
                print: s => JsonSerializer.Serialize(s)
            );
        TestContext.Out.WriteLine(
            $"CsCheck 5.0.1: extension={changeExtension}, change={change}: 100 samples"
        );
    }
}
