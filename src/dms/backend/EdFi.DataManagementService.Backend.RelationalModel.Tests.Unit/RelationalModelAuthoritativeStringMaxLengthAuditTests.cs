// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for an authoritative api schema for ed fi string max length rules.
/// </summary>
[TestFixture]
public class Given_An_Authoritative_ApiSchema_For_Ed_Fi_String_MaxLength_Rules
{
    private const string ExtensionPropertyName = "_ext";
    private IReadOnlyList<OffendingString> _offendingStrings = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var authoritativeFixtureRoot = BackendFixturePaths.GetAuthoritativeFixtureRoot(
            TestContext.CurrentContext.TestDirectory
        );
        var inputPath = Path.Combine(
            authoritativeFixtureRoot,
            "ds-5.2",
            "inputs",
            "ds-5.2-api-schema-authoritative.json"
        );

        File.Exists(inputPath).Should().BeTrue($"fixture missing at {inputPath}");

        var apiSchemaRoot = LoadApiSchemaRoot(inputPath);

        if (apiSchemaRoot is not JsonObject rootObject)
        {
            throw new InvalidOperationException("ApiSchema root must be a JSON object.");
        }

        var projectSchema = RequireObject(rootObject["projectSchema"], "projectSchema");
        var resourceSchemas = RequireObject(
            projectSchema["resourceSchemas"],
            "projectSchema.resourceSchemas"
        );

        List<OffendingString> offenders = [];

        foreach (var resourceEntry in resourceSchemas.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var resourceEndpointName = resourceEntry.Key;

            if (resourceEntry.Value is not JsonObject)
            {
                throw new InvalidOperationException(
                    $"Expected resource schema '{resourceEndpointName}' to be an object."
                );
            }

            var context = new RelationalModelBuilderContext
            {
                ApiSchemaRoot = apiSchemaRoot,
                ResourceEndpointName = resourceEndpointName,
            };

            new ExtractInputsStep().Execute(context);

            if (context.JsonSchemaForInsert is not JsonObject jsonSchemaForInsert)
            {
                throw new InvalidOperationException(
                    $"Expected jsonSchemaForInsert to be an object for {resourceEndpointName}."
                );
            }

            var descriptorPaths = new HashSet<string>(
                context.DescriptorPathsByJsonPath.Keys,
                StringComparer.Ordinal
            );

            CollectMissingMaxLengthStrings(
                jsonSchemaForInsert,
                [],
                descriptorPaths,
                context.StringMaxLengthOmissionPaths,
                offenders,
                resourceEndpointName
            );
        }

