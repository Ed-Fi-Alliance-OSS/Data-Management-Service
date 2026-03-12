// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Groups repeated physical reference identity bindings into site-scoped logical reference fields.
/// </summary>
public static class DocumentReferenceBindingExtensions
{
    /// <summary>
    /// Groups one reference site's identity bindings by logical reference field while preserving first-seen
    /// field order.
    /// </summary>
    public static IReadOnlyList<DocumentReferenceFieldGroup> GetLogicalFieldGroups(
        this DocumentReferenceBinding binding
    )
    {
        ArgumentNullException.ThrowIfNull(binding);

        Dictionary<string, LogicalFieldGroupBuilder> groupsByPath = new(StringComparer.Ordinal);
        List<LogicalFieldGroupBuilder> orderedGroups = [];

        foreach (var identityBinding in binding.IdentityBindings)
        {
            if (
                !groupsByPath.TryGetValue(
                    identityBinding.ReferenceJsonPath.Canonical,
                    out var logicalFieldGroup
                )
            )
            {
                logicalFieldGroup = new LogicalFieldGroupBuilder(identityBinding.ReferenceJsonPath);
                groupsByPath.Add(identityBinding.ReferenceJsonPath.Canonical, logicalFieldGroup);
                orderedGroups.Add(logicalFieldGroup);
            }

            logicalFieldGroup.MemberColumns.Add(identityBinding.Column);
        }

        return orderedGroups
            .Select(group => new DocumentReferenceFieldGroup(
                binding.IsIdentityComponent,
                binding.ReferenceObjectPath,
                binding.Table,
                binding.FkColumn,
                binding.TargetResource,
                group.ReferenceJsonPath,
                group.MemberColumns.ToArray()
            ))
            .ToArray();
    }

    /// <summary>
    /// Groups all supplied reference sites, preserving the input binding order and each binding's first-seen
    /// logical field order.
    /// </summary>
    public static IReadOnlyList<DocumentReferenceFieldGroup> GetLogicalFieldGroups(
        this IEnumerable<DocumentReferenceBinding> bindings
    )
    {
        ArgumentNullException.ThrowIfNull(bindings);

        List<DocumentReferenceFieldGroup> groupedFields = [];

        foreach (var binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            groupedFields.AddRange(binding.GetLogicalFieldGroups());
        }

        return groupedFields;
    }

    private sealed class LogicalFieldGroupBuilder(JsonPathExpression referenceJsonPath)
    {
        public JsonPathExpression ReferenceJsonPath { get; } = referenceJsonPath;
        public List<DbColumnName> MemberColumns { get; } = [];
    }
}

/// <summary>
/// Represents one logical field within one document reference site, including all physical member columns.
/// </summary>
/// <param name="IsIdentityComponent">
/// Indicates whether the parent reference participates in the resource identity projection.
/// </param>
/// <param name="ReferenceObjectPath">The JSONPath of the owning reference object.</param>
/// <param name="Table">The table that owns the reference site.</param>
/// <param name="FkColumn">The <c>..._DocumentId</c> FK column for the owning reference site.</param>
/// <param name="TargetResource">The referenced resource type.</param>
/// <param name="ReferenceJsonPath">The logical field path under the reference object.</param>
/// <param name="MemberColumns">All physical member columns for the logical field in binding order.</param>
public sealed record DocumentReferenceFieldGroup(
    bool IsIdentityComponent,
    JsonPathExpression ReferenceObjectPath,
    DbTableName Table,
    DbColumnName FkColumn,
    QualifiedResourceName TargetResource,
    JsonPathExpression ReferenceJsonPath,
    IReadOnlyList<DbColumnName> MemberColumns
);
