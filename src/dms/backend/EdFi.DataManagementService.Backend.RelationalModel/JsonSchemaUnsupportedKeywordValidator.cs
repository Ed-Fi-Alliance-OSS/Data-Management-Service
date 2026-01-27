// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

internal static class JsonSchemaUnsupportedKeywordValidator
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

    internal static void Validate(JsonObject schema, string path)
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
}
