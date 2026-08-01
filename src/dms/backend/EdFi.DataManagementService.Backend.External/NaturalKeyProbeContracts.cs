// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Compiled natural-key probe descriptor for one reference *target*: the table and column set a
/// reference lookup seeks so it lands on the target's <c>UX_&lt;T&gt;_RefKey</c> unique index
/// (identity storage columns leading, <c>DocumentId</c> trailing).
/// </summary>
/// <remarks>
/// Derived from the target's own <c>identityJsonPaths</c> ordering, resolved to root-table columns by
/// <see cref="DbColumnModel.SourceJsonPath"/>, then storage-resolved through
/// <see cref="ColumnStorage.UnifiedAlias"/> and de-duplicated by storage column — the exact sequence
/// <c>ReferenceConstraintPass</c> uses when it derives the <c>*_RefKey</c> constraint. Binding a
/// unified-alias column instead of its canonical stored column would be semantically correct but could
/// not seek the index, so storage resolution is load bearing.
/// </remarks>
/// <param name="ProbeTable">
/// The table the probe seeks: the concrete resource's root table, or the <c>{Abstract}Identity</c>
/// table for abstract resource keys (never the abstract union view, which carries no index).
/// </param>
/// <param name="DocumentIdColumn">The probe table's <c>DocumentId</c> column.</param>
/// <param name="IsAbstract">Whether <paramref name="ProbeTable"/> is an <c>{Abstract}Identity</c> table.</param>
/// <param name="Columns">
/// The probe predicate columns in <c>*_RefKey</c> order (identity-path order, de-duplicated by storage
/// column, first-seen order preserved).
/// </param>
public sealed record NaturalKeyProbeTarget(
    DbTableName ProbeTable,
    DbColumnName DocumentIdColumn,
    bool IsAbstract,
    IReadOnlyList<NaturalKeyProbeColumn> Columns
);

/// <summary>
/// One predicate column of a <see cref="NaturalKeyProbeTarget"/>.
/// </summary>
/// <param name="StorageColumn">
/// The canonical stored column on the probe table — never a <see cref="ColumnStorage.UnifiedAlias"/>.
/// </param>
/// <param name="SourceIdentityJsonPath">
/// The identity JSONPath (on the target resource) that first resolved to this storage column. Under key
/// unification several identity paths can collapse onto one storage column; the first one in
/// identity-path order is recorded.
/// </param>
/// <param name="ScalarType">The storage column's scalar type, for parameter coercion.</param>
/// <param name="DescriptorResource">
/// Non-null when the column is a descriptor foreign key (<c>..._DescriptorId</c>), naming the descriptor
/// resource whose URI must be resolved to a descriptor document id before the probe can bind a value.
/// </param>
public sealed record NaturalKeyProbeColumn(
    DbColumnName StorageColumn,
    JsonPathExpression SourceIdentityJsonPath,
    RelationalScalarType ScalarType,
    QualifiedResourceName? DescriptorResource
);

/// <summary>
/// Compiled natural-key probe descriptor for a resource's *own* identity — the column set of its root
/// <c>UX_&lt;R&gt;_NK</c> unique constraint, used for upsert detection and for recognizing a natural-key
/// unique violation as a 409 identity conflict.
/// </summary>
/// <remarks>
/// Unlike <see cref="NaturalKeyProbeTarget"/>, reference-sourced identity parts collapse to the single
/// <c>..._DocumentId</c> foreign-key column of the reference site (four <c>courseOfferingReference</c>
/// identity paths become one <c>CourseOffering_DocumentId</c>), matching
/// <c>RootIdentityConstraintPass.BuildRootIdentityColumns</c>. Descriptor identity parts stay scalar
/// <c>..._DescriptorId</c> columns.
/// </remarks>
/// <param name="RootTable">The resource's root table.</param>
/// <param name="Columns">
/// The natural-key columns in <c>UX_&lt;R&gt;_NK</c> order (identity-path order, de-duplicated by column,
/// first-seen order preserved).
/// </param>
public sealed record OwnNaturalKeyProbe(
    DbTableName RootTable,
    IReadOnlyList<OwnNaturalKeyProbeColumn> Columns
)
{
    /// <summary>
    /// The resource's <c>identityJsonPaths</c> in schema order, one entry per identity element with no
    /// de-duplication — the ordering contract Core's <c>DocumentIdentity</c> follows. This is the
    /// attribution source for the 409 <c>duplicateIdentityValues</c> body, which reports one entry per
    /// identity path rather than one per (collapsed) natural-key column.
    /// </summary>
    public IReadOnlyList<JsonPathExpression> IdentityJsonPathsInOrder { get; init; } = [];
}