        _offendingStrings = offenders
            .OrderBy(item => item.ResourceEndpointName, StringComparer.Ordinal)
            .ThenBy(item => item.JsonPath, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// It should not have unexpected missing max length strings.
    /// </summary>
    [Test]
    public void It_should_not_have_unexpected_missing_maxLength_strings()
    {
        if (_offendingStrings.Count == 0)
        {
            return;
        }

        Assert.Fail(BuildFailureMessage(_offendingStrings));
    }

    /// <summary>
    /// Collect missing max length strings.
    /// </summary>
    private static void CollectMissingMaxLengthStrings(
        JsonObject schema,
        List<JsonPathSegment> pathSegments,
        HashSet<string> descriptorPaths,
        IReadOnlySet<string> stringMaxLengthOmissionPaths,
        List<OffendingString> offenders,
        string resourceEndpointName
    )
    {
        var currentPath = JsonPathExpressionCompiler.FromSegments(pathSegments).Canonical;
        var schemaKind = DetermineSchemaKind(schema, currentPath);

        switch (schemaKind)
        {
            case SchemaKind.Object:
                if (
                    !schema.TryGetPropertyValue("properties", out var propertiesNode)
                    || propertiesNode is null
                )
                {
                    return;
                }

                if (propertiesNode is not JsonObject propertiesObject)
                {
                    throw new InvalidOperationException(
                        $"Expected properties to be an object at {currentPath}."
                    );
                }

                foreach (var property in propertiesObject.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    if (string.Equals(property.Key, ExtensionPropertyName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (property.Value is not JsonObject propertySchema)
                    {
                        throw new InvalidOperationException(
                            $"Expected property schema to be an object at {BuildPropertyPath(pathSegments, property.Key)}."
                        );
                    }

                    pathSegments.Add(new JsonPathSegment.Property(property.Key));
                    CollectMissingMaxLengthStrings(
                        propertySchema,
                        pathSegments,
                        descriptorPaths,
                        stringMaxLengthOmissionPaths,
                        offenders,
                        resourceEndpointName
                    );
                    pathSegments.RemoveAt(pathSegments.Count - 1);
                }
                break;
            case SchemaKind.Array:
                if (!schema.TryGetPropertyValue("items", out var itemsNode) || itemsNode is null)
                {
                    throw new InvalidOperationException(
                        $"Array schema items must be an object at {currentPath}."
                    );
                }

                if (itemsNode is not JsonObject itemsSchema)
                {
                    throw new InvalidOperationException(
                        $"Array schema items must be an object at {currentPath}."
                    );
                }

                pathSegments.Add(new JsonPathSegment.AnyArrayElement());
                CollectMissingMaxLengthStrings(
                    itemsSchema,
                    pathSegments,
                    descriptorPaths,
                    stringMaxLengthOmissionPaths,
                    offenders,
                    resourceEndpointName
                );
                pathSegments.RemoveAt(pathSegments.Count - 1);
                break;
            case SchemaKind.Scalar:
                ValidateStringSchema(
                    schema,
                    currentPath,
                    descriptorPaths,
                    stringMaxLengthOmissionPaths,
                    offenders,
                    resourceEndpointName
                );
                break;
            default:
                throw new InvalidOperationException($"Unknown schema kind at {currentPath}.");
        }
    }

    /// <summary>
    /// Validate string schema.
    /// </summary>
    private static void ValidateStringSchema(
        JsonObject schema,
        string currentPath,
        HashSet<string> descriptorPaths,
        IReadOnlySet<string> stringMaxLengthOmissionPaths,
        List<OffendingString> offenders,
        string resourceEndpointName
    )
    {
        var schemaType = TryGetSchemaType(schema, currentPath);

        if (!string.Equals(schemaType, "string", StringComparison.Ordinal))
        {
            return;
        }

        if (schema.TryGetPropertyValue("maxLength", out var maxLengthNode) && maxLengthNode is not null)
        {
            return;
        }

        if (descriptorPaths.Contains(currentPath))
        {
            return;
        }

        if (IsDateOrTimeFormat(schema, currentPath))
        {
            return;
        }

        if (stringMaxLengthOmissionPaths.Contains(currentPath))
        {
            return;
        }

        if (HasEnum(schema))
        {
            return;
        }

        offenders.Add(new OffendingString(resourceEndpointName, currentPath));
    }

    /// <summary>
    /// Is date or time format.
    /// </summary>
    private static bool IsDateOrTimeFormat(JsonObject schema, string currentPath)
    {
        if (!schema.TryGetPropertyValue("format", out var formatNode) || formatNode is null)
        {
            return false;
        }

        if (formatNode is not JsonValue formatValue || !formatValue.TryGetValue<string>(out var format))
        {
            throw new InvalidOperationException($"Expected format to be a string at {currentPath}.format.");
        }

        return format switch
        {
            "date" => true,
            "date-time" => true,
            "time" => true,
            _ => false,
        };
    }

    /// <summary>
    /// Has enum.
    /// </summary>
    private static bool HasEnum(JsonObject schema)
    {
        if (!schema.TryGetPropertyValue("enum", out var enumNode) || enumNode is null)
        {
            return false;
        }

        return enumNode is JsonArray;
    }

    /// <summary>
    /// Determine schema kind.
    /// </summary>
    private static SchemaKind DetermineSchemaKind(JsonObject schema, string currentPath)
    {
        var schemaType = TryGetSchemaType(schema, currentPath);

        if (schemaType is not null)
        {
            return schemaType switch
            {
                "object" => SchemaKind.Object,
                "array" => SchemaKind.Array,
                _ => SchemaKind.Scalar,
            };
        }

        if (schema.ContainsKey("items"))
        {
            return SchemaKind.Array;
        }

        if (schema.ContainsKey("properties"))
        {
            return SchemaKind.Object;
        }

        return SchemaKind.Scalar;
    }

    /// <summary>
    /// Try get schema type.
    /// </summary>
    private static string? TryGetSchemaType(JsonObject schema, string currentPath)
    {
        if (!schema.TryGetPropertyValue("type", out var typeNode) || typeNode is null)
        {
            return null;
        }

        if (typeNode is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var schemaType))
        {
            throw new InvalidOperationException(
                $"Expected schema type to be a string at {currentPath}.type."
            );
        }

        return schemaType;
    }

    /// <summary>
    /// Build property path.
    /// </summary>
    private static string BuildPropertyPath(List<JsonPathSegment> pathSegments, string propertyName)
    {
        List<JsonPathSegment> propertySegments =
        [
            .. pathSegments,
            new JsonPathSegment.Property(propertyName),
        ];
        return JsonPathExpressionCompiler.FromSegments(propertySegments).Canonical;
    }

    /// <summary>
    /// Build failure message.
    /// </summary>
    private static string BuildFailureMessage(IReadOnlyList<OffendingString> offenders)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Missing maxLength string schema nodes found:");

        foreach (var offender in offenders)
        {
            builder
                .Append("- ")
                .Append(offender.ResourceEndpointName)
                .Append(": ")
                .AppendLine(offender.JsonPath);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Load api schema root.
    /// </summary>
    private static JsonNode LoadApiSchemaRoot(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path));

        return root ?? throw new InvalidOperationException($"ApiSchema parsed null: {path}");
    }

    /// <summary>
    /// Test type offending string.
    /// </summary>
    private sealed record OffendingString(string ResourceEndpointName, string JsonPath);

    /// <summary>
    /// Test type schema kind.
    /// </summary>
    private enum SchemaKind
    {
        Object,
        Array,
        Scalar,
    }
}
