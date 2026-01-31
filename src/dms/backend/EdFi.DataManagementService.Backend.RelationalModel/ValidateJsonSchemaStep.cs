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

        HashSet<string> scalarPaths = new(StringComparer.Ordinal);
        HashSet<string> arrayPaths = new(StringComparer.Ordinal);

        ValidateSchema(rootSchema, "$", isRoot: true, [], context, scalarPaths, arrayPaths);

        ValidateIdentityJsonPaths(context, scalarPaths);
        ValidateArrayUniquenessConstraints(context, scalarPaths, arrayPaths);
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
        RelationalModelBuilderContext context,
        ISet<string> scalarPaths,
        ISet<string> arrayPaths
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
                ValidateObjectSchema(schema, path, jsonPathSegments, context, scalarPaths, arrayPaths);
                break;
            case SchemaKind.Array:
                ValidateArraySchema(schema, path, jsonPathSegments, context, scalarPaths, arrayPaths);
                break;
            case SchemaKind.Scalar:
                scalarPaths.Add(JsonPathExpressionCompiler.FromSegments(jsonPathSegments).Canonical);
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
        RelationalModelBuilderContext context,
        ISet<string> scalarPaths,
        ISet<string> arrayPaths
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
                context,
                scalarPaths,
                arrayPaths
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
        RelationalModelBuilderContext context,
        ISet<string> scalarPaths,
        ISet<string> arrayPaths
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

        List<JsonPathSegment> arraySegments = [.. jsonPathSegments, new JsonPathSegment.AnyArrayElement()];
        arrayPaths.Add(JsonPathExpressionCompiler.FromSegments(arraySegments).Canonical);

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

            ValidateSchema(
                itemsSchema,
                $"{path}.items",
                isRoot: false,
                arrayElementSegments,
                context,
                scalarPaths,
                arrayPaths
            );
            return;
        }

        ValidateSchema(
            itemsSchema,
            $"{path}.items",
            isRoot: false,
            arraySegments,
            context,
            scalarPaths,
            arrayPaths
        );
    }

    private static void ValidateIdentityJsonPaths(
        RelationalModelBuilderContext context,
        IReadOnlySet<string> scalarPaths
    )
    {
        var missing = context
            .IdentityJsonPaths.Select(path => path.Canonical)
            .Where(path => !scalarPaths.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"identityJsonPaths were not found in JSON schema for resource "
                + $"'{context.ProjectName}:{context.ResourceName}': {string.Join(", ", missing)}."
        );
    }

    private static void ValidateArrayUniquenessConstraints(
        RelationalModelBuilderContext context,
        IReadOnlySet<string> scalarPaths,
        IReadOnlySet<string> arrayPaths
    )
    {
        if (context.ArrayUniquenessConstraints.Count == 0)
        {
            return;
        }

        HashSet<string> missingPaths = new(StringComparer.Ordinal);
        HashSet<string> missingBasePaths = new(StringComparer.Ordinal);

        foreach (var constraint in context.ArrayUniquenessConstraints)
        {
            ValidateArrayUniquenessConstraint(
                constraint,
                scalarPaths,
                arrayPaths,
                missingPaths,
                missingBasePaths
            );
        }

        if (missingBasePaths.Count > 0)
        {
            throw new InvalidOperationException(
                $"arrayUniquenessConstraints basePath values were not found in JSON schema for resource "
                    + $"'{context.ProjectName}:{context.ResourceName}': "
                    + string.Join(", ", missingBasePaths.OrderBy(path => path, StringComparer.Ordinal))
                    + "."
            );
        }

        if (missingPaths.Count > 0)
        {
            throw new InvalidOperationException(
                $"arrayUniquenessConstraints paths were not found in JSON schema for resource "
                    + $"'{context.ProjectName}:{context.ResourceName}': "
                    + string.Join(", ", missingPaths.OrderBy(path => path, StringComparer.Ordinal))
                    + "."
            );
        }
    }

    private static void ValidateArrayUniquenessConstraint(
        ArrayUniquenessConstraintInput constraint,
        IReadOnlySet<string> scalarPaths,
        IReadOnlySet<string> arrayPaths,
        ISet<string> missingPaths,
        ISet<string> missingBasePaths
    )
    {
        var basePath = constraint.BasePath;

        if (basePath is not null && !arrayPaths.Contains(basePath.Value.Canonical))
        {
            missingBasePaths.Add(basePath.Value.Canonical);
        }

        foreach (var path in constraint.Paths)
        {
            var resolvedPath = basePath is null ? path : ResolveRelativePath(basePath.Value, path);

            if (!scalarPaths.Contains(resolvedPath.Canonical))
            {
                missingPaths.Add(resolvedPath.Canonical);
            }
        }

        foreach (var nested in constraint.NestedConstraints)
        {
            ValidateArrayUniquenessConstraint(
                nested,
                scalarPaths,
                arrayPaths,
                missingPaths,
                missingBasePaths
            );
        }
    }

    private static JsonPathExpression ResolveRelativePath(
        JsonPathExpression basePath,
        JsonPathExpression relativePath
    )
    {
        if (relativePath.Segments.Count == 0)
        {
            return basePath;
        }

        var combinedSegments = basePath.Segments.Concat(relativePath.Segments).ToArray();
        return JsonPathExpressionCompiler.FromSegments(combinedSegments);
    }
}