/// <summary>
/// One column of an <see cref="OwnNaturalKeyProbe"/>. Exactly one of
/// <paramref name="ScalarSourceJsonPath"/> and <paramref name="ReferenceIdentityJsonPath"/> is non-null.
/// </summary>
/// <param name="ColumnName">The root-table column named by the natural-key constraint.</param>
/// <param name="ScalarType">The column's scalar type, for parameter coercion.</param>
/// <param name="ScalarSourceJsonPath">
/// For a value read straight from the request payload (scalar or descriptor identity part), the identity
/// JSONPath that sources it; otherwise <see langword="null"/>.
/// </param>
/// <param name="ReferenceIdentityJsonPath">
/// For a <c>..._DocumentId</c> column filled from an already-resolved document reference, the identity
/// JSONPath under the reference object that first mapped to this reference site; otherwise
/// <see langword="null"/>.
/// </param>
/// <param name="DescriptorResource">
/// Non-null when the column is a descriptor foreign key (<c>..._DescriptorId</c>).
/// </param>
public sealed record OwnNaturalKeyProbeColumn(
    DbColumnName ColumnName,
    RelationalScalarType ScalarType,
    JsonPathExpression? ScalarSourceJsonPath,
    JsonPathExpression? ReferenceIdentityJsonPath,
    QualifiedResourceName? DescriptorResource
);

/// <summary>
/// The fixed <c>dms.Descriptor</c> column names the descriptor probe binds.
/// </summary>
/// <remarks>
/// These are the single source of the literals: the DDL emitter that creates the column, the compiler that
/// binds it into a <see cref="DescriptorProbeTarget"/>, and the mapping-set default all read them from
/// here, so a rename cannot leave the emitted schema and the compiled probe disagreeing.
/// </remarks>
public static class DescriptorProbeColumns
{
    /// <summary>
    /// The engine-computed, persisted lower-cased projection of <c>dms.Descriptor.Uri</c> that the probe
    /// seeks. The original-case <c>Uri</c> column remains the stored representation.
    /// </summary>
    public static readonly DbColumnName UriLowered = new("UriLowered");
}

/// <summary>
/// Compiled probe descriptor for the shared <c>dms.Descriptor</c> table: how a descriptor URI plus its
/// owning descriptor resource is resolved to a descriptor document id.
/// </summary>
/// <remarks>
/// Descriptor matching is case-insensitive by Ed-Fi contract and Core hands the backend an already
/// lower-cased URI (<c>DescriptorExtractor.CreateDescriptorReference</c>), so the probe binds a
/// persisted lower-cased URI column rather than the original-case <c>Uri</c> column.
/// </remarks>
/// <param name="Table">The shared descriptor table (<c>dms.Descriptor</c>).</param>
/// <param name="UriLoweredColumn">The persisted lower-cased URI column.</param>
/// <param name="DiscriminatorColumn">The descriptor discriminator column.</param>
/// <param name="DiscriminatorLiteralByResource">
/// The discriminator literal written for each descriptor resource, keyed by qualified resource name.
/// The literal is the BARE resource name — it must byte-match what
/// <c>DescriptorWriteBodyExtractor</c> stores, and it is deliberately NOT the
/// <c>"{ProjectName}:{ResourceName}"</c> form used by link injection and abstract identity tables.
/// </param>
public sealed record DescriptorProbeTarget(
    DbTableName Table,
    DbColumnName UriLoweredColumn,
    DbColumnName DiscriminatorColumn,
    IReadOnlyDictionary<QualifiedResourceName, string> DiscriminatorLiteralByResource
);
