// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

public sealed class ValidateJsonSchemaStep : IRelationalModelBuilderStep
{
    public void Execute(RelationalModelBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var jsonSchemaForInsert =
            context.JsonSchemaForInsert
            ?? throw new InvalidOperationException("JsonSchemaForInsert must be provided before validation.");

        if (jsonSchemaForInsert is not JsonObject rootSchema)
        {
            throw new InvalidOperationException("Json schema root must be an object.");
        }

        ValidateSchema(rootSchema, "$", isRoot: true, [], context);
    }

    private static void ValidateSchema(
        JsonObject schema,
        string path,
        bool isRoot,
        IReadOnlyList<JsonPathSegment> jsonPathSegments,
        RelationalModelBuilderContext context
    )
    {
        JsonSchemaUnsupportedKeywordValidator.Validate(schema, path);

        var schemaKind = JsonSchemaTraversalConventions.DetermineSchemaKind(
            schema,
            path,
            isRoot,
            includeTypePathInErrors: true
        );

        switch (schemaKind)
        {
            case SchemaKind.Object:
                ValidateObjectSchema(schema, path, jsonPathSegments, context);
                break;
            case SchemaKind.Array:
                ValidateArraySchema(schema, path, jsonPathSegments, context);
                break;
            case SchemaKind.Scalar:
                break;
            default:
                throw new InvalidOperationException($"Unknown schema kind at {path}.");
        }
    }

    private static void ValidateObjectSchema(
        JsonObject schema,
        string path,
        IReadOnlyList<JsonPathSegment> jsonPathSegments,
        RelationalModelBuilderContext context
    )
    {
        if (!schema.TryGetPropertyValue("properties", out var propertiesNode) || propertiesNode is null)
        {
            return;
        }

        if (propertiesNode is not JsonObject propertiesObject)
        {
            throw new InvalidOperationException($"Expected properties to be an object at {path}.properties.");
        }

        foreach (var property in propertiesObject.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (property.Value is not JsonObject propertySchema)
            {
                throw new InvalidOperationException(
                    $"Expected property schema to be an object at {path}.properties.{property.Key}."
                );
            }

            List<JsonPathSegment> propertySegments =
            [
                .. jsonPathSegments,
                new JsonPathSegment.Property(property.Key),
            ];

            ValidateSchema(
                propertySchema,
                $"{path}.properties.{property.Key}",
                isRoot: false,
                propertySegments,
                context
            );
        }
    }

    private static void ValidateArraySchema(
        JsonObject schema,
        string path,
        IReadOnlyList<JsonPathSegment> jsonPathSegments,
        RelationalModelBuilderContext context
    )
    {
        if (!schema.TryGetPropertyValue("items", out var itemsNode) || itemsNode is null)
        {
            throw new InvalidOperationException($"Array schema items must be an object at {path}.items.");
        }

        if (itemsNode is not JsonObject itemsSchema)
        {
            throw new InvalidOperationException($"Array schema items must be an object at {path}.items.");
        }

        var itemsType = GetSchemaType(itemsSchema, $"{path}.items");

        if (!string.Equals(itemsType, "object", StringComparison.Ordinal))
        {
            var arrayPath = JsonPathExpressionCompiler.FromSegments(jsonPathSegments).Canonical;

            if (string.Equals(itemsType, "array", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Array schema items must be type object at {arrayPath}."
                );
            }

            List<JsonPathSegment> arrayElementSegments =
            [
                .. jsonPathSegments,
                new JsonPathSegment.AnyArrayElement(),
            ];
            var arrayElementPath = JsonPathExpressionCompiler.FromSegments(arrayElementSegments);

            if (!context.TryGetDescriptorPath(arrayElementPath, out _))
            {
                throw new InvalidOperationException(
                    $"Array schema items must be type object at {arrayPath}."
                );
            }

            ValidateSchema(itemsSchema, $"{path}.items", isRoot: false, arrayElementSegments, context);
            return;
        }

        List<JsonPathSegment> arraySegments = [.. jsonPathSegments, new JsonPathSegment.AnyArrayElement()];

        ValidateSchema(itemsSchema, $"{path}.items", isRoot: false, arraySegments, context);
    }

    private static string? GetSchemaType(JsonObject schema, string path)
    {
        if (!schema.TryGetPropertyValue("type", out var typeNode) || typeNode is null)
        {
            return null;
        }

        return typeNode switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            JsonArray => throw new InvalidOperationException(
                $"Unsupported schema keyword 'type' at {path}.type."
            ),
            _ => throw new InvalidOperationException($"Expected type to be a string at {path}.type."),
        };
    }
}
