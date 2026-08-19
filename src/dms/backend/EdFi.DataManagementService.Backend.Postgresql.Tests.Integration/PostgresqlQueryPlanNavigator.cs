// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Navigation over an <c>EXPLAIN (FORMAT JSON)</c> plan tree: locating the root-to-scan path for a relation,
/// enumerating node types and nodes, and collecting predicate-bearing property text.
/// </summary>
/// <remarks>
/// Extracted verbatim from <c>Given_A_Postgresql_People_Auth_View_Query_Plan</c>, where these were private
/// static members and therefore unreachable from any other fixture. The assertions that interpret what the
/// navigator returns stay with the fixtures that own them; only the traversal lives here.
/// </remarks>
internal static class PostgresqlQueryPlanNavigator
{
    /// <summary>
    /// Returns the root→scan node path for the one and only scan of <paramref name="relationName"/>,
    /// failing the test when the relation is not scanned directly (i.e. the view was not inlined) or
    /// is scanned more than once. Callers are the single-arm views, where exactly one scan per base
    /// relation is the invariant; multi-arm plans use <see cref="FindAllRelationScanPaths"/>.
    /// </summary>
    public static IReadOnlyList<JsonElement> FindRelationScanPath(JsonElement plan, string relationName) =>
        FindAllRelationScanPaths(plan, relationName)
            .Should()
            .ContainSingle($"relation '{relationName}' should be scanned exactly once in the flattened plan")
            .Subject;

    /// <summary>
    /// Returns the root→scan node path for every direct scan of <paramref name="relationName"/>.
    /// Multi-arm (appendrel) plans scan the same relation once per arm, so callers assert on the
    /// full path set instead of the single path <see cref="FindRelationScanPath"/> returns.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<JsonElement>> FindAllRelationScanPaths(
        JsonElement plan,
        string relationName
    )
    {
        var paths = new List<IReadOnlyList<JsonElement>>();
        CollectRelationScanPaths(plan, relationName, [], paths);
        return paths;
    }

    private static void CollectRelationScanPaths(
        JsonElement node,
        string relationName,
        List<JsonElement> currentPath,
        List<IReadOnlyList<JsonElement>> paths
    )
    {
        currentPath.Add(node);

        if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == relationName)
        {
            paths.Add([.. currentPath]);
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                CollectRelationScanPaths(child, relationName, currentPath, paths);
            }
        }

        currentPath.RemoveAt(currentPath.Count - 1);
    }

    public static List<string> CollectNodeTypes(JsonElement plan)
    {
        var nodeTypes = new List<string>();
        Visit(plan, node => nodeTypes.Add(GetNodeType(node)));
        return nodeTypes;
    }

    public static List<JsonElement> CollectNodes(JsonElement plan)
    {
        var nodes = new List<JsonElement>();
        Visit(plan, nodes.Add);
        return nodes;
    }

    private static readonly string[] _conditionProperties =
    [
        "Filter",
        "Index Cond",
        "Hash Cond",
        "Join Filter",
        "Recheck Cond",
        "Merge Cond",
    ];

    /// <summary>
    /// Collects the text of every predicate-bearing plan property (Filter, Index Cond, Hash Cond,
    /// Join Filter, Recheck Cond, Merge Cond) attached to a single plan node.
    /// </summary>
    public static List<string> CollectOwnConditionText(JsonElement node)
    {
        var conditions = new List<string>();
        foreach (var propertyName in _conditionProperties)
        {
            if (node.TryGetProperty(propertyName, out var condition))
            {
                conditions.Add(condition.GetString() ?? string.Empty);
            }
        }

        return conditions;
    }

    /// <summary>
    /// Collects the predicate-bearing property text across the whole plan tree.
    /// </summary>
    public static List<string> CollectConditionText(JsonElement plan)
    {
        var conditions = new List<string>();
        Visit(plan, node => conditions.AddRange(CollectOwnConditionText(node)));
        return conditions;
    }

    public static string GetNodeType(JsonElement node) =>
        node.TryGetProperty("Node Type", out var nodeType)
            ? nodeType.GetString() ?? string.Empty
            : string.Empty;

    private static void Visit(JsonElement node, Action<JsonElement> visit)
    {
        visit(node);
        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                Visit(child, visit);
            }
        }
    }
}
