// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Provides helper methods for reading normalized ApiSchema JSON nodes with consistent validation errors.
/// </summary>
internal static class RelationalModelSetSchemaHelpers
{
    /// <summary>
    /// Requires that the supplied node is a non-null <see cref="JsonObject"/>.
    /// </summary>
    /// <param name="node">The node to validate.</param>
    /// <param name="propertyName">The JSON property label used for diagnostics.</param>
    /// <returns>The validated <see cref="JsonObject"/>.</returns>
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
    /// Requires that the specified property is present on the supplied object and contains a non-empty string value.
    /// </summary>
    /// <param name="node">The object to read.</param>
    /// <param name="propertyName">The property name.</param>
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

    /// <summary>
    /// Attempts to read an optional string property, returning <see langword="null"/> when the property
    /// is absent or explicitly null.
    /// </summary>
    /// <param name="node">The object to read.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The validated string value, or <see langword="null"/>.</returns>
    internal static string? TryGetOptionalString(JsonObject node, string propertyName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var value))
        {
            return null;
        }

        if (value is null)
        {
            return null;
        }

        if (value is not JsonValue jsonValue)
        {
            throw new InvalidOperationException(
                $"Expected {propertyName} to be a string, invalid ApiSchema."
            );
        }

        var text = jsonValue.GetValue<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"Expected {propertyName} to be non-empty, invalid ApiSchema."
            );
        }

        return text;
    }

    /// <summary>
    /// Determines the resource name for a schema entry, using <c>resourceName</c> when present and
    /// falling back to the schema entry key.
    /// </summary>
    /// <param name="resourceKey">The resource schema entry key.</param>
    /// <param name="resourceSchema">The resource schema object.</param>
    /// <returns>The resolved resource name.</returns>
    internal static string GetResourceName(string resourceKey, JsonObject resourceSchema)
    {
        if (resourceSchema.TryGetPropertyValue("resourceName", out var resourceNameNode))
        {
            return resourceNameNode switch
            {
                JsonValue jsonValue => RequireNonEmpty(jsonValue.GetValue<string>(), "resourceName"),
                null => throw new InvalidOperationException(
                    "Expected resourceName to be present, invalid ApiSchema."
                ),
                _ => throw new InvalidOperationException(
                    "Expected resourceName to be a string, invalid ApiSchema."
                ),
            };
        }

        return RequireNonEmpty(resourceKey, "resourceName");
    }

    /// <summary>
    /// Requires that a value is a non-null, non-whitespace string.
    /// </summary>
    /// <param name="value">The value to validate.</param>
    /// <param name="propertyName">The label used for diagnostics.</param>
    /// <returns>The validated string.</returns>
    internal static string RequireNonEmpty(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Expected {propertyName} to be non-empty.");
        }

        return value;
    }

    /// <summary>
    /// Formats a qualified resource name for diagnostics.
    /// </summary>
    /// <param name="resource">The resource identifier.</param>
    /// <returns>A formatted label.</returns>
    internal static string FormatResource(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}:{resource.ResourceName}";
    }
}
