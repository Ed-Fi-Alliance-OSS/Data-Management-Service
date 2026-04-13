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
/// Column <c>SourceJsonPath</c> values are scope-relative: they begin with <c>$</c>
/// and their segments are interpreted relative to the owning table's <c>JsonScope</c>.
/// For example, a column with path <c>$.calendarReference.schoolId</c> in a table
/// whose <c>JsonScope</c> is <c>$.classPeriods</c> has the absolute path
/// <c>$.classPeriods.calendarReference.schoolId</c>. The intermediate absolute prefix
/// <c>$.classPeriods.calendarReference</c> is an inlined scope when no table carries
/// that scope.
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
                // Column paths are scope-relative (e.g., "$.calendarReference.schoolId").
                // Compose the absolute path by appending the relative path (sans leading "$")
                // to the table scope. For root scope "$", this is identity:
                //   "$.name" -> "$" + ".name" = "$.name"
                // For a collection scope "$.classPeriods":
                //   "$.calendarReference.schoolId" -> "$.classPeriods" + ".calendarReference.schoolId"
                //   = "$.classPeriods.calendarReference.schoolId"
                var relativePathWithoutDollar = sourcePath.Canonical[1..]; // strip leading "$"
                var absolutePath = tableScope + relativePathWithoutDollar; // compose absolute path

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
}
