// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CsCheck;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Tests.Unit.Utilities;
using EdFi.DataManagementService.Core.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

internal static class SchemaHashPropertyScenarios
{
    internal sealed record Scenario(
        string Payload,
        string Documentation,
        int Revision,
        int ResourceCount,
        ImmutableArray<int> Priorities
    );

    internal static Gen<Scenario> Generate(int extensionCount) =>
        from payload in CanonicalJsonPropertyScenarios.Generate("nested")
        from documentation in CanonicalJsonPropertyScenarios.Text
        from revision in Gen.Int[1, 10000]
        from resourceCount in Gen.Int[1, 3]
        from priorities in Gen.Int[0, 100].Array[extensionCount]
        select new Scenario(payload, documentation, revision, resourceCount, priorities.ToImmutableArray());

    internal static ApiSchemaDocumentNodes Build(Scenario scenario, bool includeOpenApi) =>
        new(
            Project(scenario, -1, includeOpenApi),
            Enumerable
                .Range(0, scenario.Priorities.Length)
                .Select(i => (JsonNode)Project(scenario, i, includeOpenApi))
                .ToArray()
        );

    private static JsonObject Project(Scenario scenario, int index, bool includeOpenApi)
    {
        var resources = new JsonObject();
        var abstracts = new JsonObject();
        for (var i = 0; i < scenario.ResourceCount; i++)
        {
            var resource = new JsonObject
            {
                ["resourceName"] = $"Resource{i}",
                ["jsonSchema"] = JsonNode.Parse(scenario.Payload),
                // The same names outside the documented removal paths must survive.
                ["nested"] = new JsonObject
                {
                    ["openApiBaseDocuments"] = scenario.Revision,
                    ["openApiFragments"] = scenario.Revision,
                    ["openApiFragment"] = scenario.Revision,
                },
            };
            var abstractResource = new JsonObject { ["resourceName"] = $"Abstract{i}" };
            if (includeOpenApi)
            {
                resource["openApiFragments"] = CreateDocumentation(scenario);
                abstractResource["openApiFragment"] = CreateDocumentation(scenario);
            }
            resources[$"resource{i}"] = resource;
            abstracts[$"abstract{i}"] = abstractResource;
        }
        var project = new JsonObject
        {
            ["projectEndpointName"] = Endpoint(index),
            ["projectName"] = $"Project-{index}-{scenario.Revision}",
            ["projectVersion"] = $"1.{scenario.Revision}.0",
            ["isExtensionProject"] = index >= 0,
            ["resourceSchemas"] = resources,
            ["abstractResources"] = abstracts,
        };
        if (includeOpenApi)
        {
            project["openApiBaseDocuments"] = CreateDocumentation(scenario);
        }
        return new JsonObject { ["apiSchemaVersion"] = "1.0.0", ["projectSchema"] = project };
    }

    private static JsonObject CreateDocumentation(Scenario scenario) =>
        new() { ["description"] = scenario.Documentation, ["payload"] = JsonNode.Parse(scenario.Payload) };

    // Core sorts between extensions, and Unicode/case exercise ordinal ordering.
    private static string Endpoint(int index) =>
        index < 0 ? "ed-fi" : new[] { "Zeta", "alpha", "é", "雪" }[index];

    internal static ApiSchemaDocumentNodes Permute(ApiSchemaDocumentNodes nodes, Scenario scenario) =>
        new(
            JsonNode.Parse(
                CanonicalJsonPropertyScenarios.Permute(
                    nodes.CoreApiSchemaRootNode.ToJsonString(),
                    scenario.Revision
                )
            )!,
            nodes
                .ExtensionApiSchemaRootNodes.Select(
                    (node, index) => (Node: node, Priority: scenario.Priorities[index])
                )
                .OrderByDescending(x => x.Priority)
                .ThenByDescending(
                    x => x.Node["projectSchema"]!["projectEndpointName"]!.GetValue<string>(),
                    StringComparer.Ordinal
                )
                .Select(x =>
                    JsonNode.Parse(
                        CanonicalJsonPropertyScenarios.Permute(x.Node.ToJsonString(), scenario.Revision)
                    )!
                )
                .ToArray()
        );

    internal static IEnumerable<JsonNode> All(ApiSchemaDocumentNodes nodes) =>
        nodes.ExtensionApiSchemaRootNodes.Prepend(nodes.CoreApiSchemaRootNode);

    internal static string[] Snapshot(ApiSchemaDocumentNodes nodes) =>
        All(nodes).Select(n => n.ToJsonString()).ToArray();

    internal static ApiSchemaDocumentNodes Normalize(ApiSchemaDocumentNodes nodes) =>
        new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance)
            .Normalize(nodes)
            .Should()
            .BeOfType<ApiSchemaNormalizationResult.SuccessResult>()
            .Subject.NormalizedNodes;

    internal static EffectiveSchemaHashProvider Provider() =>
        new(NullLogger<EffectiveSchemaHashProvider>.Instance);

    internal static string ExpectedHash(ApiSchemaDocumentNodes normalized)
    {
        // Independent manifest oracle: whole-buffer SHA-256, with no production metadata extractor,
        // canonical serializer, or hash provider. Sorting includes the core project.
        var entries = All(normalized)
            .Select(node => node["projectSchema"]!.AsObject())
            .OrderBy(project => project["projectEndpointName"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(project =>
                string.Join(
                    "|",
                    project["projectEndpointName"]!.GetValue<string>(),
                    project["projectName"]!.GetValue<string>(),
                    project["projectVersion"]!.GetValue<string>(),
                    project["isExtensionProject"]!.GetValue<bool>() ? "true" : "false",
                    Hash(CanonicalJsonPropertyScenarios.Expected(project.ToJsonString()))
                )
            );
        var lines = new[]
        {
            SchemaHashConstants.HashVersion,
            $"relationalMappingVersion={SchemaHashConstants.RelationalMappingVersion}",
            $"apiSchemaFormatVersion={normalized.CoreApiSchemaRootNode["apiSchemaVersion"]!.GetValue<string>()}",
        }.Concat(entries);
        return Hash(string.Join("\n", lines));
    }

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
