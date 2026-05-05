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
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");

    /// <summary>
    /// Resolves all securable element column paths for a single concrete resource.
    /// Returns a list of resolved paths, each carrying the element kind and the column path chain.
    /// </summary>
    public static IReadOnlyList<ResolvedSecurableElementPath> ResolveAll(
        ConcreteResourceModel subjectResource,
        IReadOnlyList<ConcreteResourceModel> allResources
    )
    {
        var securableElements = subjectResource.SecurableElements;
        if (!securableElements.HasAny)
        {
            return [];
        }

        var results = new List<ResolvedSecurableElementPath>();
        var unresolvedPaths = new List<string>();
        var skippedArrayNestedPaths = new List<string>();

        foreach (var jsonPath in securableElements.EducationOrganization.Select(e => e.JsonPath))
        {
            var path = ResolveEdOrgOrNamespacePath(subjectResource, jsonPath);
            if (path is not null)
            {
                results.Add(
                    new ResolvedSecurableElementPath(SecurableElementKind.EducationOrganization, [path])
                );
            }
            else
            {
                unresolvedPaths.Add(jsonPath);
            }
        }

        foreach (string ns in securableElements.Namespace)
        {
            var path = ResolveEdOrgOrNamespacePath(subjectResource, ns);
            if (path is not null)
            {
                results.Add(new ResolvedSecurableElementPath(SecurableElementKind.Namespace, [path]));
            }
            else
            {
                unresolvedPaths.Add(ns);
            }
        }

        var resourceLookup = BuildResourceLookup(allResources);

        ResolvePersonPaths(
            subjectResource,
            securableElements.Student,
            "Student",
            SecurableElementKind.Student,
            resourceLookup,
            results,
            unresolvedPaths,
            skippedArrayNestedPaths
        );
        ResolvePersonPaths(
            subjectResource,
            securableElements.Contact,
            "Contact",
            SecurableElementKind.Contact,
            resourceLookup,
            results,
            unresolvedPaths,
            skippedArrayNestedPaths
        );
        ResolvePersonPaths(
            subjectResource,
            securableElements.Staff,
            "Staff",
            SecurableElementKind.Staff,
            resourceLookup,
            results,
            unresolvedPaths,
            skippedArrayNestedPaths
        );

        if (unresolvedPaths.Count > 0)
        {
            var resource = subjectResource.RelationalModel.Resource;
            throw new InvalidOperationException(
                $"Failed to resolve securable element column paths for resource "
                    + $"'{resource.ProjectName}.{resource.ResourceName}': "
                    + $"unresolved paths: {string.Join(", ", unresolvedPaths)}"
            );
        }

        if (results.Count == 0 && skippedArrayNestedPaths.Count > 0)
        {
            var resource = subjectResource.RelationalModel.Resource;
            throw new InvalidOperationException(
                $"Failed to resolve securable element column paths for resource "
                    + $"'{resource.ProjectName}.{resource.ResourceName}': "
                    + $"all paths require unsupported child-table traversal (array-nested): "
                    + $"{string.Join(", ", skippedArrayNestedPaths)}"
            );
        }

        return results;
    }

    /// <summary>
    /// Resolves an EdOrg or Namespace securable element to a single column path step
    /// (<see cref="ColumnPathStep.TargetTable"/> and <see cref="ColumnPathStep.TargetColumnName"/>
    /// are <see langword="null"/> — the auth value lives at <c>SourceTable.SourceColumnName</c>).
    /// Walks every <see cref="DocumentReferenceBinding"/> (root or child) for an identity binding
    /// whose <c>ReferenceJsonPath</c> matches; falls back to scanning every table for a scalar
    /// column whose <c>SourceJsonPath</c> matches. Mirrors
    /// <c>DeriveAuthorizationIndexInventoryPass.ResolveSecurableElementLocation</c> so the
    /// runtime mapping and the DDL index pass agree on which (table, column) carries the value
    /// for both root-level and array-nested paths.
    /// </summary>
    private static ColumnPathStep? ResolveEdOrgOrNamespacePath(
        ConcreteResourceModel resource,
        string securableElementPath
    )
    {
        var model = resource.RelationalModel;

        foreach (var binding in model.DocumentReferenceBindings)
        {
            var identityBinding = binding.IdentityBindings.FirstOrDefault(ib =>
                string.Equals(ib.ReferenceJsonPath.Canonical, securableElementPath, StringComparison.Ordinal)
            );

            if (identityBinding is null)
            {
                continue;
            }

            var owningTable = FindTable(model, binding.Table);
            var column = owningTable is null
                ? identityBinding.Column
                : ResolveToCanonicalColumn(owningTable, identityBinding.Column);
            return new ColumnPathStep(binding.Table, column, null, null);
        }

        foreach (var table in model.TablesInDependencyOrder)
        {
            foreach (var column in table.Columns)
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
                    var resolved = ResolveToCanonicalColumn(table, column.ColumnName);
                    return new ColumnPathStep(table.Table, resolved, null, null);
                }
            }
        }

        return null;
    }

    private static DbTableModel? FindTable(RelationalResourceModel model, DbTableName tableName)
    {
        foreach (var t in model.TablesInDependencyOrder)
        {
            if (t.Table == tableName)
            {
                return t;
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
        SecurableElementKind kind,
        Dictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        List<ResolvedSecurableElementPath> results,
        List<string> unresolvedPaths,
        List<string> skippedArrayNestedPaths
    )
    {
        if (personPaths.Count == 0)
        {
            return;
        }

        // Filter out array-nested paths; the resolver only handles root-table paths.
        var rootLevelPaths = new List<string>();
        foreach (var p in personPaths)
        {
            if (IsArrayNestedPath(p))
            {
                skippedArrayNestedPaths.Add(p);
            }
            else
            {
                rootLevelPaths.Add(p);
            }
        }

        if (rootLevelPaths.Count == 0)
        {
            return;
        }

        var model = subjectResource.RelationalModel;
        var rootTable = model.Root;

        // Find all candidate paths and pick the shortest
        IReadOnlyList<ColumnPathStep>? shortestPath = null;

        foreach (var securableElementPath in rootLevelPaths)
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
                if (resourceLookup.TryGetValue(binding.TargetResource, out var targetResource))
                {
                    var path = new List<ColumnPathStep>
                    {
                        new(
                            rootTable.Table,
                            fkColumn,
                            targetResource.RelationalModel.Root.Table,
                            _documentIdColumn
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
                var chain = BfsToPersonResource(
                    subjectResource,
                    rootTable,
                    binding,
                    personResourceName,
                    resourceLookup
                );
                if (chain is not null && (shortestPath is null || chain.Count < shortestPath.Count))
                {
                    shortestPath = chain;
                }
            }
        }

        if (shortestPath is not null)
        {
            results.Add(new ResolvedSecurableElementPath(kind, shortestPath));
        }
        else if (!IsPersonResource(subjectResource.RelationalModel.Resource, personResourceName))
        {
            // Only flag as unresolved when the subject resource is NOT the person resource
            // itself. Person resources (e.g., Contact with $.contactUniqueId) don't need
            // a join chain — their own identity column is the authorization anchor.
            unresolvedPaths.AddRange(rootLevelPaths);
        }
    }

    /// <summary>
    /// BFS from an intermediate resource to find the shortest path to a person resource.
    /// </summary>
    private static IReadOnlyList<ColumnPathStep>? BfsToPersonResource(
        ConcreteResourceModel subjectResource,
        DbTableModel subjectRootTable,
        DocumentReferenceBinding firstHopBinding,
        string personResourceName,
        Dictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup
    )
    {
        // BFS state: (currentResourceName, path so far)
        var queue = new Queue<(QualifiedResourceName Resource, List<ColumnPathStep> Path)>();
        var visited = new HashSet<QualifiedResourceName> { subjectResource.RelationalModel.Resource };

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
            _documentIdColumn
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
                    _documentIdColumn
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
        int lastDot = jsonPath.LastIndexOf('.');
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
    /// Checks whether a qualified resource name corresponds to a core Ed-Fi person resource type.
    /// Both <c>ProjectName</c> and <c>ResourceName</c> must match to avoid homograph collisions.
    /// </summary>
    private static bool IsPersonResource(QualifiedResourceName resource, string personResourceName)
    {
        return string.Equals(resource.ProjectName, "Ed-Fi", StringComparison.Ordinal)
            && string.Equals(resource.ResourceName, personResourceName, StringComparison.Ordinal);
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

    /// <summary>
    /// Returns <c>true</c> if the JSON path contains an array wildcard (<c>[*]</c>),
    /// indicating it traverses into a child table. The resolver currently only
    /// supports root-table paths, so array-nested paths are skipped.
    /// </summary>
    private static bool IsArrayNestedPath(string jsonPath)
    {
        return jsonPath.Contains("[*]", StringComparison.Ordinal);
    }
}
