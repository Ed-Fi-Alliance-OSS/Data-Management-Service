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
public sealed record SupportedDescriptorQueryField
{
    /// <summary>
    /// Initializes descriptor query field metadata from the target's canonical contract.
    /// </summary>
    public SupportedDescriptorQueryField(string queryFieldName, DescriptorQueryFieldTarget target)
    {
        var valueKind = GetValueKind(target);

        QueryFieldName = queryFieldName;
        Target = target;
        ValueKind = valueKind;
        ApiSchemaType = GetApiSchemaType(valueKind);
        ScalarKind = GetScalarKind(valueKind);
        DescriptorColumn = GetDescriptorColumn(target);
    }

    public SupportedDescriptorQueryField(
        string queryFieldName,
        DescriptorQueryFieldTarget target,
        DescriptorQueryValueKind valueKind,
        string apiSchemaType,
        ScalarKind? scalarKind,
        DbColumnName? descriptorColumn
    )
    {
        QueryFieldName = queryFieldName;
        Target = target;
        ValueKind = valueKind;
        ApiSchemaType = apiSchemaType;
        ScalarKind = scalarKind;
        DescriptorColumn = descriptorColumn;
    }

    /// <summary>
    /// The public query parameter name.
    /// </summary>
    public string QueryFieldName { get; }

    /// <summary>
    /// The deterministic descriptor query predicate target.
    /// </summary>
    public DescriptorQueryFieldTarget Target { get; }

    /// <summary>
    /// The request value preprocessing category for this query field.
    /// </summary>
    public DescriptorQueryValueKind ValueKind { get; }

    /// <summary>
    /// The ApiSchema query field type expected for this query field.
    /// </summary>
    public string ApiSchemaType { get; }

    /// <summary>
    /// The relational scalar kind for descriptor-column predicates.
    /// </summary>
    public ScalarKind? ScalarKind { get; }

    /// <summary>
    /// The shared descriptor column used for descriptor-column predicates.
    /// </summary>
    public DbColumnName? DescriptorColumn { get; }

    private static DescriptorQueryValueKind GetValueKind(DescriptorQueryFieldTarget target)
    {
        return target switch
        {
            DescriptorQueryFieldTarget.DocumentUuid => DescriptorQueryValueKind.DocumentUuid,
            DescriptorQueryFieldTarget.Namespace
            or DescriptorQueryFieldTarget.CodeValue
            or DescriptorQueryFieldTarget.ShortDescription
            or DescriptorQueryFieldTarget.Description => DescriptorQueryValueKind.String,
            DescriptorQueryFieldTarget.EffectiveBeginDate or DescriptorQueryFieldTarget.EffectiveEndDate =>
                DescriptorQueryValueKind.Date,
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "Unsupported descriptor query target."
            ),
        };
    }

    private static string GetApiSchemaType(DescriptorQueryValueKind valueKind)
    {
        return valueKind switch
        {
            DescriptorQueryValueKind.DocumentUuid or DescriptorQueryValueKind.String => "string",
            DescriptorQueryValueKind.Date => "date",
            _ => throw new ArgumentOutOfRangeException(
                nameof(valueKind),
                valueKind,
                "Unsupported descriptor query value kind."
            ),
        };
    }

    private static ScalarKind? GetScalarKind(DescriptorQueryValueKind valueKind)
    {
        return valueKind switch
        {
            DescriptorQueryValueKind.DocumentUuid => null,
            DescriptorQueryValueKind.String => EdFi.DataManagementService.Backend.External.ScalarKind.String,
            DescriptorQueryValueKind.Date => EdFi.DataManagementService.Backend.External.ScalarKind.Date,
            _ => throw new ArgumentOutOfRangeException(
                nameof(valueKind),
                valueKind,
                "Unsupported descriptor query value kind."
            ),
        };
    }

    private static DbColumnName? GetDescriptorColumn(DescriptorQueryFieldTarget target)
    {
        return target switch
        {
            DescriptorQueryFieldTarget.DocumentUuid => null,
            DescriptorQueryFieldTarget.Namespace descriptorNamespace => descriptorNamespace.Column,
            DescriptorQueryFieldTarget.CodeValue codeValue => codeValue.Column,
            DescriptorQueryFieldTarget.ShortDescription shortDescription => shortDescription.Column,
            DescriptorQueryFieldTarget.Description description => description.Column,
            DescriptorQueryFieldTarget.EffectiveBeginDate effectiveBeginDate => effectiveBeginDate.Column,
            DescriptorQueryFieldTarget.EffectiveEndDate effectiveEndDate => effectiveEndDate.Column,
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "Unsupported descriptor query target."
            ),
        };
    }
}

/// <summary>
/// Request value preprocessing categories for compiled descriptor query fields.
/// </summary>
public enum DescriptorQueryValueKind
{
    /// <summary>
    /// The query value must parse as <c>dms.Document.DocumentUuid</c>.
    /// </summary>
    DocumentUuid,

    /// <summary>
    /// The query value is used as an exact-match string.
    /// </summary>
    String,

    /// <summary>
    /// The query value must parse as an exact-match date.
    /// </summary>
    Date,
}

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
