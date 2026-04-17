// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// One ApiSchema <c>queryFieldMapping</c> entry normalized to canonical JSONPath expressions.
/// </summary>
/// <param name="QueryFieldName">The public query parameter name.</param>
/// <param name="Paths">The JSON paths the query parameter targets.</param>
public sealed record RelationalQueryFieldMapping(
    string QueryFieldName,
    IReadOnlyList<RelationalQueryFieldPath> Paths
);

/// <summary>
/// One queryable JSON path plus its ApiSchema scalar type label.
/// </summary>
/// <param name="Path">The canonical JSON path.</param>
/// <param name="Type">The ApiSchema scalar type label.</param>
public sealed record RelationalQueryFieldPath(JsonPathExpression Path, string Type);

/// <summary>
/// Compiled relational GET-many query metadata for one resource.
/// </summary>
/// <param name="Support">Whether relational GET-many is supported for the resource or intentionally omitted.</param>
/// <param name="SupportedFieldsByQueryField">
/// Query fields that can compile directly to deterministic root-table predicates.
/// </param>
/// <param name="UnsupportedFieldsByQueryField">
/// Query fields identified at compile time as unsupported for root-table relational GET-many.
/// </param>
public sealed record RelationalQueryCapability(
    RelationalQuerySupport Support,
    IReadOnlyDictionary<string, SupportedRelationalQueryField> SupportedFieldsByQueryField,
    IReadOnlyDictionary<string, UnsupportedRelationalQueryField> UnsupportedFieldsByQueryField
);

/// <summary>
/// Resource-scoped relational GET-many support state.
/// </summary>
public abstract record RelationalQuerySupport
{
    private RelationalQuerySupport() { }

    /// <summary>
    /// Relational GET-many is supported for this resource.
    /// </summary>
    public sealed record Supported : RelationalQuerySupport;

    /// <summary>
    /// Relational GET-many was intentionally omitted for this resource.
    /// </summary>
    /// <param name="Omission">The stable omission classification and actionable reason.</param>
    public sealed record Omitted(RelationalQueryCapabilityOmission Omission) : RelationalQuerySupport;
}

/// <summary>
/// Deterministic resource-scoped relational GET-many omission metadata.
/// </summary>
/// <param name="Kind">The omission classification.</param>
/// <param name="Reason">An actionable stable omission reason.</param>
public sealed record RelationalQueryCapabilityOmission(
    RelationalQueryCapabilityOmissionKind Kind,
    string Reason
);

/// <summary>
/// Classifies why relational GET-many support was intentionally omitted for a resource.
/// </summary>
public enum RelationalQueryCapabilityOmissionKind
{
    /// <summary>
    /// The resource is stored in the shared descriptor table and follows the descriptor-endpoint query path.
    /// </summary>
    DescriptorResource,

    /// <summary>
    /// The resource has one or more unsupported query-field mappings.
    /// </summary>
    UnsupportedQueryFields,
}

/// <summary>
/// A query field that compiled successfully for relational GET-many.
/// </summary>
/// <param name="QueryFieldName">The public query parameter name.</param>
/// <param name="Path">The single canonical JSON path bound by the query field.</param>
/// <param name="Target">The deterministic root-table predicate target.</param>
public sealed record SupportedRelationalQueryField(
    string QueryFieldName,
    RelationalQueryFieldPath Path,
    RelationalQueryFieldTarget Target
);

/// <summary>
/// A query field that was classified during compilation as unsupported for root-table relational GET-many.
/// </summary>
/// <param name="QueryFieldName">The public query parameter name.</param>
/// <param name="Paths">The raw ApiSchema query paths for diagnostics and later omission decisions.</param>
/// <param name="FailureKind">The compile-time support classification.</param>
public sealed record UnsupportedRelationalQueryField(
    string QueryFieldName,
    IReadOnlyList<RelationalQueryFieldPath> Paths,
    RelationalQueryFieldFailureKind FailureKind
);

/// <summary>
/// Classifies why a query field could not yet compile to a deterministic root-table predicate target.
/// </summary>
public enum RelationalQueryFieldFailureKind
{
    /// <summary>
    /// The query field maps to multiple ApiSchema paths and therefore cannot compile to a single predicate target.
    /// </summary>
    MultiPath,

    /// <summary>
    /// The query path crosses an array scope and therefore cannot compile to a root-table predicate.
    /// </summary>
    ArrayCrossing,

    /// <summary>
    /// The query path targets a non-root relational table.
    /// </summary>
    NonRootTable,

    /// <summary>
    /// The query path did not resolve to a deterministic relational-table binding.
    /// </summary>
    UnmappedPath,

    /// <summary>
    /// The query path resolved to more than one possible root-table predicate target.
    /// </summary>
    AmbiguousRootTarget,
}

/// <summary>
/// The deterministic relational predicate target for a compiled query field.
/// </summary>
public abstract record RelationalQueryFieldTarget
{
    private RelationalQueryFieldTarget() { }

    /// <summary>
    /// A query field that binds directly to one root-table column.
    /// </summary>
    /// <param name="Column">The API-bound root-table column used for the predicate.</param>
    public sealed record RootColumn(DbColumnName Column) : RelationalQueryFieldTarget;

    /// <summary>
    /// A query field that targets <c>dms.Document.DocumentUuid</c> and therefore requires the special-case document join.
    /// </summary>
    public sealed record DocumentUuid : RelationalQueryFieldTarget;

    /// <summary>
    /// A descriptor-valued query field that resolves a URI to a descriptor <c>DocumentId</c> and then filters on one root-table FK column.
    /// </summary>
    /// <param name="Column">The root-table descriptor FK column.</param>
    /// <param name="DescriptorResource">The descriptor resource type expected at the query path.</param>
    public sealed record DescriptorIdColumn(DbColumnName Column, QualifiedResourceName DescriptorResource)
        : RelationalQueryFieldTarget;
}
