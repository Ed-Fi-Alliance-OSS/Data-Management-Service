// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Normalizes ordering of tables, columns, constraints, and related metadata to ensure deterministic
/// relational model output regardless of source enumeration order.
/// </summary>
public sealed class CanonicalizeOrderingStep : IRelationalModelBuilderStep
{
    /// <summary>
    /// Applies canonical ordering rules to the current <see cref="RelationalModelBuilderContext"/>,
    /// including table ordering, column and constraint ordering, document reference bindings,
    /// descriptor edges, and extension sites.
    /// </summary>
    /// <param name="context">The relational model builder context to canonicalize.</param>
    public void Execute(RelationalModelBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resourceModel =
            context.ResourceModel
            ?? throw new InvalidOperationException(
                "Resource model must be provided before canonicalizing ordering."
            );

        var canonicalTables = resourceModel
            .TablesInReadDependencyOrder.Select(RelationalModelOrdering.CanonicalizeTable)
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

        var orderedExtensionSites = CanonicalizeExtensionSites(context.ExtensionSites);

        context.ResourceModel = resourceModel with
        {
            Root = rootTable,
            TablesInReadDependencyOrder = canonicalTables,
            TablesInWriteDependencyOrder = canonicalTables,
            DocumentReferenceBindings = orderedDocumentReferences,
            DescriptorEdgeSources = orderedDescriptorEdges,
        };

        context.ExtensionSites.Clear();
        context.ExtensionSites.AddRange(orderedExtensionSites);
    }

    /// <summary>
    /// Counts the number of array segments within a JSON path, which is used to ensure
    /// parent scopes are ordered ahead of deeper array scopes.
    /// </summary>
    /// <param name="scope">The JSON scope to inspect.</param>
    /// <returns>The number of array-depth segments in the scope.</returns>
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

    /// <summary>
    /// Produces a stable ordering for extension sites by normalizing project key ordering and then
    /// ordering by owning scope, extension path, and project keys.
    /// </summary>
    /// <param name="extensionSites">The extension sites to canonicalize.</param>
    /// <returns>An ordered collection of extension sites.</returns>
    private static IReadOnlyList<ExtensionSite> CanonicalizeExtensionSites(
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
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
