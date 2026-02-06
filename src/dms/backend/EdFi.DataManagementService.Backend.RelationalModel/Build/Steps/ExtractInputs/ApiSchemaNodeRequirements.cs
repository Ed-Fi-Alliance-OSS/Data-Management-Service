// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;

/// <summary>
/// Provides helper methods for reading ApiSchema JSON nodes with consistent validation errors.
/// </summary>
internal static class ApiSchemaNodeRequirements
{
    /// <summary>
    /// Ensures a node is a <see cref="JsonObject"/> and throws a schema validation exception otherwise.
    /// </summary>
    /// <param name="node">The node to validate.</param>
    /// <param name="propertyName">The schema property path used for exception messages.</param>
    /// <returns>The validated object.</returns>
    internal static JsonObject RequireObject(JsonNode? node, string propertyName)
    {
        return node switch
        {
            JsonObject jsonObject => jsonObject,
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be an object, invalid ApiSchema."
            ),
        };
    }

    /// <summary>
    /// Ensures a named property on an object is a <see cref="JsonArray"/> and throws a schema validation
    /// exception otherwise.
    /// </summary>
    /// <param name="node">The node holding the property.</param>
    /// <param name="propertyName">The property name to read.</param>
    /// <returns>The validated array.</returns>
    internal static JsonArray RequireArray(JsonObject node, string propertyName)
    {
        return node[propertyName] switch
        {
            JsonArray jsonArray => jsonArray,
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be an array, invalid ApiSchema."
            ),
        };
    }

    /// <summary>
    /// Ensures a named property on an object is a boolean value.
    /// </summary>
    internal static bool RequireBoolean(JsonObject node, string propertyName)
    {
        return node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<bool>(),
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a boolean, invalid ApiSchema."
            ),
        };
    }

    /// <summary>
    /// Reads a named optional boolean property with a default when absent.
    /// </summary>
    internal static bool TryGetOptionalBoolean(JsonObject node, string propertyName, bool defaultValue)
    {
        if (!node.TryGetPropertyValue(propertyName, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            JsonValue jsonValue => jsonValue.GetValue<bool>(),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a boolean, invalid ApiSchema."
            ),
        };
    }

    /// <summary>
    /// Ensures a named property on an object is a non-empty string and throws a schema validation
    /// exception otherwise.
    /// </summary>
    /// <param name="node">The node holding the property.</param>
    /// <param name="propertyName">The property name to read.</param>
    /// <returns>The validated string value.</returns>
    internal static string RequireString(JsonObject node, string propertyName)
    {
        var value = node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a string, invalid ApiSchema."
            ),
        };

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Expected {propertyName} to be non-empty, invalid ApiSchema."
            );
        }

        return value;
    }
}
