// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Removes non-root-scope entries from <c>securableElements.Namespace</c> on a loaded ApiSchema
/// node. Namespace-based authorization applies only to Namespace fields on the resource root,
/// but older ApiSchema artifacts also emitted collection-scoped paths (canonical paths containing
/// <c>[*]</c>) and resource-extension paths (canonical paths containing <c>._ext.</c>, covering
/// both <c>$._ext.…</c> and <c>$.collection[*]._ext.…</c> forms). Filtering at load keeps those
/// stale entries out of everything derived from the schema — securable-element extraction,
/// authorization index emission, and the effective schema hash — while leaving all other
/// metadata (reference metadata, equality constraints, array uniqueness constraints, JSON
/// schema) untouched. The operation is idempotent.
/// </summary>
internal static class NamespaceSecurableElementsFilter
{
    /// <summary>
    /// Mutates <paramref name="apiSchemaRootNode"/> in place, removing every
    /// <c>securableElements.Namespace</c> string entry that contains <c>[*]</c> or <c>._ext.</c>.
    /// Missing or malformed sections are left for downstream schema validation to report.
    /// </summary>
    public static void RemoveNonRootScopePaths(JsonNode apiSchemaRootNode)
    {
        if (apiSchemaRootNode["projectSchema"]?["resourceSchemas"] is not JsonObject resourceSchemas)
        {
            return;
        }

        foreach (var (_, resourceSchema) in resourceSchemas)
        {
            if (resourceSchema?["securableElements"]?["Namespace"] is not JsonArray namespacePaths)
            {
                continue;
            }

            for (int i = namespacePaths.Count - 1; i >= 0; i--)
            {
                if (
                    namespacePaths[i] is JsonValue pathValue
                    && pathValue.TryGetValue<string>(out var path)
                    && (
                        path.Contains("[*]", StringComparison.Ordinal)
                        || path.Contains("._ext.", StringComparison.Ordinal)
                    )
                )
                {
                    namespacePaths.RemoveAt(i);
                }
            }
        }
    }
}
