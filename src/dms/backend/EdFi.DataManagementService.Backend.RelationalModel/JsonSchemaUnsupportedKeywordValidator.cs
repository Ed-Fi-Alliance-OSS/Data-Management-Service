// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Validates that <c>jsonSchemaForInsert</c> is fully dereferenced/expanded and does not contain schema
/// constructs the relational builder does not support.
/// </summary>
/// <remarks>
/// This validator is called at each schema node that the traversal visits. Nodes under
/// <c>additionalProperties</c> are intentionally not visited (and therefore not validated) because
/// <c>additionalProperties</c> is treated as "ignore/prune" for persistence.
/// </remarks>
internal static class JsonSchemaUnsupportedKeywordValidator
{
    private static readonly IReadOnlyList<string> _unsupportedKeywords =
    [
        "$ref",
        "oneOf",
        "anyOf",
        "allOf",
        "enum",
        "patternProperties",
    ];

    /// <summary>
    /// Throws when an unsupported keyword is present on <paramref name="schema"/>, including the offending
    /// keyword and the schema traversal path in the error message.
    /// </summary>
    /// <param name="schema">The schema node to validate.</param>
    /// <param name="path">The schema traversal path used for error reporting.</param>
    internal static void Validate(JsonObject schema, string path)
    {
        foreach (var keyword in _unsupportedKeywords)
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
