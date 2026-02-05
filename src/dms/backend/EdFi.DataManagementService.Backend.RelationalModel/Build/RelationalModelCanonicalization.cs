// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Canonicalizes ordering-sensitive collections within derived relational models to ensure deterministic
/// output regardless of input dictionary iteration order.
/// </summary>
internal static class RelationalModelCanonicalization
{
    /// <summary>
    /// Returns a copy of the resource model with tables, bindings, and descriptor edges ordered canonically.
    /// </summary>
    internal static RelationalResourceModel CanonicalizeResourceModel(RelationalResourceModel resourceModel)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);

        var canonicalTables = resourceModel
            .TablesInDependencyOrder.Select(RelationalModelOrdering.CanonicalizeTable)
            .OrderBy(table => CountArrayDepth(table.JsonScope))
            .ThenBy(table => table.JsonScope.Canonical, StringComparer.Ordinal)
            .ThenBy(table => table.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(table => table.Table.Name, StringComparer.Ordinal)
            .ToArray();

        var rootTable = canonicalTables.FirstOrDefault(table =>
            string.Equals(table.JsonScope.Canonical, "$", StringComparison.Ordinal)
        );

        if (rootTable is null)
        {
            throw new InvalidOperationException("Root table scope '$' was not found.");
        }

        var orderedDocumentReferences = resourceModel
            .DocumentReferenceBindings.OrderBy(
                binding => binding.ReferenceObjectPath.Canonical,
                StringComparer.Ordinal
            )
            .ThenBy(binding => binding.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(binding => binding.Table.Name, StringComparer.Ordinal)
            .ThenBy(binding => binding.FkColumn.Value, StringComparer.Ordinal)
            .ThenBy(binding => binding.TargetResource.ProjectName, StringComparer.Ordinal)
            .ThenBy(binding => binding.TargetResource.ResourceName, StringComparer.Ordinal)
            .ThenBy(binding => binding.IsIdentityComponent)
            .ToArray();

        var orderedDescriptorEdges = resourceModel
            .DescriptorEdgeSources.OrderBy(edge => edge.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.Table.Name, StringComparer.Ordinal)
            .ThenBy(edge => edge.DescriptorValuePath.Canonical, StringComparer.Ordinal)
            .ThenBy(edge => edge.FkColumn.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.DescriptorResource.ProjectName, StringComparer.Ordinal)
            .ThenBy(edge => edge.DescriptorResource.ResourceName, StringComparer.Ordinal)
            .ThenBy(edge => edge.IsIdentityComponent)
            .ToArray();

        return resourceModel with
        {
            Root = rootTable,
            TablesInDependencyOrder = canonicalTables,
            DocumentReferenceBindings = orderedDocumentReferences,
            DescriptorEdgeSources = orderedDescriptorEdges,
        };
    }

    /// <summary>
    /// Returns a copy of the extension sites list ordered canonically.
    /// </summary>
    internal static IReadOnlyList<ExtensionSite> CanonicalizeExtensionSites(
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
        if (extensionSites.Count == 0)
        {
            return Array.Empty<ExtensionSite>();
        }

        return extensionSites
            .Select(site =>
                site with
                {
                    ProjectKeys = site.ProjectKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
                }
            )
            .OrderBy(site => site.OwningScope.Canonical, StringComparer.Ordinal)
            .ThenBy(site => site.ExtensionPath.Canonical, StringComparer.Ordinal)
            .ThenBy(site => string.Join("|", site.ProjectKeys), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Counts the number of array wildcard segments in the scope, used for depth-first ordering.
    /// </summary>
    private static int CountArrayDepth(JsonPathExpression scope)
    {
        var depth = 0;

        foreach (var segment in scope.Segments)
        {
            if (segment is JsonPathSegment.AnyArrayElement)
            {
                depth++;
            }
        }

        return depth;
    }
}
