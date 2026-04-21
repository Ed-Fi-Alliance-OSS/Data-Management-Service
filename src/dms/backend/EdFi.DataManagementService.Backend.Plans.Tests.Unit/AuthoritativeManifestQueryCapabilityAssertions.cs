// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class AuthoritativeManifestQueryCapabilityAssertions
{
    public static RootColumnQueryFieldExpectation RootColumnField(
        string projectName,
        string resourceName,
        string queryFieldName,
        string jsonPath,
        params string[] expectedColumnNames
    )
    {
        return new RootColumnQueryFieldExpectation(
            projectName,
            resourceName,
            queryFieldName,
            jsonPath,
            expectedColumnNames
        );
    }

    public static void AssertRootColumnFields(
        string manifest,
        params RootColumnQueryFieldExpectation[] expectations
    )
    {
        var mappingSets = ParseMappingSets(manifest);
        mappingSets.Should().NotBeEmpty();

        foreach (var mappingSet in mappingSets)
        {
            var dialect = ReadDialect(mappingSet);

            foreach (var expectation in expectations)
            {
                var resource = FindResource(mappingSet, expectation.ProjectName, expectation.ResourceName);
                var queryCapability = ReadRequiredObject(resource["query_capability"], "query_capability");
                var support = ReadRequiredObject(queryCapability["support"], "support");

                ReadRequiredString(support, "kind")
                    .Should()
                    .Be("supported", $"{dialect} {expectation.ProjectName}.{expectation.ResourceName}");
                support["omission_kind"]
                    .Should()
                    .BeNull($"{dialect} {expectation.ProjectName}.{expectation.ResourceName}");
                support["reason"]
                    .Should()
                    .BeNull($"{dialect} {expectation.ProjectName}.{expectation.ResourceName}");

                var supportedField = FindSupportedField(queryCapability, expectation);
                ReadRequiredString(supportedField, "json_path")
                    .Should()
                    .Be(
                        expectation.JsonPath,
                        $"{dialect} {expectation.ProjectName}.{expectation.ResourceName}.{expectation.QueryFieldName}"
                    );

                var target = ReadRequiredObject(supportedField["target"], "target");
                ReadRequiredString(target, "kind")
                    .Should()
                    .Be(
                        "root_column",
                        $"{dialect} {expectation.ProjectName}.{expectation.ResourceName}.{expectation.QueryFieldName}"
                    );
                ReadRequiredString(target, "column_name")
                    .Should()
                    .BeOneOf(
                        expectation.ExpectedColumnNames,
                        $"{dialect} {expectation.ProjectName}.{expectation.ResourceName}.{expectation.QueryFieldName}"
                    );

                ReadUnsupportedQueryFieldNames(queryCapability)
                    .Should()
                    .NotContain(
                        expectation.QueryFieldName,
                        $"{dialect} {expectation.ProjectName}.{expectation.ResourceName}.{expectation.QueryFieldName}"
                    );
            }
        }
    }

    private static JsonObject FindSupportedField(
        JsonObject queryCapability,
        RootColumnQueryFieldExpectation expectation
    )
    {
        var supportedFields = ReadRequiredArray(
            queryCapability["supported_fields_in_query_field_order"],
            "supported_fields_in_query_field_order"
        );

        return supportedFields
            .Select(fieldNode => ReadRequiredObject(fieldNode, "supported_fields_in_query_field_order entry"))
            .Single(field => ReadRequiredString(field, "query_field_name") == expectation.QueryFieldName);
    }

    private static IReadOnlyList<string> ReadUnsupportedQueryFieldNames(JsonObject queryCapability)
    {
        var unsupportedFields = ReadRequiredArray(
            queryCapability["unsupported_fields_in_query_field_order"],
            "unsupported_fields_in_query_field_order"
        );

        return unsupportedFields
            .Select(fieldNode =>
                ReadRequiredObject(fieldNode, "unsupported_fields_in_query_field_order entry")
            )
            .Select(field => ReadRequiredString(field, "query_field_name"))
            .ToArray();
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

    private static string ReadDialect(JsonObject mappingSet)
    {
        var mappingSetKey = ReadRequiredObject(mappingSet["mapping_set_key"], "mapping_set_key");

        return ReadRequiredString(mappingSetKey, "dialect");
    }

    private static JsonObject FindResource(JsonObject mappingSet, string projectName, string resourceName)
    {
        var resources = ReadRequiredArray(mappingSet["resources"], "resources");

        return resources
            .Select(resourceNode => ReadRequiredObject(resourceNode, "resources entry"))
            .Single(resource =>
            {
                var identity = ReadRequiredObject(resource["resource"], "resource");

                return ReadRequiredString(identity, "project_name") == projectName
                    && ReadRequiredString(identity, "resource_name") == resourceName;
            });
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

    internal sealed record RootColumnQueryFieldExpectation(
        string ProjectName,
        string ResourceName,
        string QueryFieldName,
        string JsonPath,
        IReadOnlyList<string> ExpectedColumnNames
    );
}
