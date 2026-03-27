// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Resolves securable element authorization column paths from the derived relational model.
/// Given a subject resource and its securable elements, produces the chain of table joins
/// needed to reach the authorization column.
/// </summary>
internal static class SecurableElementColumnPathResolver
{
    private static readonly DbColumnName s_documentIdColumn = new("DocumentId");

    /// <summary>
    /// Resolves all securable element column paths for a single concrete resource.
    /// Returns a list of column path chains, one per securable element path.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ColumnPathStep>> ResolveAll(
        ConcreteResourceModel subjectResource,
        IReadOnlyList<ConcreteResourceModel> allResources
    )
    {
        var securableElements = subjectResource.SecurableElements;
        if (!securableElements.HasAny)
        {
            return [];
        }

        var results = new List<IReadOnlyList<ColumnPathStep>>();

        foreach (var edOrg in securableElements.EducationOrganization)
        {
            var path = ResolveEdOrgOrNamespacePath(subjectResource, edOrg.JsonPath);
            if (path is not null)
            {
                results.Add([path]);
            }
        }

        foreach (var ns in securableElements.Namespace)
        {
            var path = ResolveEdOrgOrNamespacePath(subjectResource, ns);
            if (path is not null)
            {
                results.Add([path]);
            }
        }

        ResolvePersonPaths(subjectResource, securableElements.Student, "Student", allResources, results);
        ResolvePersonPaths(subjectResource, securableElements.Contact, "Contact", allResources, results);
        ResolvePersonPaths(subjectResource, securableElements.Staff, "Staff", allResources, results);

        return results;
    }

    /// <summary>
    /// Resolves an EdOrg or Namespace securable element to a single column path step
    /// with null target (the column is directly on the root table).
    /// </summary>
    private static ColumnPathStep? ResolveEdOrgOrNamespacePath(
        ConcreteResourceModel resource,
        string securableElementPath
    )
    {
        var model = resource.RelationalModel;
        var rootTable = model.Root;

        // Match the securable element path against reference identity bindings
        foreach (var binding in model.DocumentReferenceBindings)
        {
            // Only consider bindings on the root table
            if (binding.Table != rootTable.Table)
            {
                continue;
            }

            foreach (var identityBinding in binding.IdentityBindings)
            {
                if (
                    string.Equals(
                        identityBinding.ReferenceJsonPath.Canonical,
                        securableElementPath,
                        StringComparison.Ordinal
                    )
                )
                {
                    var column = ResolveToCanonicalColumn(rootTable, identityBinding.Column);
                    return new ColumnPathStep(rootTable.Table, column, null, null);
                }
            }
        }

        // Fallback: check for a direct scalar column matching the JSON path (e.g., Namespace on root)
        foreach (var column in rootTable.Columns)
        {
            if (
                column.SourceJsonPath is not null
                && string.Equals(
                    column.SourceJsonPath.Value.Canonical,
                    securableElementPath,
                    StringComparison.Ordinal
                )
            )
            {
                var resolved = ResolveToCanonicalColumn(rootTable, column.ColumnName);
                return new ColumnPathStep(rootTable.Table, resolved, null, null);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves person (Student/Contact/Staff) securable element paths.
    /// Handles both direct and transitive references.
    /// </summary>
    private static void ResolvePersonPaths(
        ConcreteResourceModel subjectResource,
        IReadOnlyList<string> personPaths,
        string personResourceName,
        IReadOnlyList<ConcreteResourceModel> allResources,
        List<IReadOnlyList<ColumnPathStep>> results
    )
    {
        if (personPaths.Count == 0)
        {
            return;
        }

        var model = subjectResource.RelationalModel;
        var rootTable = model.Root;

        // Find all candidate paths and pick the shortest
        IReadOnlyList<ColumnPathStep>? shortestPath = null;

        foreach (var securableElementPath in personPaths)
        {
            var referencePrefix = ExtractReferencePrefix(securableElementPath);
            if (referencePrefix is null)
            {
                continue;
            }

            // Find the DocumentReferenceBinding matching the reference prefix
            var binding = FindBindingByReferencePrefix(model, rootTable.Table, referencePrefix);
            if (binding is null)
            {
                continue;
            }

            // Check if the target is the person resource directly
            if (IsPersonResource(binding.TargetResource, personResourceName))
            {
                var fkColumn = ResolveToCanonicalColumn(rootTable, binding.FkColumn);
                var targetResource = FindResource(allResources, binding.TargetResource);
                if (targetResource is not null)
                {
                    var path = new List<ColumnPathStep>
                    {
                        new(
                            rootTable.Table,
                            fkColumn,
                            targetResource.RelationalModel.Root.Table,
                            s_documentIdColumn
                        ),
                    };
                    if (shortestPath is null || path.Count < shortestPath.Count)
                    {
                        shortestPath = path;
                    }
                }
            }
            else
            {
                // Transitive: BFS from the intermediate resource to the person resource
                var chain = BfsToPersonResource(rootTable, binding, personResourceName, allResources);
                if (chain is not null && (shortestPath is null || chain.Count < shortestPath.Count))
                {
                    shortestPath = chain;
                }
            }
        }

        if (shortestPath is not null)
        {
            results.Add(shortestPath);
        }
    }

    /// <summary>
    /// BFS from an intermediate resource to find the shortest path to a person resource.
    /// </summary>
    private static IReadOnlyList<ColumnPathStep>? BfsToPersonResource(
        DbTableModel subjectRootTable,
        DocumentReferenceBinding firstHopBinding,
        string personResourceName,
        IReadOnlyList<ConcreteResourceModel> allResources
    )
    {
        var resourceLookup = BuildResourceLookup(allResources);

        // BFS state: (currentResourceName, path so far)
        var queue = new Queue<(QualifiedResourceName Resource, List<ColumnPathStep> Path)>();
        var visited = new HashSet<QualifiedResourceName>();

        // First hop from subject to intermediate
        var firstFkColumn = ResolveToCanonicalColumn(subjectRootTable, firstHopBinding.FkColumn);
        if (!resourceLookup.TryGetValue(firstHopBinding.TargetResource, out var intermediateResource))
        {
            return null;
        }

        var firstStep = new ColumnPathStep(
            subjectRootTable.Table,
            firstFkColumn,
            intermediateResource.RelationalModel.Root.Table,
            s_documentIdColumn
        );

        // Check if the intermediate IS the person resource
        if (IsPersonResource(firstHopBinding.TargetResource, personResourceName))
        {
            return [firstStep];
        }

        visited.Add(firstHopBinding.TargetResource);
        queue.Enqueue((firstHopBinding.TargetResource, new List<ColumnPathStep> { firstStep }));

        while (queue.Count > 0)
        {
            var (currentResourceName, currentPath) = queue.Dequeue();

            if (!resourceLookup.TryGetValue(currentResourceName, out var currentResource))
            {
                continue;
            }

            var currentModel = currentResource.RelationalModel;
            var currentRoot = currentModel.Root;

            foreach (var binding in currentModel.DocumentReferenceBindings)
            {
                // Only follow root-table bindings
                if (binding.Table != currentRoot.Table)
                {
                    continue;
                }

                if (visited.Contains(binding.TargetResource))
                {
                    continue;
                }

                if (!resourceLookup.TryGetValue(binding.TargetResource, out var targetResource))
                {
                    continue;
                }

                var fkColumn = ResolveToCanonicalColumn(currentRoot, binding.FkColumn);
                var step = new ColumnPathStep(
                    currentRoot.Table,
                    fkColumn,
                    targetResource.RelationalModel.Root.Table,
                    s_documentIdColumn
                );

                var newPath = new List<ColumnPathStep>(currentPath) { step };

                if (IsPersonResource(binding.TargetResource, personResourceName))
                {
                    return newPath;
                }

                visited.Add(binding.TargetResource);
                queue.Enqueue((binding.TargetResource, newPath));
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the reference object prefix from a securable element JSON path.
    /// For <c>$.schoolReference.schoolId</c>, returns <c>$.schoolReference</c>.
    /// </summary>
    private static string? ExtractReferencePrefix(string jsonPath)
    {
        var lastDot = jsonPath.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return null;
        }

        return jsonPath[..lastDot];
    }

    /// <summary>
    /// Finds a <see cref="DocumentReferenceBinding"/> on the specified table whose
    /// <see cref="DocumentReferenceBinding.ReferenceObjectPath"/> matches the reference prefix.
    /// </summary>
    private static DocumentReferenceBinding? FindBindingByReferencePrefix(
        RelationalResourceModel model,
        DbTableName table,
        string referencePrefix
    )
    {
        foreach (var binding in model.DocumentReferenceBindings)
        {
            if (
                binding.Table == table
                && string.Equals(
                    binding.ReferenceObjectPath.Canonical,
                    referencePrefix,
                    StringComparison.Ordinal
                )
            )
            {
                return binding;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether a qualified resource name corresponds to a person resource type.
    /// </summary>
    private static bool IsPersonResource(QualifiedResourceName resource, string personResourceName)
    {
        return string.Equals(resource.ResourceName, personResourceName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finds a concrete resource model by its qualified resource name.
    /// </summary>
    private static ConcreteResourceModel? FindResource(
        IReadOnlyList<ConcreteResourceModel> allResources,
        QualifiedResourceName resourceName
    )
    {
        foreach (var resource in allResources)
        {
            if (resource.ResourceKey.Resource == resourceName)
            {
                return resource;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a lookup dictionary from qualified resource name to concrete resource model.
    /// </summary>
    private static Dictionary<QualifiedResourceName, ConcreteResourceModel> BuildResourceLookup(
        IReadOnlyList<ConcreteResourceModel> allResources
    )
    {
        var lookup = new Dictionary<QualifiedResourceName, ConcreteResourceModel>(allResources.Count);
        foreach (var resource in allResources)
        {
            lookup.TryAdd(resource.ResourceKey.Resource, resource);
        }

        return lookup;
    }

    /// <summary>
    /// Resolves a column name to its canonical form through key unification.
    /// If the column is a <see cref="ColumnStorage.UnifiedAlias"/>, returns the canonical column;
    /// otherwise returns the column as-is.
    /// </summary>
    private static DbColumnName ResolveToCanonicalColumn(DbTableModel table, DbColumnName column)
    {
        foreach (var col in table.Columns)
        {
            if (col.ColumnName == column && col.Storage is ColumnStorage.UnifiedAlias alias)
            {
                return alias.CanonicalColumn;
            }
        }

        return column;
    }
}
