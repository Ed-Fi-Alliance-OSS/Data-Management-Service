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
/// Input contract for the profile merge synthesizer. Slice 2 merge itself is root-table-only,
/// but the input write plan may carry additional tables (e.g. a separate-table extension scope
/// that the profile renders hidden or request-absent). Those non-root tables are intentionally
/// excluded from the produced merge result; the persister then leaves them untouched. The
/// handed-off <see cref="FlattenedWriteSet"/>, however, must itself be root-only: non-root
/// flattened buffers (root-extension rows or collection candidates on the root row) must be
/// fenced upstream and are rejected fail-closed here.
/// </summary>
internal sealed record RelationalWriteProfileMergeRequest
{
    public RelationalWriteProfileMergeRequest(
        ResourceWritePlan writePlan,
        FlattenedWriteSet flattenedWriteSet,
        JsonNode writableRequestBody,
        RelationalWriteCurrentState? currentState,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext,
        ResolvedReferenceSet resolvedReferences
    )
    {
        WritePlan = writePlan ?? throw new ArgumentNullException(nameof(writePlan));
        FlattenedWriteSet = flattenedWriteSet ?? throw new ArgumentNullException(nameof(flattenedWriteSet));
        WritableRequestBody =
            writableRequestBody ?? throw new ArgumentNullException(nameof(writableRequestBody));
        CurrentState = currentState;
        ProfileRequest = profileRequest ?? throw new ArgumentNullException(nameof(profileRequest));
        ProfileAppliedContext = profileAppliedContext;
        ResolvedReferences =
            resolvedReferences ?? throw new ArgumentNullException(nameof(resolvedReferences));

        if (
            !ReferenceEquals(
                WritePlan.TablePlansInDependencyOrder[0],
                FlattenedWriteSet.RootRow.TableWritePlan
            )
        )
        {
            throw new ArgumentException(
                $"{nameof(flattenedWriteSet)} must use the root table from the supplied {nameof(writePlan)}.",
                nameof(flattenedWriteSet)
            );
        }
        if (
            !FlattenedWriteSet.RootRow.RootExtensionRows.IsDefaultOrEmpty
            || !FlattenedWriteSet.RootRow.CollectionCandidates.IsDefaultOrEmpty
        )
        {
            throw new ArgumentException(
                "Slice 2 profile merge requires a root-only flattened shape; non-root flattened "
                    + "buffers (root-extension rows or collection candidates) must be fenced upstream.",
                nameof(flattenedWriteSet)
            );
        }
        if (
            ProfileAppliedContext is not null
            && !ReferenceEquals(ProfileAppliedContext.Request, ProfileRequest)
        )
        {
            throw new ArgumentException(
                $"{nameof(profileAppliedContext)}.Request must be the same instance as {nameof(profileRequest)}.",
                nameof(profileAppliedContext)
            );
        }
        if ((CurrentState is null) != (ProfileAppliedContext is null))
        {
            throw new ArgumentException(
                $"{nameof(currentState)} and {nameof(profileAppliedContext)} must both be null (create-new) "
                    + "or both be non-null (existing-document)."
            );
        }
    }

    public ResourceWritePlan WritePlan { get; init; }

    public FlattenedWriteSet FlattenedWriteSet { get; init; }

    public JsonNode WritableRequestBody { get; init; }

    public RelationalWriteCurrentState? CurrentState { get; init; }

    public ProfileAppliedWriteRequest ProfileRequest { get; init; }

    public ProfileAppliedWriteContext? ProfileAppliedContext { get; init; }

    public ResolvedReferenceSet ResolvedReferences { get; init; }
}

internal interface IRelationalWriteProfileMergeSynthesizer
{
    RelationalWriteMergeResult Synthesize(RelationalWriteProfileMergeRequest request);
}

