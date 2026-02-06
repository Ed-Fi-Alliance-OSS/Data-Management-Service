// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel.Schema;

/// <summary>
/// The high-level schema shape needed by the relational model derivation traversal.
/// </summary>
internal enum SchemaKind
{
    Object,
    Array,
    Scalar,
}

/// <summary>
/// Determines how a JSON schema node should be interpreted for traversal when deriving relational tables and
/// columns.
///
/// The Ed-Fi <c>jsonSchemaForInsert</c> payload is expected to be fully dereferenced and commonly includes
/// explicit <c>type</c> keywords, but the implementation is tolerant of omitted <c>type</c> when other
/// structure keywords (like <c>properties</c> or <c>items</c>) are present.
/// </summary>
internal static class JsonSchemaTraversalConventions
{
    /// <summary>
    /// Classifies a schema node as object/array/scalar, enforcing the root-must-be-object constraint when
    /// requested.
    /// </summary>
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

    /// <summary>
    /// Reads the schema <c>type</c> keyword when present and validates it is a string.
    /// </summary>
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

    /// <summary>
    /// Throws a consistent error when the root schema is not an object.
    /// </summary>
    private static void ThrowRootSchemaMustBeObject(string? path)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(path) ? "$" : path;

        throw new InvalidOperationException($"Root schema must be an object at {resolvedPath}.");
    }

    /// <summary>
    /// Builds a type-validation error message, optionally including the <c>.type</c> suffix for more precise
    /// diagnostics.
    /// </summary>
    private static string BuildTypeErrorMessage(string? path, bool includeTypePathInErrors)
    {
        if (includeTypePathInErrors && !string.IsNullOrWhiteSpace(path))
        {
            return $"Expected schema type to be a string at {path}.type.";
        }

        return "Expected schema type to be a string.";
    }
}
