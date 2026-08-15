// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// One proposed custom-view check paired with the basis value read for it, when SQL decides it.
/// </summary>
/// <param name="Check">The planned check.</param>
/// <param name="BasisValue">
/// The finalized root row's value for the check's bound column. <see langword="null"/> when the row carries no
/// value, which the SQL maps to the auth.md §2.8 proposed-value-missing failure rather than being rejected
/// here — a missing value is a client outcome, not a planning defect.
/// </param>
internal sealed record ProposedCustomViewRuntimeValue(
    SingleRecordCustomViewAuthorizationCheckSpec Check,
    object? BasisValue
);

/// <summary>
/// The proposed custom-view work for one write, split by how each check is decided.
/// </summary>
/// <param name="SqlValues">
/// Checks SQL decides, each with its bound basis value, in planned order.
/// </param>
/// <param name="SelfBasisChecks">
/// Checks whose basis is the subject itself, in planned order. SQL cannot decide them: on a create the
/// document has no <c>DocumentId</c> yet, and on an update the value is the immutable one the paired stored
/// check already authorized. The caller resolves them against the target context.
/// </param>
internal sealed record ProposedCustomViewRuntimeWork(
    IReadOnlyList<ProposedCustomViewRuntimeValue> SqlValues,
    IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> SelfBasisChecks
);

internal abstract record ProposedCustomViewExtractionResult
{
    private ProposedCustomViewExtractionResult() { }

    public sealed record Ready(ProposedCustomViewRuntimeWork Work) : ProposedCustomViewExtractionResult;

    /// <summary>
    /// The planned checks could not be reconciled with the finalized root row. The write fails closed as a
    /// security-configuration failure, matching the namespace and relationship proposed-value siblings.
    /// </summary>
    public sealed record InvalidAuthorizationPlan(string FailureMessage) : ProposedCustomViewExtractionResult;
}

/// <summary>
/// Reads each proposed custom-view check's basis value from the finalized merged root row, using the root
/// table's <see cref="TableWritePlan.ColumnBindings"/> to locate the bound column. Authorization never reads
/// the raw request body: the merged row is what will be persisted, so it is what must be authorized.
/// </summary>
internal static class ProposedCustomViewValueExtractor
{
    public static ProposedCustomViewExtractionResult Extract(
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> proposedChecks,
        RootWriteRowBuffer rootRow
    )
    {
        ArgumentNullException.ThrowIfNull(proposedChecks);
        ArgumentNullException.ThrowIfNull(rootRow);

        if (proposedChecks.Count == 0)
        {
            return Invalid("Proposed custom view authorization requires at least one check spec.");
        }

        var rootTable = rootRow.TableWritePlan.TableModel.Table;
        List<ProposedCustomViewRuntimeValue> sqlValues = [];
        List<SingleRecordCustomViewAuthorizationCheckSpec> selfBasisChecks = [];

        foreach (var check in proposedChecks)
        {
            if (check.ValueSource is not CustomViewAuthorizationCheckValueSource.Proposed)
            {
                return Invalid(
                    $"Proposed custom view authorization cannot extract check '{check.Index}' because it uses value source '{check.ValueSource}'."
                );
            }

            switch (check.CheckTarget)
            {
                case CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable selfBasis:
                    if (!selfBasis.RootTable.Equals(rootTable))
                    {
                        return Invalid(
                            $"Proposed custom view authorization check '{check.Index}' targets root table '{selfBasis.RootTable}', but the finalized root row is for '{rootTable}'."
                        );
                    }

                    selfBasisChecks.Add(check);
                    continue;

                case CustomViewAuthorizationCheckTarget.Proposed proposed:
                    if (!proposed.RootTable.Equals(rootTable))
                    {
                        return Invalid(
                            $"Proposed custom view authorization check '{check.Index}' targets root table '{proposed.RootTable}', but the finalized root row is for '{rootTable}'."
                        );
                    }

                    // RootTable and Binding.Table are independent contract fields, so a malformed plan can
                    // name the right root table and a foreign binding table. Without this check a same-named
                    // column on the root would be read as if it were the foreign table's column.
                    if (!proposed.Binding.Table.Equals(rootTable))
                    {
                        return Invalid(
                            $"Proposed custom view authorization check '{check.Index}' binds basis column '{proposed.Binding.Column.Value}' on table '{proposed.Binding.Table}', but the finalized root row is for '{rootTable}'."
                        );
                    }

                    if (!TryFindBindingIndex(rootRow, proposed.Binding.Column, out var bindingIndex))
                    {
                        // The strategy is configured but the write plan binds no column for its basis
                        // reference — a profile-shaped body that dropped the reference, for instance. Failing
                        // closed is required: skipping the check would serve a write the strategy restricts.
                        return Invalid(
                            $"Proposed custom view authorization could not locate a root binding for basis column '{proposed.Binding.Column.Value}' required by strategy '{check.ConfiguredStrategy.StrategyName}'."
                        );
                    }

                    sqlValues.Add(
                        new ProposedCustomViewRuntimeValue(
                            check,
                            GetBoundSqlValue(rootRow.Values[bindingIndex])
                        )
                    );
                    continue;

                default:
                    return Invalid(
                        $"Proposed custom view authorization check '{check.Index}' does not use a proposed-value target."
                    );
            }
        }

        return new ProposedCustomViewExtractionResult.Ready(
            new ProposedCustomViewRuntimeWork(sqlValues, selfBasisChecks)
        );
    }

    private static bool TryFindBindingIndex(
        RootWriteRowBuffer rootRow,
        External.DbColumnName column,
        out int bindingIndex
    )
    {
        var bindings = rootRow.TableWritePlan.ColumnBindings;

        for (var index = 0; index < bindings.Length; index++)
        {
            if (bindings[index].Column.ColumnName.Equals(column))
            {
                bindingIndex = index;
                return true;
            }
        }

        bindingIndex = -1;
        return false;
    }

    private static object? GetBoundSqlValue(FlattenedWriteValue value) =>
        value is FlattenedWriteValue.Literal { Value: { } literalValue } && literalValue is not DBNull
            ? literalValue
            : null;

    private static ProposedCustomViewExtractionResult Invalid(string failureMessage) =>
        new ProposedCustomViewExtractionResult.InvalidAuthorizationPlan(failureMessage);
}
