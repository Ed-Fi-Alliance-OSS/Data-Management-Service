// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Per-binding disposition produced by <see cref="IProfileRootTableBindingClassifier"/>.
/// </summary>
internal enum RootBindingDisposition
{
    /// <summary>
    /// The profile makes this binding writable in this request. The synthesizer should
    /// evaluate the binding from the writable request body.
    /// </summary>
    VisibleWritable,

    /// <summary>
    /// The profile hides this binding on the stored side (either the whole scope is hidden,
    /// or the binding's member path is on the stored hidden-paths list). The synthesizer
    /// must preserve the stored value.
    /// </summary>
    HiddenPreserved,

    /// <summary>
    /// The profile makes this binding's containing scope visible but the request omits it.
    /// The synthesizer should clear the stored value.
    /// </summary>
    ClearOnVisibleAbsent,

    /// <summary>
    /// The binding is storage-managed (DocumentId, Precomputed, or resolver-owned). The
    /// synthesizer must not derive its value from request JSON.
    /// </summary>
    StorageManaged,
}

/// <summary>
/// Classification result for the root table's column bindings.
/// </summary>
internal sealed record ProfileRootTableBindingClassification(
    ImmutableArray<RootBindingDisposition> BindingsByIndex,
    ImmutableHashSet<int> ResolverOwnedBindingIndices
);

internal interface IProfileRootTableBindingClassifier
{
    ProfileRootTableBindingClassification Classify(
        ResourceWritePlan writePlan,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext
    );
}

/// <summary>
/// Root-table wrapper over <see cref="ProfileBindingClassificationCore"/>. Selects the
/// root <see cref="TableWritePlan"/> from <see cref="ResourceWritePlan.TablePlansInDependencyOrder"/>
/// and delegates all per-binding classification and drift-check machinery to the core.
/// </summary>
internal sealed class ProfileRootTableBindingClassifier : IProfileRootTableBindingClassifier
{
    public ProfileRootTableBindingClassification Classify(
        ResourceWritePlan writePlan,
        ProfileAppliedWriteRequest profileRequest,
        ProfileAppliedWriteContext? profileAppliedContext
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(profileRequest);

        var rootTable = writePlan.TablePlansInDependencyOrder[0];
        var resolverOwnedBindingIndices = ProfileBindingClassificationCore.CollectResolverOwnedIndices(
            rootTable
        );
        var bindingsByIndex = ProfileBindingClassificationCore.ClassifyBindings(
            writePlan,
            rootTable,
            profileRequest,
            profileAppliedContext,
            resolverOwnedBindingIndices
        );
        return new ProfileRootTableBindingClassification(bindingsByIndex, resolverOwnedBindingIndices);
    }
}
