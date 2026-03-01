// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_ThinSliceMappingSetManifestJsonEmitter
{
    private const string FixturePath = "Fixtures/runtime-plan-compilation/ApiSchema.json";
    private string _manifest = null!;

    [SetUp]
    public void Setup()
    {
        var mappingSets = BuildPermutedMappingSets(reverseMappingSetOrder: true);
        _manifest = ThinSliceMappingSetManifestJsonEmitter.Emit(mappingSets);
    }

    [Test]
    public void It_should_emit_lf_only_line_endings_with_no_trailing_whitespace()
    {
        _manifest.Should().EndWith("\n");
        _manifest.Should().NotContain("\r");

        foreach (var line in _manifest.Split('\n'))
        {
            line.Should().NotMatchRegex("[ \\t]+$");
        }
    }

    [Test]
    public void It_should_emit_deterministically_when_mapping_set_and_dictionary_inputs_are_permuted()
    {
        var forwardInput = BuildPermutedMappingSets(reverseMappingSetOrder: false);
        var reverseInput = BuildPermutedMappingSets(reverseMappingSetOrder: true);

        var manifestFromForwardInput = ThinSliceMappingSetManifestJsonEmitter.Emit(forwardInput);
        var manifestFromReverseInput = ThinSliceMappingSetManifestJsonEmitter.Emit(reverseInput);

        manifestFromForwardInput.Should().Be(manifestFromReverseInput);

        ReadMappingSetDialects(manifestFromForwardInput).Should().Equal("mssql", "pgsql");
    }

    [Test]
    public void It_should_include_only_resources_with_both_read_and_write_plans()
    {
        var resourceNamesByDialect = ReadResourceNamesByDialect(_manifest);

        resourceNamesByDialect.Keys.Should().BeEquivalentTo("mssql", "pgsql");
        resourceNamesByDialect["mssql"].Should().Equal("Ed-Fi.School", "Ed-Fi.Student");
        resourceNamesByDialect["pgsql"].Should().Equal("Ed-Fi.School", "Ed-Fi.Student");
    }

    private static IReadOnlyList<MappingSet> BuildPermutedMappingSets(bool reverseMappingSetOrder)
    {
        var compiler = new MappingSetCompiler();
        var pgsql = compiler.Compile(ThinSliceFixtureModelSetBuilder.Build(FixturePath, SqlDialect.Pgsql));
        var mssql = compiler.Compile(ThinSliceFixtureModelSetBuilder.Build(FixturePath, SqlDialect.Mssql));

        var mappingSets = new[] { PermutePlanDictionaries(pgsql), PermutePlanDictionaries(mssql) };

        if (reverseMappingSetOrder)
        {
            Array.Reverse(mappingSets);
        }

        return mappingSets;
    }

    private static MappingSet PermutePlanDictionaries(MappingSet mappingSet)
    {
        var permutedWritePlans = mappingSet
            .WritePlansByResource.OrderByDescending(
                entry => ResourceSortKey(entry.Key),
                StringComparer.Ordinal
            )
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        var permutedReadPlans = mappingSet
            .ReadPlansByResource.OrderByDescending(
                entry => ResourceSortKey(entry.Key),
                StringComparer.Ordinal
            )
            .ToDictionary(entry => entry.Key, entry => entry.Value);

        return mappingSet with
        {
            WritePlansByResource = permutedWritePlans,
            ReadPlansByResource = permutedReadPlans,
        };
    }

    private static string ResourceSortKey(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}.{resource.ResourceName}";
    }

    private static IReadOnlyList<string> ReadMappingSetDialects(string manifest)
    {
        return ParseMappingSetObjects(manifest)
            .Select(mappingSetObject => mappingSetObject["mapping_set_key"]?["dialect"]?.GetValue<string>())
            .Select(value =>
                value is null
                    ? throw new InvalidOperationException("Manifest mapping set key dialect is required.")
                    : value
            )
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadResourceNamesByDialect(
        string manifest
    )
    {
        Dictionary<string, IReadOnlyList<string>> resourceNamesByDialect = [];

        foreach (var mappingSetObject in ParseMappingSetObjects(manifest))
        {
            var dialect = mappingSetObject["mapping_set_key"]?["dialect"]?.GetValue<string>();

            if (dialect is null)
            {
                throw new InvalidOperationException("Manifest mapping set key dialect is required.");
            }

            var resourcesNode = mappingSetObject["resources"];

            if (resourcesNode is not JsonArray resources)
            {
                throw new InvalidOperationException("Manifest mapping set resources array is required.");
            }

            resourceNamesByDialect[dialect] = resources
                .Select(ReadResourceName)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();
        }

        return resourceNamesByDialect;
    }

    private static string ReadResourceName(JsonNode? resourceNode)
    {
        var projectName = resourceNode?["resource"]?["project_name"]?.GetValue<string>();
        var resourceName = resourceNode?["resource"]?["resource_name"]?.GetValue<string>();

        if (projectName is null || resourceName is null)
        {
            throw new InvalidOperationException("Manifest resource identity is required.");
        }

        return $"{projectName}.{resourceName}";
    }

    private static JsonArray ParseMappingSetArray(string manifest)
    {
        var root = JsonNode.Parse(manifest);

        if (root is not JsonObject rootObject)
        {
            throw new InvalidOperationException("Manifest root must be a JSON object.");
        }

        var mappingSetsNode = rootObject["mapping_sets"];

        return mappingSetsNode switch
        {
            JsonArray mappingSets => mappingSets,
            null => throw new InvalidOperationException("Manifest mapping_sets property is required."),
            _ => throw new InvalidOperationException("Manifest mapping_sets property must be a JSON array."),
        };
    }

    private static IReadOnlyList<JsonObject> ParseMappingSetObjects(string manifest)
    {
        return ParseMappingSetArray(manifest)
            .Select(mappingSet =>
                mappingSet as JsonObject
                ?? throw new InvalidOperationException("Manifest mapping_sets entries must be JSON objects.")
            )
            .ToArray();
    }
}
