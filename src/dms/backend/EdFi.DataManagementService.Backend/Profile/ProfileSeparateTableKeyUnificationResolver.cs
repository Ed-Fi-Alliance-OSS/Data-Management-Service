// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Context carried into the post-overlay key-unification resolver for a root-attached
/// separate-table (<see cref="DbTableKind.RootExtension"/>) scope. Assembled by the
/// profile merge synthesizer after it applies the separate-table classifier's per-binding
/// dispositions to the merged separate-table row. Mirrors
/// <see cref="ProfileRootKeyUnificationContext"/>; the column-name projection field is
/// named <see cref="CurrentRowByColumnName"/> (without the "Root" prefix) because this
/// context is for separate-table rows, not the root row.
/// </summary>
/// <remarks>
/// <see cref="ResolvedReferenceLookups"/> is supplied pre-built by the caller
/// (the profile merge synthesizer). It is the flattener's
/// <see cref="FlatteningResolvedReferenceLookupSet"/>, compiled once per
/// synthesis from the full <see cref="ResourceWritePlan"/> and
/// <see cref="ResolvedReferenceSet"/>, and reused across every key-unification
/// plan the resolver evaluates for the separate table.
/// </remarks>
internal sealed record ProfileSeparateTableKeyUnificationContext(
    JsonNode WritableRequestBody,
    RelationalWriteCurrentState? CurrentState,
    IReadOnlyDictionary<DbColumnName, object?> CurrentRowByColumnName,
    FlatteningResolvedReferenceLookupSet ResolvedReferenceLookups,
    ProfileAppliedWriteRequest ProfileRequest,
    ProfileAppliedWriteContext? ProfileAppliedContext
);

/// <summary>
/// Post-overlay key-unification resolver for a root-attached separate-table
/// (<see cref="DbTableKind.RootExtension"/>) scope. Run by the profile merge
/// synthesizer after it applies the separate-table classifier's per-binding
/// dispositions to the merged separate-table row. Thin wrapper over
/// <see cref="ProfileKeyUnificationCore"/>: rejects non-<see cref="DbTableKind.RootExtension"/>
/// tables (collection-aligned scopes belong to slice 5), validates buffer length,
/// short-circuits on no-key-unification plans, then delegates to the core.
/// </summary>
/// <remarks>
/// Exception typing is preserved from the flattener:
/// canonical-disagreement raises <see cref="RelationalWriteRequestValidationException"/>
/// (request-shape failure); presence-gated-null, canonical-non-nullable,
/// missing hidden column, and resolver/classifier drift raise
/// <see cref="InvalidOperationException"/> (invariant violations).
/// </remarks>
internal interface IProfileSeparateTableKeyUnificationResolver
{
    void Resolve(
        TableWritePlan separateTablePlan,
        ProfileSeparateTableKeyUnificationContext context,
        FlattenedWriteValue[] mergedRowValuesMutable,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    );
}

internal sealed class ProfileSeparateTableKeyUnificationResolver : IProfileSeparateTableKeyUnificationResolver
{
    public void Resolve(
        TableWritePlan separateTablePlan,
        ProfileSeparateTableKeyUnificationContext context,
        FlattenedWriteValue[] mergedRowValuesMutable,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    )
    {
        ArgumentNullException.ThrowIfNull(separateTablePlan);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mergedRowValuesMutable);
        ArgumentNullException.ThrowIfNull(resolverOwnedBindingIndices);

        var tableKind = separateTablePlan.TableModel.IdentityMetadata.TableKind;
        if (tableKind is not DbTableKind.RootExtension)
        {
            throw new ArgumentException(
                $"{nameof(ProfileSeparateTableKeyUnificationResolver)} supports only "
                    + $"{nameof(DbTableKind.RootExtension)} tables in slice 3; "
                    + $"got {tableKind} for table '{ProfileBindingClassificationCore.FormatTable(separateTablePlan)}'. "
                    + "Collection-aligned separate-table scopes belong to slice 5.",
                nameof(separateTablePlan)
            );
        }

        if (mergedRowValuesMutable.Length != separateTablePlan.ColumnBindings.Length)
        {
            throw new InvalidOperationException(
                $"Merged row value buffer length {mergedRowValuesMutable.Length} does not match "
                    + $"separate table '{ProfileBindingClassificationCore.FormatTable(separateTablePlan)}' binding count "
                    + $"{separateTablePlan.ColumnBindings.Length}."
            );
        }

        if (separateTablePlan.KeyUnificationPlans.Length == 0)
        {
            return;
        }

        ProfileKeyUnificationCore.ResolveKeyUnification(
            separateTablePlan,
            context.CurrentRowByColumnName,
            context.WritableRequestBody,
            context.CurrentState,
            context.ResolvedReferenceLookups,
            context.ProfileRequest,
            context.ProfileAppliedContext,
            mergedRowValuesMutable,
            resolverOwnedBindingIndices
        );
    }
}
