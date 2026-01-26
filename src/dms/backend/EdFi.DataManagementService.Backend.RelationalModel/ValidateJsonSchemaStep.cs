// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

public sealed class ValidateJsonSchemaStep : IRelationalModelBuilderStep
{
    private static readonly IReadOnlyList<string> UnsupportedKeywords =
    [
        "$ref",
        "oneOf",
        "anyOf",
        "allOf",
        "enum",
        "patternProperties",
    ];

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

        ValidateSchema(rootSchema, "$", isRoot: true);
    }

    private static void ValidateSchema(JsonObject schema, string path, bool isRoot)
    {
        ValidateUnsupportedKeywords(schema, path);

        var schemaKind = DetermineSchemaKind(schema, path, isRoot);

        switch (schemaKind)
        {
            case SchemaKind.Object:
                ValidateObjectSchema(schema, path);
                break;
            case SchemaKind.Array:
                ValidateArraySchema(schema, path);
                break;
            case SchemaKind.Scalar:
                break;
            default:
                throw new InvalidOperationException($"Unknown schema kind at {path}.");
        }
    }

    private static void ValidateObjectSchema(JsonObject schema, string path)
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

            ValidateSchema(propertySchema, $"{path}.properties.{property.Key}", isRoot: false);
        }
    }

    private static void ValidateArraySchema(JsonObject schema, string path)
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
            throw new InvalidOperationException(
                $"Array schema items must be type object at {path}.items.type."
            );
        }

        ValidateSchema(itemsSchema, $"{path}.items", isRoot: false);
    }

    private static void ValidateUnsupportedKeywords(JsonObject schema, string path)
    {
        foreach (var keyword in UnsupportedKeywords)
        {
            if (schema.ContainsKey(keyword))
            {
                throw new InvalidOperationException(
                    $"Unsupported schema keyword '{keyword}' at {path}.{keyword}."
                );
            }
        }

        if (schema.TryGetPropertyValue("type", out var typeNode) && typeNode is JsonArray)
        {
            throw new InvalidOperationException($"Unsupported schema keyword 'type' at {path}.type.");
        }
    }

    private static SchemaKind DetermineSchemaKind(JsonObject schema, string path, bool isRoot)
    {
        var schemaType = GetSchemaType(schema, path);

        if (schemaType is not null)
        {
            if (isRoot && !string.Equals(schemaType, "object", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Root schema must be an object at {path}.");
            }

            return schemaType switch
            {
                "object" => SchemaKind.Object,
                "array" => SchemaKind.Array,
                _ => SchemaKind.Scalar,
            };
        }

        if (schema.ContainsKey("items"))
        {
            if (isRoot)
            {
                throw new InvalidOperationException($"Root schema must be an object at {path}.");
            }

            return SchemaKind.Array;
        }

        if (schema.ContainsKey("properties") || isRoot)
        {
            return SchemaKind.Object;
        }

        return SchemaKind.Scalar;
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

    private enum SchemaKind
    {
        Object,
        Array,
        Scalar,
    }
}
