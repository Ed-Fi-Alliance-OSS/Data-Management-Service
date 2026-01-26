// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

internal enum SchemaKind
{
    Object,
    Array,
    Scalar,
}

internal static class JsonSchemaTraversalConventions
{
    internal static SchemaKind DetermineSchemaKind(
        JsonObject schema,
        string? path = null,
        bool isRoot = false,
        bool includeTypePathInErrors = false
    )
    {
        ArgumentNullException.ThrowIfNull(schema);

        var schemaType = GetSchemaType(schema, path, includeTypePathInErrors);

        if (schemaType is not null)
        {
            if (isRoot && !string.Equals(schemaType, "object", StringComparison.Ordinal))
            {
                ThrowRootSchemaMustBeObject(path);
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
                ThrowRootSchemaMustBeObject(path);
            }

            return SchemaKind.Array;
        }

        if (schema.ContainsKey("properties") || isRoot)
        {
            return SchemaKind.Object;
        }

        return SchemaKind.Scalar;
    }

    private static string? GetSchemaType(JsonObject schema, string? path, bool includeTypePathInErrors)
    {
        if (!schema.TryGetPropertyValue("type", out var typeNode) || typeNode is null)
        {
            return null;
        }

        if (typeNode is not JsonValue jsonValue)
        {
            throw new InvalidOperationException(BuildTypeErrorMessage(path, includeTypePathInErrors));
        }

        if (!jsonValue.TryGetValue<string>(out var schemaType))
        {
            throw new InvalidOperationException(BuildTypeErrorMessage(path, includeTypePathInErrors));
        }

        return schemaType;
    }

    private static void ThrowRootSchemaMustBeObject(string? path)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(path) ? "$" : path;

        throw new InvalidOperationException($"Root schema must be an object at {resolvedPath}.");
    }

    private static string BuildTypeErrorMessage(string? path, bool includeTypePathInErrors)
    {
        if (includeTypePathInErrors && !string.IsNullOrWhiteSpace(path))
        {
            return $"Expected schema type to be a string at {path}.type.";
        }

        return "Expected schema type to be a string.";
    }
}
