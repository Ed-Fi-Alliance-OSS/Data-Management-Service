// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Core.Profile;

internal interface IEffectiveSchemaRequiredMembersProvider
{
    IReadOnlyDictionary<string, IReadOnlyList<string>> Resolve(
        ResourceWritePlan writePlan,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    );
}

internal sealed class WritePlanEffectiveSchemaRequiredMembersProvider
    : IEffectiveSchemaRequiredMembersProvider
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Resolve(
        ResourceWritePlan writePlan,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(scopeCatalog);

        var requiredMembersByScope = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var tablePlansByJsonScope = writePlan.TablePlansInDependencyOrder.ToDictionary(
            tablePlan => tablePlan.TableModel.JsonScope.Canonical,
            StringComparer.Ordinal
        );
        var scopesByJsonScope = scopeCatalog.ToDictionary(
            scopeDescriptor => scopeDescriptor.JsonScope,
            StringComparer.Ordinal
        );

        foreach (var scopeDescriptor in scopeCatalog)
        {
            HashSet<string> requiredMembers = new(StringComparer.Ordinal);

            if (tablePlansByJsonScope.TryGetValue(scopeDescriptor.JsonScope, out var tablePlan))
            {
                AddRequiredMembersForOwningTable(
                    requiredMembers,
                    scopeDescriptor.JsonScope,
                    tablePlan,
                    writePlan
                );
            }
            else
            {
                var ancestorTablePlan = FindClosestTableAncestor(
                    scopeDescriptor,
                    tablePlansByJsonScope,
                    scopesByJsonScope
                );

                if (ancestorTablePlan is not null)
                {
                    AddRequiredMembersForOwningTable(
                        requiredMembers,
                        scopeDescriptor.JsonScope,
                        ancestorTablePlan,
                        writePlan
                    );
                }
            }

            if (requiredMembers.Count > 0)
            {
                requiredMembersByScope[scopeDescriptor.JsonScope] = [.. requiredMembers];
            }
        }

        return requiredMembersByScope;
    }

    private static void AddRequiredMembersForOwningTable(
        HashSet<string> requiredMembers,
        string scopeJsonScope,
        TableWritePlan tablePlan,
        ResourceWritePlan writePlan
    )
    {
        foreach (var columnBinding in tablePlan.ColumnBindings)
        {
            if (columnBinding.Column.IsNullable)
            {
                continue;
            }

            if (TryGetAbsolutePath(columnBinding, tablePlan, writePlan, out var absolutePath))
            {
                AddRequiredMemberIfUnderScope(requiredMembers, scopeJsonScope, absolutePath);
            }

            switch (columnBinding.Source)
            {
                case WriteValueSource.DocumentReference(var bindingIndex)
                    when string.Equals(
                        writePlan.Model.DocumentReferenceBindings[bindingIndex].ReferenceObjectPath.Canonical,
                        scopeJsonScope,
                        StringComparison.Ordinal
                    ):
                    foreach (
                        var identityBinding in writePlan
                            .Model
                            .DocumentReferenceBindings[bindingIndex]
                            .IdentityBindings
                    )
                    {
                        AddRequiredMember(requiredMembers, identityBinding.ReferenceJsonPath.Canonical);
                    }
                    break;

                case WriteValueSource.ReferenceDerived(var referenceSource)
                    when string.Equals(
                        referenceSource.ReferenceObjectPath.Canonical,
                        scopeJsonScope,
                        StringComparison.Ordinal
                    ):
                    AddRequiredMember(requiredMembers, referenceSource.ReferenceJsonPath.Canonical);
                    break;
            }
        }
    }

    private static bool TryGetAbsolutePath(
        WriteColumnBinding columnBinding,
        TableWritePlan tablePlan,
        ResourceWritePlan writePlan,
        out string absolutePath
    )
    {
        absolutePath = columnBinding.Source switch
        {
            WriteValueSource.Scalar(var relativePath, _) => ToAbsolutePath(
                tablePlan.TableModel.JsonScope,
                relativePath
            ),
            WriteValueSource.DescriptorReference(_, var relativePath, _) => ToAbsolutePath(
                tablePlan.TableModel.JsonScope,
                relativePath
            ),
            WriteValueSource.DocumentReference(var bindingIndex) => writePlan
                .Model
                .DocumentReferenceBindings[bindingIndex]
                .ReferenceObjectPath
                .Canonical,
            WriteValueSource.ReferenceDerived(var referenceSource) => referenceSource
                .ReferenceObjectPath
                .Canonical,
            _ => string.Empty,
        };

        return !string.IsNullOrWhiteSpace(absolutePath);
    }

    private static TableWritePlan? FindClosestTableAncestor(
        CompiledScopeDescriptor scopeDescriptor,
        IReadOnlyDictionary<string, TableWritePlan> tablePlansByJsonScope,
        IReadOnlyDictionary<string, CompiledScopeDescriptor> scopesByJsonScope
    )
    {
        var currentParentJsonScope = scopeDescriptor.ImmediateParentJsonScope;

        while (currentParentJsonScope is not null)
        {
            if (tablePlansByJsonScope.TryGetValue(currentParentJsonScope, out var tablePlan))
            {
                return tablePlan;
            }

            currentParentJsonScope = scopesByJsonScope.TryGetValue(
                currentParentJsonScope,
                out var parentScope
            )
                ? parentScope.ImmediateParentJsonScope
                : null;
        }

        return null;
    }

    private static void AddRequiredMemberIfUnderScope(
        HashSet<string> requiredMembers,
        string scopeJsonScope,
        string absolutePath
    )
    {
        if (!TryDeriveRelativePath(scopeJsonScope, absolutePath, out var relativePath))
        {
            return;
        }

        AddRequiredMember(requiredMembers, relativePath);
    }

    private static void AddRequiredMember(HashSet<string> requiredMembers, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var normalizedRelativePath = relativePath.StartsWith("$.", StringComparison.Ordinal)
            ? relativePath[2..]
            : relativePath;

        if (
            string.IsNullOrWhiteSpace(normalizedRelativePath)
            || normalizedRelativePath.Contains("[*]", StringComparison.Ordinal)
        )
        {
            return;
        }

        var directMember = MemberPathVisibility.ExtractTopLevelMember(normalizedRelativePath);

        if (!string.IsNullOrWhiteSpace(directMember))
        {
            requiredMembers.Add(directMember);
        }
    }

    private static bool TryDeriveRelativePath(
        string scopeJsonScope,
        string absolutePath,
        out string relativePath
    )
    {
        if (string.Equals(scopeJsonScope, absolutePath, StringComparison.Ordinal))
        {
            relativePath = string.Empty;
            return true;
        }

        var expectedPrefix = scopeJsonScope == "$" ? "$." : scopeJsonScope + ".";

        if (!absolutePath.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = absolutePath[expectedPrefix.Length..];
        return true;
    }

    private static string ToAbsolutePath(JsonPathExpression tableScope, JsonPathExpression relativePath)
    {
        if (tableScope.Canonical == "$")
        {
            return "$" + relativePath.Canonical[1..];
        }

        return tableScope.Canonical + relativePath.Canonical[1..];
    }
}
