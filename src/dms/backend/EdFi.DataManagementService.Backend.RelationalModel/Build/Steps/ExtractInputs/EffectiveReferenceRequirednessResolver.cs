// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;

/// <summary>
/// Derives relational document-reference requiredness from <c>jsonSchemaForInsert</c> relative to the
/// materialized table scope that owns the reference object.
/// </summary>
internal static class EffectiveReferenceRequirednessResolver
{
    private const string ExtensionPropertyName = "_ext";

    /// <summary>
    /// Resolves whether a reference object path is required inside its owning materialized row.
    /// </summary>
    internal static bool Resolve(
        JsonNode? jsonSchemaForInsert,
        string projectName,
        string resourceName,
        JsonPathExpression referenceObjectPath
    )
    {
        if (jsonSchemaForInsert is not JsonObject rootSchema)
        {
            throw new InvalidOperationException("Json schema root must be an object.");
        }

        var resourceLabel = $"{projectName}:{resourceName}";
        var owningScopeSegmentCount = DetermineOwningScopeSegmentCount(referenceObjectPath.Segments);
        var current = ResolveOwningScopeSchema(
            rootSchema,
            referenceObjectPath,
            owningScopeSegmentCount,
            resourceLabel
        );
        var isRequired = true;

        for (var index = owningScopeSegmentCount; index < referenceObjectPath.Segments.Count; index++)
        {
            var segment = referenceObjectPath.Segments[index];
            var currentPath = JsonPathExpressionCompiler
                .FromSegments(referenceObjectPath.Segments.Take(index))
                .Canonical;
            var nextPath = JsonPathExpressionCompiler
                .FromSegments(referenceObjectPath.Segments.Take(index + 1))
                .Canonical;

            switch (segment)
            {
                case JsonPathSegment.Property property:
                    var propertySchema = ResolveRequirednessPropertySchema(
                        current,
                        property.Name,
                        currentPath,
                        nextPath,
                        referenceObjectPath,
                        resourceLabel
                    );
                    var requiredProperties = GetRequiredProperties(current, currentPath);

                    if (!requiredProperties.Contains(property.Name) || IsXNullable(propertySchema, nextPath))
                    {
                        isRequired = false;
                    }

                    current = propertySchema;
                    break;

                case JsonPathSegment.AnyArrayElement:
                    current = ResolveArrayItemSchema(
                        current,
                        currentPath,
                        nextPath,
                        referenceObjectPath,
                        resourceLabel
                    );

                    if (IsXNullable(current, nextPath))
                    {
                        isRequired = false;
                    }

                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown JSONPath segment while resolving reference path "
                            + $"'{referenceObjectPath.Canonical}' for resource '{resourceLabel}'."
                    );
            }
        }

        var finalKind = JsonSchemaTraversalConventions.DetermineSchemaKind(
            current,
            referenceObjectPath.Canonical,
            includeTypePathInErrors: true
        );

        if (finalKind != SchemaKind.Object)
        {
            throw new InvalidOperationException(
                $"Reference path '{referenceObjectPath.Canonical}' was not an object in "
                    + $"jsonSchemaForInsert for resource '{resourceLabel}'."
            );
        }

