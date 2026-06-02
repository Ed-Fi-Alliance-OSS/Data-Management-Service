// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Outcome of reading the proposed namespace value out of the finalized merged root row.
/// </summary>
internal abstract record ProposedNamespaceValueExtractionResult
{
    private ProposedNamespaceValueExtractionResult() { }

    /// <summary>
    /// The proposed namespace value was extracted. <paramref name="ProposedNamespace"/> is
    /// <see langword="null"/> when the finalized value is null/empty, which the SQL maps to a
    /// proposed-namespace-missing failure.
    /// </summary>
    public sealed record Ready(string? ProposedNamespace) : ProposedNamespaceValueExtractionResult;

    /// <summary>
    /// The planned namespace checks could not be reconciled with the finalized root row. The write
    /// fails closed as a security-configuration failure, matching the read-path namespace
    /// security-configuration mapping and the proposed relationship sibling.
    /// </summary>
    public sealed record InvalidAuthorizationPlan(string FailureMessage)
        : ProposedNamespaceValueExtractionResult;
}

/// <summary>
/// Reads the proposed namespace value from the finalized merged root row buffer, using the root
/// table's <see cref="EdFi.DataManagementService.Backend.External.Plans.TableWritePlan.ColumnBindings"/>
/// to locate the namespace column. Authorization never reads the raw request body.
/// </summary>
internal static class ProposedNamespaceValueExtractor
{
    public static ProposedNamespaceValueExtractionResult Extract(
        IReadOnlyList<NamespaceAuthorizationCheckSpec> checks,
        RootWriteRowBuffer rootRow
    )
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(rootRow);

        if (checks.Count == 0)
        {
            return Invalid("Proposed namespace authorization requires at least one check spec.");
        }

        var rootTable = rootRow.TableWritePlan.TableModel.Table;
        var namespaceColumn = checks[0].NamespaceColumn;

        foreach (var check in checks)
        {
            if (check.ValueSource is not NamespaceAuthorizationCheckValueSource.Proposed)
            {
                return Invalid(
                    $"Proposed namespace authorization cannot extract check '{check.Index}' because it uses value source '{check.ValueSource}'."
                );
            }

            if (!check.RootTable.Equals(rootTable))
            {
                return Invalid(
                    $"Proposed namespace authorization check '{check.Index}' targets root table '{check.RootTable}', but the finalized root row is for '{rootTable}'."
                );
            }

            if (!check.NamespaceColumn.Equals(namespaceColumn))
            {
                return Invalid(
                    $"Proposed namespace authorization checks must reference a single namespace column. "
                        + $"Found '{check.NamespaceColumn.Value}' and '{namespaceColumn.Value}'."
                );
            }
        }

        var bindings = rootRow.TableWritePlan.ColumnBindings;
        var bindingIndex = -1;

        for (var index = 0; index < bindings.Length; index++)
        {
            if (bindings[index].Column.ColumnName.Equals(namespaceColumn))
            {
                bindingIndex = index;
                break;
            }
        }

        if (bindingIndex < 0)
        {
            return Invalid(
                $"Proposed namespace authorization could not locate a root binding for namespace column '{namespaceColumn.Value}'."
            );
        }

        var boundValue = GetBoundSqlValue(rootRow.Values[bindingIndex]);

        return boundValue switch
        {
            null => new ProposedNamespaceValueExtractionResult.Ready(null),
            string proposedNamespace => new ProposedNamespaceValueExtractionResult.Ready(
                string.IsNullOrEmpty(proposedNamespace) ? null : proposedNamespace
            ),
            _ => Invalid(
                $"Proposed namespace authorization expected a string namespace value for column "
                    + $"'{namespaceColumn.Value}' but found '{boundValue.GetType().Name}'."
            ),
        };
    }

    private static object? GetBoundSqlValue(FlattenedWriteValue value)
    {
        if (value is FlattenedWriteValue.Literal { Value: { } literalValue } && literalValue is not DBNull)
        {
            return literalValue;
        }

        return null;
    }

    private static ProposedNamespaceValueExtractionResult.InvalidAuthorizationPlan Invalid(string message) =>
        new(message);
}
