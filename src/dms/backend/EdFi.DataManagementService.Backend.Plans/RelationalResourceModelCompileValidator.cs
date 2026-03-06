// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Validates baseline structural invariants shared by relational plan compilers.
/// </summary>
internal static class RelationalResourceModelCompileValidator
{
    public static DbTableModel ResolveRootScopeTableModelOrThrow(
        RelationalResourceModel resourceModel,
        string planKind
    )
    {
        ArgumentNullException.ThrowIfNull(resourceModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(planKind);

        if (resourceModel.TablesInDependencyOrder.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for resource '{GetResourceDisplayName(resourceModel)}': no tables were found in dependency order."
            );
        }

        if (!IsRootJsonScope(resourceModel.Root.JsonScope))
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for resource '{GetResourceDisplayName(resourceModel)}': resourceModel.Root must have JsonScope '$', but was '{resourceModel.Root.JsonScope.Canonical}'."
            );
        }

        var rootScopeTables = resourceModel
            .TablesInDependencyOrder.Where(static tableModel => IsRootJsonScope(tableModel.JsonScope))
            .ToArray();

        if (rootScopeTables.Length != 1)
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for resource '{GetResourceDisplayName(resourceModel)}': expected exactly one root-scope table (JsonScope '$') in TablesInDependencyOrder, but found {rootScopeTables.Length}."
            );
        }

        var rootScopeTable = rootScopeTables[0];

        if (!rootScopeTable.Table.Equals(resourceModel.Root.Table))
        {
            throw new InvalidOperationException(
                $"Cannot compile {planKind} for resource '{GetResourceDisplayName(resourceModel)}': root-scope table '{rootScopeTable.Table}' does not match resourceModel.Root table '{resourceModel.Root.Table}'."
            );
        }

        return rootScopeTable;
    }

    private static bool IsRootJsonScope(JsonPathExpression jsonScope)
    {
        return jsonScope.Canonical == "$" && jsonScope.Segments.Count == 0;
    }

    private static string GetResourceDisplayName(RelationalResourceModel resourceModel)
    {
        return $"{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}";
    }
}