/// <summary>
/// Root-table-only profile merge synthesizer for Slice 2. Composes the root-table binding
/// classifier, the per-disposition overlay, and the post-overlay key-unification resolver
/// into a single <see cref="RelationalWriteMergeResult"/>. Slice 2 does not support
/// guarded no-op; <see cref="RelationalWriteMergeResult.SupportsGuardedNoOp"/> is always
/// <c>false</c>.
/// </summary>
internal sealed class RelationalWriteProfileMergeSynthesizer(
    IProfileRootTableBindingClassifier classifier,
    IProfileRootKeyUnificationResolver resolver
) : IRelationalWriteProfileMergeSynthesizer
{
    private readonly IProfileRootTableBindingClassifier _classifier =
        classifier ?? throw new ArgumentNullException(nameof(classifier));
    private readonly IProfileRootKeyUnificationResolver _resolver =
        resolver ?? throw new ArgumentNullException(nameof(resolver));

    public RelationalWriteMergeResult Synthesize(RelationalWriteProfileMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rootTable = request.WritePlan.TablePlansInDependencyOrder[0];

        // 1. Project the current root row (binding-indexed) and the column-name projection for
        //    the resolver context. Both come from the same hydrated row via shared support
        //    helpers so downstream normalization stays symmetric.
        RelationalWriteMergedTableRow? projectedCurrentRootRow = null;
        IReadOnlyDictionary<DbColumnName, object?> currentRootRowByColumnName = ImmutableDictionary<
            DbColumnName,
            object?
        >.Empty;

        if (request.CurrentState is not null)
        {
            var hydrated = request.CurrentState.TableRowsInDependencyOrder.Single(h =>
                h.TableModel.Table.Equals(rootTable.TableModel.Table)
            );
            if (hydrated.Rows.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Root table '{ProfileBindingClassificationCore.FormatTable(rootTable)}' has {hydrated.Rows.Count} current rows "
                        + "for profiled existing-document merge; expected exactly one."
                );
            }

            var projected = RelationalWriteMergeSupport.ProjectCurrentRows(rootTable, hydrated.Rows);
            projectedCurrentRootRow = projected[0];
            currentRootRowByColumnName = RelationalWriteMergeSupport.BuildCurrentRowByColumnName(
                rootTable.TableModel,
                hydrated.Rows[0]
            );
        }

        // 2. Classify root-table bindings.
        var classification = _classifier.Classify(
            request.WritePlan,
            request.ProfileRequest,
            request.ProfileAppliedContext
        );

        // 3. Overlay per disposition. Skip resolver-owned bindings; the resolver writes them
        //    in step 4.
        var mergedValues = new FlattenedWriteValue[rootTable.ColumnBindings.Length];
        for (var bindingIndex = 0; bindingIndex < mergedValues.Length; bindingIndex++)
        {
            if (classification.ResolverOwnedBindingIndices.Contains(bindingIndex))
            {
                // Resolver will write; leave a default so the array is fully populated before
                // the resolver call (the resolver overwrites these indices).
                continue;
            }
            switch (classification.BindingsByIndex[bindingIndex])
            {
                case RootBindingDisposition.VisibleWritable:
                case RootBindingDisposition.StorageManaged:
                    mergedValues[bindingIndex] = request.FlattenedWriteSet.RootRow.Values[bindingIndex];
                    break;
                case RootBindingDisposition.HiddenPreserved:
                    if (projectedCurrentRootRow is null)
                    {
                        throw new InvalidOperationException(
                            $"Root-table binding at index {bindingIndex} on table '{ProfileBindingClassificationCore.FormatTable(rootTable)}' "
                                + "classified HiddenPreserved, but no current row is available. "
                                + "Upstream contract violation between classifier and synthesizer."
                        );
                    }
                    mergedValues[bindingIndex] = projectedCurrentRootRow.Values[bindingIndex];
                    break;
                case RootBindingDisposition.ClearOnVisibleAbsent:
                    mergedValues[bindingIndex] = new FlattenedWriteValue.Literal(null);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected RootBindingDisposition '{classification.BindingsByIndex[bindingIndex]}' "
                            + $"at index {bindingIndex} on table '{ProfileBindingClassificationCore.FormatTable(rootTable)}'."
                    );
            }
        }

        // 4. Build the resolver context and recompute canonical + synthetic-presence bindings.
        var resolvedReferenceLookups = FlatteningResolvedReferenceLookupSet.Create(
            request.WritePlan,
            request.ResolvedReferences
        );
        var resolverContext = new ProfileRootKeyUnificationContext(
            WritableRequestBody: request.WritableRequestBody,
            CurrentState: request.CurrentState,
            CurrentRootRowByColumnName: currentRootRowByColumnName,
            ResolvedReferenceLookups: resolvedReferenceLookups,
            ProfileRequest: request.ProfileRequest,
            ProfileAppliedContext: request.ProfileAppliedContext
        );

        _resolver.Resolve(
            rootTable,
            resolverContext,
            mergedValues,
            classification.ResolverOwnedBindingIndices
        );

        // 5. Assemble the merge result.
        var comparableValues = RelationalWriteMergeSupport.ProjectComparableValues(rootTable, mergedValues);
        var mergedRow = new RelationalWriteMergedTableRow(mergedValues, comparableValues);

        ImmutableArray<RelationalWriteMergedTableRow> currentRows = projectedCurrentRootRow is null
            ? []
            : [projectedCurrentRootRow];

        var tableState = new RelationalWriteMergedTableState(rootTable, currentRows, [mergedRow]);
        return new RelationalWriteMergeResult([tableState], supportsGuardedNoOp: false);
    }
}
