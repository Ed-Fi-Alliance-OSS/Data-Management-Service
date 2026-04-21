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
/// Classification result for a single root-attached separate-table non-collection
/// scope (<see cref="DbTableKind.RootExtension"/>). Shares the per-binding
/// disposition vocabulary with <see cref="ProfileRootTableBindingClassification"/>;
/// reports which bindings the key-unification resolver owns so the synthesizer
/// skips those during overlay.
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
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext
    );
}

/// <summary>
/// Separate-table wrapper over <see cref="ProfileBindingClassificationCore"/> for
/// root-attached non-collection extension scopes (<see cref="DbTableKind.RootExtension"/>).
/// Rejects non-<see cref="DbTableKind.RootExtension"/> tables; collection-aligned
/// separate-table scopes (<see cref="DbTableKind.CollectionExtensionScope"/>) are
/// fenced out of slice 3 and addressed in slice 5.
/// </summary>
internal sealed class ProfileSeparateTableBindingClassifier : IProfileSeparateTableBindingClassifier
{
    public ProfileSeparateTableBindingClassification Classify(
        ResourceWritePlan writePlan,
        TableWritePlan separateTablePlan,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(separateTablePlan);
        ArgumentNullException.ThrowIfNull(profileRequest);

        var tableKind = separateTablePlan.TableModel.IdentityMetadata.TableKind;
        if (tableKind is not DbTableKind.RootExtension)
        {
            throw new ArgumentException(
                $"{nameof(ProfileSeparateTableBindingClassifier)} supports only "
                    + $"{nameof(DbTableKind.RootExtension)} tables in slice 3; "
                    + $"got {tableKind} for table '{ProfileBindingClassificationCore.FormatTable(separateTablePlan)}'. "
                    + "Collection-aligned separate-table scopes belong to slice 5.",
                nameof(separateTablePlan)
            );
        }

        var resolverOwnedBindingIndices = ProfileBindingClassificationCore.CollectResolverOwnedIndices(
            separateTablePlan
        );
        var bindingsByIndex = ProfileBindingClassificationCore.ClassifyBindings(
            writePlan,
            separateTablePlan,
            profileRequest,
            profileAppliedContext,
            resolverOwnedBindingIndices
        );
        return new ProfileSeparateTableBindingClassification(bindingsByIndex, resolverOwnedBindingIndices);
    }
}
