// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Classifies why a resource is not eligible for thin-slice root-only read plan compilation.
/// </summary>
internal enum ThinSliceReadPlanUnsupportedReason
{
    /// <summary>
    /// The resource is supported.
    /// </summary>
    None,

    /// <summary>
    /// The resource is not stored as per-resource relational tables.
    /// </summary>
    NonRelationalStorage,

    /// <summary>
    /// The resource has child/extension tables (not root-only).
    /// </summary>
    NonRootOnly,

    /// <summary>
    /// The resource has document-reference bindings and requires reference-identity projection metadata.
    /// </summary>
    RequiresReferenceIdentityProjectionMetadata,

    /// <summary>
    /// The resource has descriptor edge sources and requires descriptor projection metadata.
    /// </summary>
    RequiresDescriptorProjectionMetadata,
}

/// <summary>
/// Thin-slice eligibility and diagnostic details for root-only read plan compilation.
/// </summary>
/// <param name="UnsupportedReason">The reason the resource is unsupported, or <see cref="ThinSliceReadPlanUnsupportedReason.None" /> when supported.</param>
/// <param name="StorageKind">The resource storage kind.</param>
/// <param name="TableCount">The number of tables in <c>TablesInDependencyOrder</c>.</param>
/// <param name="DocumentReferenceBindingCount">The number of document-reference bindings on the resource model.</param>
/// <param name="DescriptorEdgeSourceCount">The number of descriptor edge sources on the resource model.</param>
internal readonly record struct ThinSliceReadPlanSupportResult(
    ThinSliceReadPlanUnsupportedReason UnsupportedReason,
    ResourceStorageKind StorageKind,
    int TableCount,
    int DocumentReferenceBindingCount,
    int DescriptorEdgeSourceCount
)
{
    /// <summary>
    /// Gets a value indicating whether the resource is supported by thin-slice read plan compilation.
    /// </summary>
    public bool IsSupported => UnsupportedReason == ThinSliceReadPlanUnsupportedReason.None;
}

/// <summary>
/// Evaluates whether a resource model is eligible for thin-slice root-only relational-table read plan compilation.
/// </summary>
internal static class ThinSliceReadPlanSupportEvaluator
{
    /// <summary>
    /// Evaluates thin-slice read plan support and returns a deterministic diagnostic result.
    /// </summary>
    /// <param name="resourceModel">The resource model to evaluate.</param>
    /// <returns>Support result describing eligibility and any unsupported reason.</returns>
    public static ThinSliceReadPlanSupportResult Evaluate(RelationalResourceModel resourceModel)
    {
        ArgumentNullException.ThrowIfNull(resourceModel);

        var documentReferenceBindingCount = resourceModel.DocumentReferenceBindings.Count;
        var descriptorEdgeSourceCount = resourceModel.DescriptorEdgeSources.Count;

        var unsupportedReason = resourceModel switch
        {
            { StorageKind: not ResourceStorageKind.RelationalTables } =>
                ThinSliceReadPlanUnsupportedReason.NonRelationalStorage,
            { TablesInDependencyOrder.Count: not 1 } => ThinSliceReadPlanUnsupportedReason.NonRootOnly,
            _ when documentReferenceBindingCount > 0 =>
                ThinSliceReadPlanUnsupportedReason.RequiresReferenceIdentityProjectionMetadata,
            _ when descriptorEdgeSourceCount > 0 =>
                ThinSliceReadPlanUnsupportedReason.RequiresDescriptorProjectionMetadata,
            _ => ThinSliceReadPlanUnsupportedReason.None,
        };

        return new ThinSliceReadPlanSupportResult(
            UnsupportedReason: unsupportedReason,
            StorageKind: resourceModel.StorageKind,
            TableCount: resourceModel.TablesInDependencyOrder.Count,
            DocumentReferenceBindingCount: documentReferenceBindingCount,
            DescriptorEdgeSourceCount: descriptorEdgeSourceCount
        );
    }
}
