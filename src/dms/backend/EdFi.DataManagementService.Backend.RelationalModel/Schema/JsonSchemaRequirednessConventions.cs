// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel.Schema;

/// <summary>
/// Reads JSON Schema requiredness keywords used by relational nullability derivation.
/// </summary>
internal static class JsonSchemaRequirednessConventions
{
    /// <summary>
    /// Reads and validates the <c>required</c> array on an object schema.
    /// </summary>
    internal static HashSet<string> GetRequiredProperties(
        JsonObject schema,
        IReadOnlyList<JsonPathSegment> pathSegments
    )
    {
        var path = JsonPathExpressionCompiler.FromSegments(pathSegments).Canonical;

        return GetRequiredProperties(schema, path);
    }

    /// <summary>
    /// Reads and validates the <c>required</c> array on an object schema.
    /// </summary>
    internal static HashSet<string> GetRequiredProperties(JsonObject schema, string path)
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

            if (requiredEntry is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var name))
            {
                throw new InvalidOperationException(
                    $"Expected required entries to be strings at {path}.required."
                );
            }

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
    /// Reads <c>x-nullable</c> as an override for JSON Schema requiredness.
    /// </summary>
    internal static bool IsXNullable(JsonObject schema, string path)
    {
        if (!schema.TryGetPropertyValue("x-nullable", out var nullableNode) || nullableNode is null)
        {
            return false;
        }

        if (nullableNode is not JsonValue jsonValue || !jsonValue.TryGetValue<bool>(out var isNullable))
        {
            throw new InvalidOperationException($"Expected x-nullable to be a boolean at {path}.");
        }

        return isNullable;
    }
}
