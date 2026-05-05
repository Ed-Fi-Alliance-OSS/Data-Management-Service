// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Compiled descriptor-endpoint query metadata for one descriptor resource.
/// </summary>
/// <param name="Support">
/// Whether descriptor endpoint query is supported for the resource or intentionally omitted.
/// </param>
/// <param name="SupportedFieldsByQueryField">
/// Query fields that can compile directly to deterministic shared <c>dms.Descriptor</c> predicates.
/// </param>
public sealed record DescriptorQueryCapability(
    DescriptorQuerySupport Support,
    IReadOnlyDictionary<string, SupportedDescriptorQueryField> SupportedFieldsByQueryField
);

/// <summary>
/// Resource-scoped descriptor endpoint query support state.
/// </summary>
public abstract record DescriptorQuerySupport
{
    private DescriptorQuerySupport() { }

    /// <summary>
    /// Descriptor endpoint query is supported for this resource.
    /// </summary>
    public sealed record Supported : DescriptorQuerySupport;

    /// <summary>
    /// Descriptor endpoint query was intentionally omitted for this resource.
    /// </summary>
    /// <param name="Omission">The stable omission classification and actionable reason.</param>
    public sealed record Omitted(DescriptorQueryCapabilityOmission Omission) : DescriptorQuerySupport;
}

/// <summary>
/// Deterministic resource-scoped descriptor endpoint query omission metadata.
/// </summary>
/// <param name="Kind">The omission classification.</param>
/// <param name="Reason">An actionable stable omission reason.</param>
public sealed record DescriptorQueryCapabilityOmission(
    DescriptorQueryCapabilityOmissionKind Kind,
    string Reason
);

/// <summary>
/// Classifies why descriptor endpoint query support was intentionally omitted for a resource.
/// </summary>
public enum DescriptorQueryCapabilityOmissionKind
{
    /// <summary>
    /// The descriptor resource's ApiSchema query-field mapping disagrees with the shared descriptor column contract.
    /// </summary>
    ApiSchemaMismatch,
}

/// <summary>
/// A descriptor query field that compiled successfully for descriptor endpoint querying.
/// </summary>
/// <param name="QueryFieldName">The public query parameter name.</param>
/// <param name="Target">The deterministic descriptor query predicate target.</param>
public sealed record SupportedDescriptorQueryField(string QueryFieldName, DescriptorQueryFieldTarget Target);

/// <summary>
/// The deterministic predicate target for a compiled descriptor query field.
/// </summary>
public abstract record DescriptorQueryFieldTarget
{
    private DescriptorQueryFieldTarget() { }

    /// <summary>
    /// A query field that targets <c>dms.Document.DocumentUuid</c>.
    /// </summary>
    public sealed record DocumentUuid : DescriptorQueryFieldTarget;

    /// <summary>
    /// A query field that targets <c>dms.Descriptor.Namespace</c>.
    /// </summary>
    /// <param name="Column">The shared descriptor column used for the predicate.</param>
    public sealed record Namespace(DbColumnName Column) : DescriptorQueryFieldTarget;

    /// <summary>
    /// A query field that targets <c>dms.Descriptor.CodeValue</c>.
    /// </summary>
    /// <param name="Column">The shared descriptor column used for the predicate.</param>
    public sealed record CodeValue(DbColumnName Column) : DescriptorQueryFieldTarget;

    /// <summary>
    /// A query field that targets <c>dms.Descriptor.ShortDescription</c>.
    /// </summary>
    /// <param name="Column">The shared descriptor column used for the predicate.</param>
    public sealed record ShortDescription(DbColumnName Column) : DescriptorQueryFieldTarget;

    /// <summary>
    /// A query field that targets <c>dms.Descriptor.Description</c>.
    /// </summary>
    /// <param name="Column">The shared descriptor column used for the predicate.</param>
    public sealed record Description(DbColumnName Column) : DescriptorQueryFieldTarget;

    /// <summary>
    /// A query field that targets <c>dms.Descriptor.EffectiveBeginDate</c>.
    /// </summary>
    /// <param name="Column">The shared descriptor column used for the predicate.</param>
    public sealed record EffectiveBeginDate(DbColumnName Column) : DescriptorQueryFieldTarget;

    /// <summary>
    /// A query field that targets <c>dms.Descriptor.EffectiveEndDate</c>.
    /// </summary>
    /// <param name="Column">The shared descriptor column used for the predicate.</param>
    public sealed record EffectiveEndDate(DbColumnName Column) : DescriptorQueryFieldTarget;
}
