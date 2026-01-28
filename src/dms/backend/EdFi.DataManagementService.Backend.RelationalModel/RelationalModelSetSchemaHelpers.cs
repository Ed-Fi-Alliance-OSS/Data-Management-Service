// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

internal static class RelationalModelSetSchemaHelpers
{
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

    internal static string RequireNonEmpty(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Expected {propertyName} to be non-empty.");
        }

        return value;
    }

    internal static string FormatResource(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}:{resource.ResourceName}";
    }
}
