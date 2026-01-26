// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

public sealed class DiscoverExtensionSitesStep : IRelationalModelBuilderStep
{
    private const string ExtensionPropertyName = "_ext";

    public void Execute(RelationalModelBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var jsonSchemaForInsert =
            context.JsonSchemaForInsert
            ?? throw new InvalidOperationException(
                "JsonSchemaForInsert must be provided before discovering extensions."
            );

        if (jsonSchemaForInsert is not JsonObject rootSchema)
        {
            throw new InvalidOperationException("Json schema root must be an object.");
        }

        context.ExtensionSites.Clear();

        DiscoverSchema(rootSchema, [], context.ExtensionSites);
    }

    private static void DiscoverSchema(
        JsonObject schema,
        List<JsonPathSegment> scopeSegments,
        List<ExtensionSite> extensionSites
    )
    {
        var schemaKind = JsonSchemaTraversalConventions.DetermineSchemaKind(schema);

        switch (schemaKind)
        {
            case SchemaKind.Object:
                DiscoverObjectSchema(schema, scopeSegments, extensionSites);
                break;
            case SchemaKind.Array:
                DiscoverArraySchema(schema, scopeSegments, extensionSites);
                break;
            case SchemaKind.Scalar:
                break;
            default:
                throw new InvalidOperationException("Unknown schema kind while discovering extensions.");
        }
    }

    private static void DiscoverObjectSchema(
        JsonObject schema,
        List<JsonPathSegment> scopeSegments,
        List<ExtensionSite> extensionSites
    )
    {
        var scopePath = JsonPathExpressionCompiler.FromSegments(scopeSegments).Canonical;

        if (!schema.TryGetPropertyValue("properties", out var propertiesNode) || propertiesNode is null)
        {
            return;
        }

        if (propertiesNode is not JsonObject propertiesObject)
        {
            throw new InvalidOperationException($"Expected properties to be an object at {scopePath}.");
        }

        foreach (var property in propertiesObject.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var propertyPath = BuildPropertyPath(scopeSegments, property.Key);

            if (property.Value is not JsonObject propertySchema)
            {
                throw new InvalidOperationException(
                    $"Expected property schema to be an object at {propertyPath}."
                );
            }

            if (string.Equals(property.Key, ExtensionPropertyName, StringComparison.Ordinal))
            {
                List<JsonPathSegment> extensionPathSegments =
                [
                    .. scopeSegments,
                    new JsonPathSegment.Property(ExtensionPropertyName),
                ];

                var owningScope = JsonPathExpressionCompiler.FromSegments(scopeSegments);
                var extensionPath = JsonPathExpressionCompiler.FromSegments(extensionPathSegments);
                var projectKeys = ExtractProjectKeys(propertySchema, extensionPath);

                extensionSites.Add(new ExtensionSite(owningScope, extensionPath, projectKeys));
                continue;
            }

            List<JsonPathSegment> childSegments =
            [
                .. scopeSegments,
                new JsonPathSegment.Property(property.Key),
            ];

            DiscoverSchema(propertySchema, childSegments, extensionSites);
        }
    }

    private static void DiscoverArraySchema(
        JsonObject schema,
        List<JsonPathSegment> scopeSegments,
        List<ExtensionSite> extensionSites
    )
    {
        var scopePath = JsonPathExpressionCompiler.FromSegments(scopeSegments).Canonical;

        if (!schema.TryGetPropertyValue("items", out var itemsNode) || itemsNode is null)
        {
            throw new InvalidOperationException($"Array schema items must be an object at {scopePath}.");
        }

        if (itemsNode is not JsonObject itemsSchema)
        {
            throw new InvalidOperationException($"Array schema items must be an object at {scopePath}.");
        }

        List<JsonPathSegment> itemSegments = [.. scopeSegments, new JsonPathSegment.AnyArrayElement()];

        DiscoverSchema(itemsSchema, itemSegments, extensionSites);
    }

    private static IReadOnlyList<string> ExtractProjectKeys(
        JsonObject extensionSchema,
        JsonPathExpression extensionPath
    )
    {
        var extensionPathCanonical = extensionPath.Canonical;

        if (
            !extensionSchema.TryGetPropertyValue("properties", out var propertiesNode)
            || propertiesNode is null
        )
        {
            throw new InvalidOperationException(
                $"Expected extension site properties at {extensionPathCanonical}.properties."
            );
        }

        if (propertiesNode is not JsonObject propertiesObject)
        {
            throw new InvalidOperationException(
                $"Expected extension site properties to be an object at {extensionPathCanonical}.properties."
            );
        }

        List<string> projectKeys = new(propertiesObject.Count);

        foreach (var project in propertiesObject.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (project.Value is not JsonObject)
            {
                throw new InvalidOperationException(
                    $"Expected extension project to be an object at {extensionPathCanonical}.properties.{project.Key}."
                );
            }

            projectKeys.Add(project.Key);
        }

        return projectKeys.ToArray();
    }

    private static string BuildPropertyPath(List<JsonPathSegment> scopeSegments, string propertyName)
    {
        List<JsonPathSegment> propertySegments =
        [
            .. scopeSegments,
            new JsonPathSegment.Property(propertyName),
        ];

        return JsonPathExpressionCompiler.FromSegments(propertySegments).Canonical;
    }
}
