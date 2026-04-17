// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Shared key-unification guardrail helper used by the flattener and the
/// post-overlay resolver. Extracted without behavior change from
/// <see cref="RelationalWriteFlattener"/>; exception typing preserved
/// (InvalidOperationException for presence-gated-null and non-nullable-canonical
/// violations).
/// </summary>
internal static class ProfileKeyUnificationGuardrails
{
    internal static void Validate(
        TableWritePlan tableWritePlan,
        KeyUnificationWritePlan keyUnificationPlan,
        object? canonicalValue,
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<bool> valueAssigned
    )
    {
        foreach (var member in keyUnificationPlan.MembersInOrder)
        {
            if (member.PresenceBindingIndex is not int presenceBindingIndex)
            {
                continue;
            }

            if (!valueAssigned[presenceBindingIndex])
            {
                throw new InvalidOperationException(
                    $"Presence binding for key-unification member '{member.MemberPathColumn.Value}' on table "
                        + $"'{RelationalWriteFlattener.FormatTable(tableWritePlan)}' was not assigned before guardrail validation."
                );
            }

            if (values[presenceBindingIndex] is not FlattenedWriteValue.Literal presenceValue)
            {
                throw new InvalidOperationException(
                    $"Presence binding for key-unification member '{member.MemberPathColumn.Value}' on table "
                        + $"'{RelationalWriteFlattener.FormatTable(tableWritePlan)}' was not materialized as a literal value."
                );
            }

            if (presenceValue.Value is not null && canonicalValue is null)
            {
                throw new InvalidOperationException(
                    $"Key-unification canonical column '{keyUnificationPlan.CanonicalColumn.Value}' on table "
                        + $"'{RelationalWriteFlattener.FormatTable(tableWritePlan)}' resolved to null while presence column "
                        + $"'{member.PresenceColumn!.Value}' indicated member '{member.MemberPathColumn.Value}' was present."
                );
            }
        }

        var canonicalBinding = tableWritePlan.ColumnBindings[keyUnificationPlan.CanonicalBindingIndex];

        if (!canonicalBinding.Column.IsNullable && canonicalValue is null)
        {
            throw new InvalidOperationException(
                $"Key-unification canonical column '{canonicalBinding.Column.ColumnName.Value}' on table "
                    + $"'{RelationalWriteFlattener.FormatTable(tableWritePlan)}' is not nullable but resolved to null."
            );
        }
    }
}
