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
/// Context carried into the post-overlay key-unification resolver. Assembled by
/// the profile merge synthesizer after it applies the classifier's per-binding
/// dispositions to merge the root row. The resolver recomputes canonical and
/// synthetic-presence values from merged request + stored state — visible
/// members are evaluated from <see cref="WritableRequestBody"/>, hidden-governed
/// members from <see cref="CurrentRootRowByColumnName"/>, and visible-absent
/// members are treated as absent.
/// </summary>
/// <remarks>
/// <see cref="ResolvedReferenceLookups"/> is supplied pre-built by the caller
/// (the profile merge synthesizer). It is the flattener's
/// <see cref="FlatteningResolvedReferenceLookupSet"/>, compiled once per
/// synthesis from the full <see cref="ResourceWritePlan"/> and
/// <see cref="ResolvedReferenceSet"/>, and reused across every key-unification
/// plan the resolver evaluates for the root table.
/// </remarks>
internal sealed record ProfileRootKeyUnificationContext(
    JsonNode WritableRequestBody,
    RelationalWriteCurrentState? CurrentState,
    IReadOnlyDictionary<DbColumnName, object?> CurrentRootRowByColumnName,
    FlatteningResolvedReferenceLookupSet ResolvedReferenceLookups,
    ProfileAppliedWriteRequest ProfileRequest,
    ProfileAppliedWriteContext? ProfileAppliedContext
);

/// <summary>
/// Post-overlay key-unification resolver for the root table. Run by the profile
/// merge synthesizer after it applies the classifier's per-binding dispositions
/// to the merged root row. Thin wrapper over
/// <see cref="ProfileKeyUnificationCore"/>: validates buffer length, short-circuits
/// on no-key-unification plans, then delegates to the core.
/// </summary>
/// <remarks>
/// Exception typing is preserved from the flattener:
/// canonical-disagreement raises <see cref="RelationalWriteRequestValidationException"/>
/// (request-shape failure); presence-gated-null, canonical-non-nullable,
/// missing hidden column, and resolver/classifier drift raise
/// <see cref="InvalidOperationException"/> (invariant violations).
/// </remarks>
internal interface IProfileRootKeyUnificationResolver
{
    void Resolve(
        TableWritePlan rootTableWritePlan,
        ProfileRootKeyUnificationContext context,
        FlattenedWriteValue[] mergedRowValuesMutable,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    );
}

internal sealed class ProfileRootKeyUnificationResolver : IProfileRootKeyUnificationResolver
{
    public void Resolve(
        TableWritePlan rootTableWritePlan,
        ProfileRootKeyUnificationContext context,
        FlattenedWriteValue[] mergedRowValuesMutable,
        ImmutableHashSet<int> resolverOwnedBindingIndices
    )
    {
        ArgumentNullException.ThrowIfNull(rootTableWritePlan);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mergedRowValuesMutable);
        ArgumentNullException.ThrowIfNull(resolverOwnedBindingIndices);

        if (mergedRowValuesMutable.Length != rootTableWritePlan.ColumnBindings.Length)
        {
            throw new InvalidOperationException(
                $"Merged row value buffer length {mergedRowValuesMutable.Length} does not match "
                    + $"root table '{FormatTable(rootTableWritePlan)}' binding count "
                    + $"{rootTableWritePlan.ColumnBindings.Length}."
            );
        }

        if (rootTableWritePlan.KeyUnificationPlans.Length == 0)
        {
            return;
        }

        ProfileKeyUnificationCore.ResolveKeyUnification(
            rootTableWritePlan,
            context.CurrentRootRowByColumnName,
            context.WritableRequestBody,
            context.CurrentState,
            context.ResolvedReferenceLookups,
            context.ProfileRequest,
            context.ProfileAppliedContext,
            mergedRowValuesMutable,
            resolverOwnedBindingIndices
        );
    }

    private static string FormatTable(TableWritePlan tableWritePlan) =>
        $"{tableWritePlan.TableModel.Table.Schema.Value}.{tableWritePlan.TableModel.Table.Name}";
}
