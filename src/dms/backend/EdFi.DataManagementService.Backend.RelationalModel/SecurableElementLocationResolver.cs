// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Resolves an EducationOrganization / Namespace securable element JSON path to a
/// <see cref="ColumnPathStep"/> location on a concrete resource (<c>SourceTable</c>,
/// <c>SourceColumnName</c>; <c>TargetTable</c> and <c>TargetColumnName</c> are
/// <see langword="null"/>).
/// </summary>
/// <remarks>
/// <para>Single source of truth shared by:</para>
/// <list type="bullet">
///   <item><description><see cref="SetPasses.DeriveAuthorizationIndexInventoryPass"/> at DDL
///   emission time — decides which <c>(table, column)</c> the auth index keys on.</description></item>
///   <item><description><c>EdFi.DataManagementService.Backend.Plans.SecurableElementColumnPathResolver</c>
///   at runtime — decides which column the auth filter reads from.</description></item>
/// </list>
/// <para>Both call sites must agree, or an emitted auth index will sit unused while the runtime
/// query scans a different column. Selection rule when multiple candidates match a single JSON
/// path: root-table candidates beat child-table candidates (lower JSON-scope depth = lower
/// priority value); within a tie, lex-sort by source table name then source column name.</para>
/// </remarks>
public static class SecurableElementLocationResolver
{
    /// <summary>
    /// Returns every distinct <see cref="ColumnPathStep"/> on <paramref name="resource"/> whose
    /// location matches <paramref name="jsonPath"/>, deduped by <c>(SourceTable,
    /// SourceColumnName)</c>. Scans <see cref="RelationalResourceModel.DocumentReferenceBindings"/>
    /// for matching <see cref="ReferenceIdentityBinding.ReferenceJsonPath"/> entries first, then
    /// every column's <see cref="DbColumnModel.SourceJsonPath"/>.
    /// </summary>
    public static IReadOnlyList<ColumnPathStep> ResolveAllCandidates(
        ConcreteResourceModel resource,
        string jsonPath
    )
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(jsonPath);

        var model = resource.RelationalModel;
        List<ColumnPathStep> candidates = [];
        HashSet<ColumnPathStep> seen = [];

        foreach (var binding in model.DocumentReferenceBindings)
        {
            var identityBinding = binding.IdentityBindings.FirstOrDefault(ib =>
                string.Equals(ib.ReferenceJsonPath.Canonical, jsonPath, StringComparison.Ordinal)
            );
            if (identityBinding is null)
            {
                continue;
            }

            var owningTable = FindTable(model, binding.Table);
            var column = owningTable is null
                ? identityBinding.Column
                : PersonJoinPathResolver.ResolveToCanonicalColumn(owningTable, identityBinding.Column);
            var candidate = new ColumnPathStep(binding.Table, column, null, null);

            if (seen.Add(candidate))
            {
                candidates.Add(candidate);
            }
        }

        foreach (var table in model.TablesInDependencyOrder)
        {
            foreach (var column in table.Columns)
            {
                if (
                    column.SourceJsonPath is null
                    || !string.Equals(
                        column.SourceJsonPath.Value.Canonical,
                        jsonPath,
                        StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }

                var resolved = PersonJoinPathResolver.ResolveToCanonicalColumn(table, column.ColumnName);
                var candidate = new ColumnPathStep(table.Table, resolved, null, null);

                if (seen.Add(candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// Resolves <paramref name="jsonPath"/> on <paramref name="resource"/> to its single
    /// preferred <see cref="ColumnPathStep"/>. Returns <see langword="null"/> when no candidate
    /// matches.
    /// </summary>
    public static ColumnPathStep? ResolvePreferred(ConcreteResourceModel resource, string jsonPath)
    {
        var candidates = ResolveAllCandidates(resource, jsonPath);
        return candidates.Count == 0 ? null : SelectPreferred(resource, candidates);
    }

    /// <summary>
    /// Selects the single preferred candidate from a pre-collected list. Exposed for callers
    /// that wrap raw <see cref="ColumnPathStep"/>s with extra metadata and need the same
    /// priority + tiebreaker rule.
    /// </summary>
    public static ColumnPathStep? SelectPreferred(
        ConcreteResourceModel resource,
        IReadOnlyList<ColumnPathStep> candidates
    )
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderBy(c => GetCandidatePriority(resource, c))
            .ThenBy(static c => c.SourceTable.ToString(), StringComparer.Ordinal)
            .ThenBy(static c => c.SourceColumnName.Value, StringComparer.Ordinal)
            .First();
    }

    /// <summary>
    /// Priority value: root table = 0, child table = <c>JsonScope.Segments.Count + 1</c>,
    /// table not found = <see cref="int.MaxValue"/> (sorts last).
    /// </summary>
    public static int GetCandidatePriority(ConcreteResourceModel resource, ColumnPathStep candidate)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (candidate.SourceTable == resource.RelationalModel.Root.Table)
        {
            return 0;
        }

        var table = FindTable(resource.RelationalModel, candidate.SourceTable);
        return table is null ? int.MaxValue : table.JsonScope.Segments.Count + 1;
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
}
