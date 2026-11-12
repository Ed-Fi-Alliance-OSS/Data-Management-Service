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
public class Given_Generated_Api_Schema_Inputs
{
    [Test]
    public void It_removes_only_documented_openapi_paths_without_mutating_input(
        [Values(0, 1, 4)] int extensionCount,
        [Values("openApiBaseDocuments", "openApiFragments", "openApiFragment")] string changedPath
    )
    {
        Generate(extensionCount)
            .Sample(
                scenario =>
                {
                    var input = Permute(Build(scenario, includeOpenApi: true), scenario);
                    var before = Snapshot(input);
                    // Build the expected schema without OpenAPI; do not derive it by stripping input.
                    var expected = Build(scenario, includeOpenApi: false);
                    var normalized = Normalize(input);
                    JsonNode
                        .DeepEquals(normalized.CoreApiSchemaRootNode, expected.CoreApiSchemaRootNode)
                        .Should()
                        .BeTrue();
                    normalized.ExtensionApiSchemaRootNodes.Should().HaveCount(extensionCount);
                    for (var i = 0; i < extensionCount; i++)
                    {
                        JsonNode
                            .DeepEquals(
                                normalized.ExtensionApiSchemaRootNodes[i],
                                expected.ExtensionApiSchemaRootNodes[i]
                            )
                            .Should()
                            .BeTrue();
                    }
                    Snapshot(input).Should().Equal(before);
                    var provider = Provider();
                    var hash = provider.ComputeHash(normalized);
                    hash.Should().Be(ExpectedHash(expected));

                    // Change each documented path independently, in core and every extension.
                    foreach (var node in All(input))
                    {
                        var project = node["projectSchema"]!.AsObject();
                        if (changedPath == "openApiBaseDocuments")
                        {
                            project[changedPath] = new JsonArray("changed", scenario.Revision);
                        }
                        else
                        {
                            var container = project[
                                changedPath == "openApiFragments" ? "resourceSchemas" : "abstractResources"
                            ]!.AsObject();
                            foreach (var child in container)
                            {
                                child.Value![changedPath] = new JsonArray("changed", scenario.Revision);
                            }
                        }
                    }
                    var changedBefore = Snapshot(input);
                    var changed = Normalize(input);
                    provider.ComputeHash(changed).Should().Be(hash);
                    provider.ComputeHash(Normalize(expected)).Should().Be(hash); // Absent payloads are also neutral.
                    Snapshot(Normalize(normalized)).Should().Equal(Snapshot(normalized));
                    Snapshot(input).Should().Equal(changedBefore);

                    // Normalization must return detached nested containers for every project.
                    foreach (var node in All(changed))
                    {
                        node["projectSchema"]!["resourceSchemas"]!["resource0"]!["nested"]![
                            "openApiFragment"
                        ] = -1;
                    }
                    Snapshot(input).Should().Equal(changedBefore);
                },
                iter: 100,
                threads: 1,
                print: s => JsonSerializer.Serialize(s)
            );
        TestContext.Out.WriteLine(
            $"CsCheck 5.0.1: extensions={extensionCount}, changed={changedPath}: 100 samples"
        );
    }
}
