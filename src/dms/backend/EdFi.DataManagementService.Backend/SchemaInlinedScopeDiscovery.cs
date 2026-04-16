// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Discovers inlined scopes for a <see cref="ResourceWritePlan"/> by scanning
/// column <c>SourceJsonPath</c> values for sub-scope prefixes that are not themselves
/// table-backed. Counterpart to Core's <c>ContentTypeScopeDiscovery</c> for the
/// no-profile execution path, where no profile content-type tree exists.
/// </summary>
/// <remarks>
/// <para>
/// For each column in each table, walks the canonical JSON path and identifies
/// every intermediate scope prefix (between the table's own <c>JsonScope</c> and
/// the column's leaf member). Any prefix that is not already a <c>JsonScope</c> of
/// some table in the plan is an inlined scope.
/// </para>
/// <para>
/// Column <c>SourceJsonPath</c> values are not consistently encoded the same way for
/// every non-root table. Root-backed tables use absolute paths (for example
/// <c>$.calendarReference.schoolId</c>), while non-root tables may use either an
/// already-absolute path or a scope-relative path rooted at <c>$</c>. For example, a
/// column in table scope <c>$.classPeriods[*]</c> may appear as either
/// <c>$.classPeriods[*].calendarReference.schoolId</c> or
/// <c>$.calendarReference.schoolId</c>. Discovery normalizes both forms to the same
/// absolute path before deriving intermediate inlined scopes.
/// </para>
/// <para>
/// Scope kind is always <see cref="ScopeKind.NonCollection"/> for inlined scopes;
/// collection-ness is denoted by the <c>[*]</c> segment in the parent path, not
/// by the inlined scope itself.
/// </para>
/// </remarks>
internal static class SchemaInlinedScopeDiscovery
{
    public static IReadOnlyList<(string JsonScope, ScopeKind Kind)> Discover(ResourceWritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var tableScopes = new HashSet<string>(
            plan.TablePlansInDependencyOrder.Select(tp => tp.TableModel.JsonScope.Canonical),
            StringComparer.Ordinal
        );

        var discovered = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var tableModel in plan.TablePlansInDependencyOrder.Select(tp => tp.TableModel))
        {
            var tableScope = tableModel.JsonScope.Canonical;
            var prefix = tableScope + ".";

            foreach (
                var sourcePath in tableModel
                    .Columns.Select(c => c.SourceJsonPath)
                    .Where(p => p.HasValue)
                    .Select(p => p!.Value)
            )
            {
                var absolutePath = ResolveAbsolutePath(tableScope, sourcePath.Canonical);

                // The absolute path always starts with "tableScope." since we composed it above.
                if (!absolutePath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var suffix = absolutePath[prefix.Length..]; // e.g., "calendarReference.schoolId"
                var segments = suffix.Split('.');

                // For each prefix length from 1 to segments.Length-1 (excluding the leaf):
                // build intermediate scopes and collect those not already table-backed.
                for (var len = 1; len < segments.Length; len++)
                {
                    var candidate = prefix + string.Join(".", segments[..len]);
                    if (!tableScopes.Contains(candidate))
                    {
                        discovered.Add(candidate);
                    }
                }
            }
        }

        return discovered.Select(scope => (JsonScope: scope, Kind: ScopeKind.NonCollection)).ToList();
    }

    private static string ResolveAbsolutePath(string tableScope, string sourcePath)
    {
        if (tableScope == "$")
        {
            return sourcePath;
        }

        var tableScopePrefix = tableScope + ".";

        if (sourcePath == tableScope || sourcePath.StartsWith(tableScopePrefix, StringComparison.Ordinal))
        {
            return sourcePath;
        }

        return sourcePath.StartsWith("$", StringComparison.Ordinal)
            ? tableScope + sourcePath[1..]
            : sourcePath;
    }
}
