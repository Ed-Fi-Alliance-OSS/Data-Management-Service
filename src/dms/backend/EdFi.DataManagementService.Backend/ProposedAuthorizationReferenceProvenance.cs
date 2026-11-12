// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Decides whether a null finalized root value is null only because the submitted document reference it
/// derives from could not be resolved and its missing-document failure was deferred for authorization.
/// </summary>
/// <remarks>
/// The finalized row carries a plain null for both an unresolved reference and a genuinely absent value, so
/// proposed authorization cannot tell them apart from the value alone. This association uses the compiled
/// value source of the exact root binding and the request's deferred reference paths, compared ordinally
/// against the binding's root reference-object path. A nonnull finalized value always wins, so a resolved,
/// scalar, or hidden-preserved contributor is never marked.
/// </remarks>
internal static class ProposedAuthorizationReferenceProvenance
{
    /// <summary>
    /// Locates the effective body value for a deferred reference-derived binding. Callers retain any
    /// finalized key-unified contributor before using this fallback. Core validates equality of supplied
    /// unified members, so the first deferred member is sufficient when the canonical value is null.
    /// </summary>
    public static bool TryGetDeferredBodyPath(
        TableWritePlan rootPlan,
        int bindingIndex,
        IReadOnlySet<string> deferredPaths,
        out JsonPathExpression bodyPath
    )
    {
        bodyPath = default;
        var binding = rootPlan.ColumnBindings[bindingIndex];

        switch (binding.Source)
        {
            case WriteValueSource.ReferenceDerived referenceDerived
                when deferredPaths.Contains(referenceDerived.ReferenceSource.ReferenceObjectPath.Canonical):
                bodyPath =
                    binding.Column.SourceJsonPath ?? referenceDerived.ReferenceSource.ReferenceJsonPath;
                return true;

            case WriteValueSource.Precomputed:
                foreach (var plan in rootPlan.KeyUnificationPlans)
                {
                    if (plan.CanonicalBindingIndex != bindingIndex)
                    {
                        continue;
                    }

                    foreach (var member in plan.MembersInOrder)
                    {
                        if (
                            member is KeyUnificationMemberWritePlan.ReferenceDerivedMember referenceMember
                            && deferredPaths.Contains(
                                referenceMember.ReferenceSource.ReferenceObjectPath.Canonical
                            )
                        )
                        {
                            bodyPath = referenceMember.RelativePath;
                            return true;
                        }
                    }
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Classifies one finalized root binding. Returns <see langword="false"/> with a diagnostic when the
    /// binding's reference metadata cannot be reconciled with the write plan; otherwise returns
    /// <see langword="true"/> with an empty diagnostic.
    /// </summary>
    public static bool TryClassify(
        ResourceWritePlan writePlan,
        RootWriteRowBuffer rootRow,
        int bindingIndex,
        IReadOnlySet<string> deferredPaths,
        out bool isDeferredMissingReferenceValue,
        out string failureMessage
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(rootRow);
        ArgumentNullException.ThrowIfNull(deferredPaths);

        isDeferredMissingReferenceValue = false;
        failureMessage = string.Empty;

        var rootPlan = rootRow.TableWritePlan;

        if (
            bindingIndex < 0
            || bindingIndex >= rootPlan.ColumnBindings.Length
            || bindingIndex >= rootRow.Values.Length
        )
        {
            failureMessage =
                $"Proposed authorization reference provenance cannot classify root binding index {bindingIndex}; "
                + $"the finalized root row for '{rootPlan.TableModel.Table}' has {rootRow.Values.Length} values.";
            return false;
        }

        if (deferredPaths.Count == 0 || !IsNullValue(rootRow.Values[bindingIndex]))
        {
            return true;
        }

        switch (rootPlan.ColumnBindings[bindingIndex].Source)
        {
            case WriteValueSource.DocumentReference documentReference:
                if (
                    !TryGetReferenceObjectPath(
                        writePlan,
                        documentReference.BindingIndex,
                        expectedReferenceObjectPath: null,
                        out var documentReferencePath,
                        out failureMessage
                    )
                )
                {
                    return false;
                }

                isDeferredMissingReferenceValue = deferredPaths.Contains(documentReferencePath);
                return true;

            case WriteValueSource.ReferenceDerived referenceDerived:
                return TryClassifyReferenceSource(
                    writePlan,
                    referenceDerived.ReferenceSource,
                    deferredPaths,
                    out isDeferredMissingReferenceValue,
                    out failureMessage
                );

            case WriteValueSource.Precomputed:
                return TryClassifyKeyUnifiedCanonical(
                    writePlan,
                    rootPlan,
                    bindingIndex,
                    deferredPaths,
                    out isDeferredMissingReferenceValue,
                    out failureMessage
                );

            default:
                return true;
        }
    }

    /// <summary>
    /// A null key-unified canonical means no member supplied a value. It is attributable to a deferred
    /// reference only when one of that canonical's own reference-derived members is deferred.
    /// </summary>
    private static bool TryClassifyKeyUnifiedCanonical(
        ResourceWritePlan writePlan,
        TableWritePlan rootPlan,
        int bindingIndex,
        IReadOnlySet<string> deferredPaths,
        out bool isDeferredMissingReferenceValue,
        out string failureMessage
    )
    {
        isDeferredMissingReferenceValue = false;
        failureMessage = string.Empty;

        foreach (var keyUnificationPlan in rootPlan.KeyUnificationPlans)
        {
            if (keyUnificationPlan.CanonicalBindingIndex != bindingIndex)
            {
                continue;
            }

            foreach (var member in keyUnificationPlan.MembersInOrder)
            {
                if (member is not KeyUnificationMemberWritePlan.ReferenceDerivedMember referenceMember)
                {
                    continue;
                }

                if (
                    !TryClassifyReferenceSource(
                        writePlan,
                        referenceMember.ReferenceSource,
                        deferredPaths,
                        out var isMemberDeferred,
                        out failureMessage
                    )
                )
                {
                    return false;
                }

                isDeferredMissingReferenceValue |= isMemberDeferred;
            }
        }

        return true;
    }

    private static bool TryClassifyReferenceSource(
        ResourceWritePlan writePlan,
        ReferenceDerivedValueSourceMetadata referenceSource,
        IReadOnlySet<string> deferredPaths,
        out bool isDeferredMissingReferenceValue,
        out string failureMessage
    )
    {
        isDeferredMissingReferenceValue = false;

        if (
            !TryGetReferenceObjectPath(
                writePlan,
                referenceSource.BindingIndex,
                referenceSource.ReferenceObjectPath.Canonical,
                out var referenceObjectPath,
                out failureMessage
            )
        )
        {
            return false;
        }

        isDeferredMissingReferenceValue = deferredPaths.Contains(referenceObjectPath);
        return true;
    }

    private static bool TryGetReferenceObjectPath(
        ResourceWritePlan writePlan,
        int documentReferenceBindingIndex,
        string? expectedReferenceObjectPath,
        out string referenceObjectPath,
        out string failureMessage
    )
    {
        referenceObjectPath = string.Empty;
        failureMessage = string.Empty;

        var referenceBindings = writePlan.Model.DocumentReferenceBindings;

        if (documentReferenceBindingIndex < 0 || documentReferenceBindingIndex >= referenceBindings.Count)
        {
            failureMessage =
                $"Proposed authorization reference provenance found document-reference binding index {documentReferenceBindingIndex}, "
                + $"but resource '{RelationalWriteSupport.FormatResource(writePlan.Model.Resource)}' has {referenceBindings.Count} document-reference bindings.";
            return false;
        }

        referenceObjectPath = referenceBindings[documentReferenceBindingIndex].ReferenceObjectPath.Canonical;

        if (
            expectedReferenceObjectPath is not null
            && !string.Equals(expectedReferenceObjectPath, referenceObjectPath, StringComparison.Ordinal)
        )
        {
            failureMessage =
                $"Proposed authorization reference provenance found reference-derived path '{expectedReferenceObjectPath}' "
                + $"for document-reference binding index {documentReferenceBindingIndex}, which binds '{referenceObjectPath}'.";
            return false;
        }

        return true;
    }

    private static bool IsNullValue(FlattenedWriteValue value) =>
        value is not FlattenedWriteValue.Literal { Value: { } literalValue } || literalValue is DBNull;
}
