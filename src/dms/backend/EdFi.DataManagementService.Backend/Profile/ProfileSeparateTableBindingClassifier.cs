// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Classification result for a single separate-table non-collection scope
/// (<see cref="DbTableKind.RootExtension"/> or <see cref="DbTableKind.CollectionExtensionScope"/>).
/// Shares the per-binding disposition vocabulary with
/// <see cref="ProfileRootTableBindingClassification"/>; reports which bindings the
/// key-unification resolver owns so the synthesizer skips those during overlay.
/// </summary>
internal sealed record ProfileSeparateTableBindingClassification(
    ImmutableArray<RootBindingDisposition> BindingsByIndex,
    ImmutableHashSet<int> ResolverOwnedBindingIndices
);

internal interface IProfileSeparateTableBindingClassifier
{
    ProfileSeparateTableBindingClassification Classify(
        ResourceWritePlan writePlan,
        TableWritePlan separateTablePlan,
        ScopeInstanceAddress scopeAddress,
        RequestScopeState? requestScope,
        StoredScopeState? storedScope,
        ProfileSeparateScopeDescendantStates descendantStates
    );
}

/// <summary>
/// Separate-table wrapper over <see cref="ProfileBindingClassificationCore"/> for
/// non-collection extension scopes (<see cref="DbTableKind.RootExtension"/> or
/// <see cref="DbTableKind.CollectionExtensionScope"/>).
/// </summary>
internal sealed class ProfileSeparateTableBindingClassifier : IProfileSeparateTableBindingClassifier
{
    public ProfileSeparateTableBindingClassification Classify(
        ResourceWritePlan writePlan,
        TableWritePlan separateTablePlan,
        ScopeInstanceAddress scopeAddress,
        RequestScopeState? requestScope,
        StoredScopeState? storedScope,
        ProfileSeparateScopeDescendantStates descendantStates
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(separateTablePlan);
        ArgumentNullException.ThrowIfNull(scopeAddress);

        GuardInstanceAwareTableKind(separateTablePlan);

        var resolverOwnedBindingIndices = ProfileBindingClassificationCore.CollectResolverOwnedIndices(
            separateTablePlan
        );
        var bindingsByIndex = ProfileBindingClassificationCore.ClassifyBindings(
            writePlan,
            separateTablePlan,
            scopeAddress,
            requestScope,
            storedScope,
            descendantStates,
            resolverOwnedBindingIndices
        );
        return new ProfileSeparateTableBindingClassification(bindingsByIndex, resolverOwnedBindingIndices);
    }

    private static void GuardInstanceAwareTableKind(TableWritePlan separateTablePlan)
    {
        var tableKind = separateTablePlan.TableModel.IdentityMetadata.TableKind;
        if (tableKind is not (DbTableKind.RootExtension or DbTableKind.CollectionExtensionScope))
        {
            throw new ArgumentException(
                $"{nameof(ProfileSeparateTableBindingClassifier)} supports "
                    + $"{nameof(DbTableKind.RootExtension)} and "
                    + $"{nameof(DbTableKind.CollectionExtensionScope)} tables; got {tableKind} "
                    + $"for table '{ProfileBindingClassificationCore.FormatTable(separateTablePlan)}'.",
                nameof(separateTablePlan)
            );
        }
    }
}