        return isRequired;
    }

    /// <summary>
    /// Finds the row scope that owns a reference. Collection row scopes reset optional ancestry, and an
    /// <c>_ext.project</c> object is the owning row scope when no deeper extension child collection owns it.
    /// </summary>
    private static int DetermineOwningScopeSegmentCount(IReadOnlyList<JsonPathSegment> segments)
    {
        var lastArrayElementIndex = -1;
        var lastExtensionProjectIndex = -1;

        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index] is JsonPathSegment.AnyArrayElement)
            {
                lastArrayElementIndex = index;
            }

            if (
                index < segments.Count - 1
                && segments[index] is JsonPathSegment.Property { Name: ExtensionPropertyName }
                && segments[index + 1] is JsonPathSegment.Property
            )
            {
                lastExtensionProjectIndex = index + 1;
            }
        }

        if (lastExtensionProjectIndex >= 0 && lastArrayElementIndex < lastExtensionProjectIndex)
        {
            return lastExtensionProjectIndex + 1;
        }

        if (lastArrayElementIndex >= 0)
        {
            return lastArrayElementIndex + 1;
        }

        return 0;
    }

    /// <summary>
    /// Resolves the schema node for the materialized owning scope without letting that scope's ancestors
    /// influence reference requiredness.
    /// </summary>
    private static JsonObject ResolveOwningScopeSchema(
        JsonObject rootSchema,
        JsonPathExpression referenceObjectPath,
        int owningScopeSegmentCount,
        string resourceLabel
    )
    {
        var current = rootSchema;

        for (var index = 0; index < owningScopeSegmentCount; index++)
        {
            var segment = referenceObjectPath.Segments[index];
            var currentPath = JsonPathExpressionCompiler
                .FromSegments(referenceObjectPath.Segments.Take(index))
                .Canonical;
            var nextPath = JsonPathExpressionCompiler
                .FromSegments(referenceObjectPath.Segments.Take(index + 1))
                .Canonical;

            current = segment switch
            {
                JsonPathSegment.Property property => ResolveScopePropertySchema(
                    current,
                    property.Name,
                    currentPath,
                    nextPath,
                    referenceObjectPath,
                    resourceLabel
                ),
                JsonPathSegment.AnyArrayElement => ResolveArrayItemSchema(
                    current,
                    currentPath,
                    nextPath,
                    referenceObjectPath,
                    resourceLabel
                ),
                _ => throw new InvalidOperationException(
                    $"Unknown JSONPath segment while resolving owning scope for reference path "
                        + $"'{referenceObjectPath.Canonical}' on resource '{resourceLabel}'."
                ),
            };
        }

        return current;
    }

    /// <summary>
    /// Resolves a property while walking row-scope ancestors.
    /// </summary>
    private static JsonObject ResolveScopePropertySchema(
        JsonObject current,
        string propertyName,
        string currentPath,
        string propertyPath,
        JsonPathExpression referenceObjectPath,
        string resourceLabel
    )
    {
        return ResolvePropertySchema(
            current,
            propertyName,
            currentPath,
            propertyPath,
            referenceObjectPath,
            resourceLabel,
            "owning scope"
        );
    }

    /// <summary>
    /// Resolves a property while walking the effective requiredness path.
    /// </summary>
    private static JsonObject ResolveRequirednessPropertySchema(
        JsonObject current,
        string propertyName,
        string currentPath,
        string propertyPath,
        JsonPathExpression referenceObjectPath,
        string resourceLabel
    )
    {
        return ResolvePropertySchema(
            current,
            propertyName,
            currentPath,
            propertyPath,
            referenceObjectPath,
            resourceLabel,
            "requiredness"
        );
    }

    /// <summary>
    /// Resolves a property schema and emits diagnostics that name the resource and reference path.
    /// </summary>
    private static JsonObject ResolvePropertySchema(
        JsonObject current,
        string propertyName,
        string currentPath,
        string propertyPath,
        JsonPathExpression referenceObjectPath,
        string resourceLabel,
        string traversalRole
    )
    {
        var schemaKind = JsonSchemaTraversalConventions.DetermineSchemaKind(
            current,
            currentPath,
            includeTypePathInErrors: true
        );

        if (schemaKind != SchemaKind.Object)
        {
            throw new InvalidOperationException(
                $"Expected object schema at '{currentPath}' while resolving {traversalRole} for reference "
                    + $"path '{referenceObjectPath.Canonical}' on resource '{resourceLabel}'."
            );
        }

        if (!current.TryGetPropertyValue("properties", out var propertiesNode) || propertiesNode is null)
        {
            throw new InvalidOperationException(
                $"Expected properties to be present at '{currentPath}' while resolving reference path "
                    + $"'{referenceObjectPath.Canonical}' on resource '{resourceLabel}'."
            );
        }

        if (propertiesNode is not JsonObject propertiesObject)
        {
            throw new InvalidOperationException(
                $"Expected properties to be an object at '{currentPath}' while resolving reference path "
                    + $"'{referenceObjectPath.Canonical}' on resource '{resourceLabel}'."
            );
        }

        if (!propertiesObject.TryGetPropertyValue(propertyName, out var propertyNode) || propertyNode is null)
        {
            throw new InvalidOperationException(
                $"Reference path '{referenceObjectPath.Canonical}' was not found in jsonSchemaForInsert "
                    + $"for resource '{resourceLabel}' at '{propertyPath}'."
            );
        }

        if (propertyNode is not JsonObject propertySchema)
        {
            throw new InvalidOperationException(
                $"Expected schema object at '{propertyPath}' while resolving reference path "
                    + $"'{referenceObjectPath.Canonical}' on resource '{resourceLabel}'."
            );
        }

        return propertySchema;
    }

    /// <summary>
    /// Resolves an array item schema for a path segment.
    /// </summary>
    private static JsonObject ResolveArrayItemSchema(
        JsonObject current,
        string currentPath,
        string itemPath,
        JsonPathExpression referenceObjectPath,
        string resourceLabel
    )
    {
        var schemaKind = JsonSchemaTraversalConventions.DetermineSchemaKind(
            current,
            currentPath,
            includeTypePathInErrors: true
        );

        if (schemaKind != SchemaKind.Array)
        {
            throw new InvalidOperationException(
                $"Expected array schema at '{currentPath}' while resolving reference path "
                    + $"'{referenceObjectPath.Canonical}' on resource '{resourceLabel}'."
            );
        }

        if (!current.TryGetPropertyValue("items", out var itemsNode) || itemsNode is null)
        {
            throw new InvalidOperationException(
                $"Expected array items to be present at '{currentPath}' while resolving reference path "
                    + $"'{referenceObjectPath.Canonical}' on resource '{resourceLabel}'."
            );
        }

        if (itemsNode is not JsonObject itemsSchema)
        {
            throw new InvalidOperationException(
                $"Expected array items schema to be an object at '{itemPath}' while resolving reference "
                    + $"path '{referenceObjectPath.Canonical}' on resource '{resourceLabel}'."
            );
        }

        return itemsSchema;
    }

    /// <summary>
    /// Reads the JSON Schema <c>required</c> array for the current object path.
    /// </summary>
    private static HashSet<string> GetRequiredProperties(JsonObject schema, string path)
    {
        if (!schema.TryGetPropertyValue("required", out var requiredNode) || requiredNode is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (requiredNode is not JsonArray requiredArray)
        {
            throw new InvalidOperationException($"Expected required to be an array at {path}.required.");
        }

        HashSet<string> requiredProperties = new(StringComparer.Ordinal);

        foreach (var requiredEntry in requiredArray)
        {
            if (requiredEntry is null)
            {
                throw new InvalidOperationException(
                    $"Expected required entries to be non-null at {path}.required."
                );
            }

            if (requiredEntry is not JsonValue jsonValue)
            {
                throw new InvalidOperationException(
                    $"Expected required entries to be strings at {path}.required."
                );
            }

            var name = jsonValue.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    $"Expected required entries to be non-empty at {path}.required."
                );
            }

            requiredProperties.Add(name);
        }

        return requiredProperties;
    }

    /// <summary>
    /// Reads the OpenAPI <c>x-nullable</c> extension from a schema node.
    /// </summary>
    private static bool IsXNullable(JsonObject schema, string path)
    {
        if (!schema.TryGetPropertyValue("x-nullable", out var nullableNode) || nullableNode is null)
        {
            return false;
        }

        if (nullableNode is not JsonValue jsonValue)
        {
            throw new InvalidOperationException($"Expected x-nullable to be a boolean at {path}.");
        }

        return jsonValue.GetValue<bool>();
    }
}
