// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build;

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
            .TablesInDependencyOrder.Select(table =>
            {
                var referenceGroups = BuildReferenceGroupLookup(
                    table.Table,
                    resourceModel.DocumentReferenceBindings
                );
                return RelationalModelOrdering.CanonicalizeTable(table, referenceGroups);
            })
            .OrderBy(table => SetPassHelpers.CountArrayDepth(table.JsonScope))
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
        var orderedDescriptorForeignKeyDeduplications = resourceModel
            .DescriptorForeignKeyDeduplications.Select(entry =>
                entry with
                {
                    BindingColumns = entry
                        .BindingColumns.OrderBy(column => column.Value, StringComparer.Ordinal)
                        .ToArray(),
                }
            )
            .OrderBy(entry => entry.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(entry => entry.Table.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.StorageColumn.Value, StringComparer.Ordinal)
            .ThenBy(
                entry => string.Join("|", entry.BindingColumns.Select(column => column.Value)),
                StringComparer.Ordinal
            )
            .ToArray();

        return resourceModel with
        {
            Root = rootTable,
            TablesInDependencyOrder = canonicalTables,
            DocumentReferenceBindings = orderedDocumentReferences,
            DescriptorEdgeSources = orderedDescriptorEdges,
            DescriptorForeignKeyDeduplications = orderedDescriptorForeignKeyDeduplications,
        };
    }

    /// <summary>
    /// Builds a lookup mapping column names to their reference group membership for the given table.
    /// Returns null when no bindings match the table (avoids allocations).
    /// </summary>
    private static Dictionary<string, ReferenceGroupMembership>? BuildReferenceGroupLookup(
        DbTableName table,
        IReadOnlyList<DocumentReferenceBinding> bindings
    )
    {
        Dictionary<string, ReferenceGroupMembership>? result = null;

        foreach (var binding in bindings)
        {
            if (!binding.Table.Equals(table))
            {
                continue;
            }

            var groupKey = binding.FkColumn.Value;

            result ??= new Dictionary<string, ReferenceGroupMembership>(StringComparer.Ordinal);
            result.TryAdd(groupKey, new ReferenceGroupMembership(groupKey, 0));

            for (var i = 0; i < binding.IdentityBindings.Count; i++)
            {
                var identityColumnName = binding.IdentityBindings[i].Column.Value;
                result.TryAdd(identityColumnName, new ReferenceGroupMembership(groupKey, i + 1));
            }
        }

        return result;
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
}
