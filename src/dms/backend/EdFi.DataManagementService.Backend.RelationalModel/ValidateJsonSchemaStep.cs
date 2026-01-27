// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Validates <c>resourceSchema.jsonSchemaForInsert</c> for the assumptions required by relational model
/// derivation.
///
/// The relational model builder assumes the schema is fully dereferenced/expanded and uses a constrained
/// subset of JSON Schema. Unsupported keywords (for example <c>$ref</c>, <c>oneOf</c>, and <c>enum</c>) are
/// rejected with path-inclusive errors.
///
/// Objects are traversed through <c>properties</c> only. <c>additionalProperties</c> is treated as
/// "prune/ignore" (closed-world persistence) and is therefore not validated or traversed.
/// </summary>
public sealed class ValidateJsonSchemaStep : IRelationalModelBuilderStep
{
    /// <summary>
    /// Validates the schema attached to <see cref="RelationalModelBuilderContext.JsonSchemaForInsert"/>.
    /// </summary>
    /// <param name="context">The builder context containing the schema and descriptor metadata.</param>
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

    /// <summary>
    /// Validates a schema node and recursively validates its descendants according to the supported keyword
    /// set.
    /// </summary>
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

    /// <summary>
    /// Validates an object schema by validating each property schema under <c>properties</c>.
    /// </summary>
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

    /// <summary>
    /// Validates an array schema, enforcing the "arrays are tables" rule: items are normally objects, with a
    /// special case for descriptor scalar arrays.
    /// </summary>
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

        var itemsKind = JsonSchemaTraversalConventions.DetermineSchemaKind(
            itemsSchema,
            $"{path}.items",
            includeTypePathInErrors: true
        );

        if (itemsKind != SchemaKind.Object)
        {
            var arrayPath = JsonPathExpressionCompiler.FromSegments(jsonPathSegments).Canonical;

            if (itemsKind == SchemaKind.Array)
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
}
