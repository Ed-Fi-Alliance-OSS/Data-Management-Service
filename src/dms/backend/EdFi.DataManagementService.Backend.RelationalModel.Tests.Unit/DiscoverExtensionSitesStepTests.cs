// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for a json schema with extension sites.
/// </summary>
[TestFixture]
public class Given_A_JsonSchema_With_Extension_Sites
{
    private RelationalModelBuilderContext _context = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = CreateSchemaWithRootAndCollectionExtensions();
        _context = new RelationalModelBuilderContext { JsonSchemaForInsert = schema };

        var step = new DiscoverExtensionSitesStep();

        step.Execute(_context);
    }

    /// <summary>
    /// It should capture sites in deterministic order.
    /// </summary>
    [Test]
    public void It_should_capture_sites_in_deterministic_order()
    {
        _context
            .ExtensionSites.Select(site => site.ExtensionPath.Canonical)
            .Should()
            .Equal("$._ext", "$.addresses[*]._ext");
    }

    /// <summary>
    /// It should capture owning scopes and project keys.
    /// </summary>
    [Test]
    public void It_should_capture_owning_scopes_and_project_keys()
    {
        var rootSite = _context.ExtensionSites[0];
        rootSite.OwningScope.Canonical.Should().Be("$");
        rootSite.ProjectKeys.Should().Equal("sample", "tpdm");

        var nestedSite = _context.ExtensionSites[1];
        nestedSite.OwningScope.Canonical.Should().Be("$.addresses[*]");
        nestedSite.ProjectKeys.Should().Equal("sample");
    }

    /// <summary>
    /// Create schema with root and collection extensions.
    /// </summary>
    private static JsonObject CreateSchemaWithRootAndCollectionExtensions()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = CreateExtensionSchema("tpdm", "sample"),
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["_ext"] = CreateExtensionSchema("sample") },
                    },
                },
            },
        };
    }

    /// <summary>
    /// Create extension schema.
    /// </summary>
    private static JsonObject CreateExtensionSchema(params string[] projectKeys)
    {
        JsonObject projects = new();

        foreach (var projectKey in projectKeys)
        {
            projects[projectKey] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        }

        return new JsonObject { ["type"] = "object", ["properties"] = projects };
    }
}

/// <summary>
/// Test fixture for a json schema with extension scalars.
/// </summary>
[TestFixture]
public class Given_A_JsonSchema_With_Extension_Scalars
{
    private IReadOnlyList<string> _scalarPaths = Array.Empty<string>();

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = CreateSchemaWithExtensionScalar();
        var context = new RelationalModelBuilderContext { JsonSchemaForInsert = schema };

        var step = new DiscoverExtensionSitesStep();

        step.Execute(context);

        _scalarPaths = ScalarPathCollector.Collect(schema, context.ExtensionSites);
    }

    /// <summary>
    /// It should skip scalar paths under ext.
    /// </summary>
    [Test]
    public void It_should_skip_scalar_paths_under_ext()
    {
        _scalarPaths.Should().Contain("$.studentUniqueId");
        _scalarPaths.Should().NotContain("$._ext.sample.customField");
    }

    /// <summary>
    /// Create schema with extension scalar.
    /// </summary>
    private static JsonObject CreateSchemaWithExtensionScalar()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["studentUniqueId"] = new JsonObject { ["type"] = "string" },
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["customField"] = new JsonObject { ["type"] = "string" },
                            },
                        },
                    },
                },
            },
        };
    }
}

/// <summary>
/// Test type scalar path collector.
/// </summary>
internal static class ScalarPathCollector
{
    /// <summary>
    /// Collect.
    /// </summary>
    public static IReadOnlyList<string> Collect(
        JsonObject schema,
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
        HashSet<string> extensionPaths = extensionSites
            .Select(site => site.ExtensionPath.Canonical)
            .ToHashSet(StringComparer.Ordinal);

        List<string> scalarPaths = [];
        CollectSchema(schema, [], extensionPaths, scalarPaths);

        return scalarPaths.ToArray();
    }

    /// <summary>
    /// Collect schema.
    /// </summary>
    private static void CollectSchema(
        JsonObject schema,
        List<JsonPathSegment> segments,
        HashSet<string> extensionPaths,
        List<string> scalarPaths
    )
    {
        var schemaKind = DetermineSchemaKind(schema);

        switch (schemaKind)
        {
            case SchemaKind.Object:
                CollectObjectSchema(schema, segments, extensionPaths, scalarPaths);
                break;
            case SchemaKind.Array:
                CollectArraySchema(schema, segments, extensionPaths, scalarPaths);
                break;
            case SchemaKind.Scalar:
                scalarPaths.Add(JsonPathExpressionCompiler.FromSegments(segments).Canonical);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Collect object schema.
    /// </summary>
    private static void CollectObjectSchema(
        JsonObject schema,
        List<JsonPathSegment> segments,
        HashSet<string> extensionPaths,
        List<string> scalarPaths
    )
    {
        if (!schema.TryGetPropertyValue("properties", out var propertiesNode) || propertiesNode is null)
        {
            return;
        }

        if (propertiesNode is not JsonObject propertiesObject)
        {
            return;
        }

        foreach (var property in propertiesObject.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (property.Value is not JsonObject propertySchema)
            {
                continue;
            }

            List<JsonPathSegment> propertySegments =
            [
                .. segments,
                new JsonPathSegment.Property(property.Key),
            ];

            var propertyPath = JsonPathExpressionCompiler.FromSegments(propertySegments).Canonical;

            if (extensionPaths.Contains(propertyPath))
            {
                continue;
            }

            CollectSchema(propertySchema, propertySegments, extensionPaths, scalarPaths);
        }
    }

    /// <summary>
    /// Collect array schema.
    /// </summary>
    private static void CollectArraySchema(
        JsonObject schema,
        List<JsonPathSegment> segments,
        HashSet<string> extensionPaths,
        List<string> scalarPaths
    )
    {
        if (!schema.TryGetPropertyValue("items", out var itemsNode) || itemsNode is null)
        {
            return;
        }

        if (itemsNode is not JsonObject itemsSchema)
        {
            return;
        }

        List<JsonPathSegment> itemSegments = [.. segments, new JsonPathSegment.AnyArrayElement()];

        CollectSchema(itemsSchema, itemSegments, extensionPaths, scalarPaths);
    }

    /// <summary>
    /// Determine schema kind.
    /// </summary>
    private static SchemaKind DetermineSchemaKind(JsonObject schema)
    {
        if (schema.TryGetPropertyValue("type", out var typeNode) && typeNode is JsonValue jsonValue)
        {
            var schemaType = jsonValue.GetValue<string>();

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
    /// Test type schema kind.
    /// </summary>
    private enum SchemaKind
    {
        Object,
        Array,
        Scalar,
    }
}
