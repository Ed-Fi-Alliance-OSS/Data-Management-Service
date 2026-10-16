// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Identifies a compiled mapping set selection key.
/// </summary>
/// <param name="EffectiveSchemaHash">The effective schema hash (lowercase hex).</param>
/// <param name="Dialect">The SQL dialect.</param>
/// <param name="RelationalMappingVersion">The relational mapping version label.</param>
public readonly record struct MappingSetKey(
    string EffectiveSchemaHash,
    SqlDialect Dialect,
    string RelationalMappingVersion
);

/// <summary>
/// Compiled mapping set used by runtime plan execution.
/// </summary>
/// <param name="Key">The mapping set selection key.</param>
/// <param name="Model">The derived relational model set used to compile plans.</param>
/// <param name="WritePlansByResource">Write plans keyed by qualified resource name.</param>
/// <param name="ReadPlansByResource">Read plans keyed by qualified resource name.</param>
/// <param name="ResourceKeyIdByResource">Resource key identifiers keyed by qualified resource name.</param>
/// <param name="ResourceKeyById">Resource key entries keyed by resource key id.</param>
/// <param name="SecurableElementColumnPathsByResource">
/// Precomputed securable element authorization column paths keyed by qualified resource name.
/// Each value is a list of <see cref="ResolvedSecurableElementPath"/> entries carrying
/// the element kind and the join path to reach the authorization column.
/// </param>
public sealed record MappingSet(
    MappingSetKey Key,
    DerivedRelationalModelSet Model,
    IReadOnlyDictionary<QualifiedResourceName, ResourceWritePlan> WritePlansByResource,
    IReadOnlyDictionary<QualifiedResourceName, ResourceReadPlan> ReadPlansByResource,
    IReadOnlyDictionary<QualifiedResourceName, short> ResourceKeyIdByResource,
    IReadOnlyDictionary<short, ResourceKeyEntry> ResourceKeyById,
    IReadOnlyDictionary<
        QualifiedResourceName,
        IReadOnlyList<ResolvedSecurableElementPath>
    > SecurableElementColumnPathsByResource
)
{
    private const string AotMappingPackDecodeStoryRef =
        "reference/design/backend-redesign/epics/05-mpack-generation/05-pack-loader-validation.md";

    /// <summary>
    /// Compiled relational GET-many query capability or omission metadata keyed by qualified resource name.
    /// </summary>
#pragma warning disable IDE0055
    public IReadOnlyDictionary<
        QualifiedResourceName,
        RelationalQueryCapability
    > QueryCapabilitiesByResource { get; init; } =
        new Dictionary<QualifiedResourceName, RelationalQueryCapability>();
#pragma warning restore IDE0055

    /// <summary>
    /// Compiled descriptor-endpoint query capability or omission metadata keyed by qualified descriptor resource name.
    /// </summary>
#pragma warning disable IDE0055
    public IReadOnlyDictionary<
        QualifiedResourceName,
        DescriptorQueryCapability
    > DescriptorQueryCapabilitiesByResource { get; init; } =
        new Dictionary<QualifiedResourceName, DescriptorQueryCapability>();
#pragma warning restore IDE0055

    /// <summary>
    /// Natural-key reference-target probes keyed by qualified resource name. Entries exist for every
    /// abstract resource with an identity table and for every concrete relational resource that carries
    /// <c>UX_&lt;R&gt;_RefKey</c> (i.e., is referenced by some resource in the effective schema).
    /// Never-referenced resources have no entry. Mapping-set validation rejects an entry for a resource
    /// without RefKey inventory.
    /// </summary>
#pragma warning disable IDE0055
    public IReadOnlyDictionary<
        QualifiedResourceName,
        NaturalKeyProbeTarget
    > NaturalKeyProbeTargets { get; init; } = new Dictionary<QualifiedResourceName, NaturalKeyProbeTarget>();
#pragma warning restore IDE0055

    /// <summary>
    /// Own natural-key probes keyed by qualified resource name. Entries exist only for concrete resources
    /// with <see cref="ResourceStorageKind.RelationalTables"/>. Descriptor resources use
    /// <see cref="DescriptorProbeTarget"/> for identity lookup.
    /// </summary>
#pragma warning disable IDE0055
    public IReadOnlyDictionary<
        QualifiedResourceName,
        OwnNaturalKeyProbe
    > OwnNaturalKeyProbesByResource { get; init; } =
        new Dictionary<QualifiedResourceName, OwnNaturalKeyProbe>();
#pragma warning restore IDE0055

    /// <summary>
    /// The natural-key probe for descriptor resources, all of which share <c>dms.Descriptor</c>.
    /// </summary>
    public DescriptorProbeTarget DescriptorProbeTarget { get; init; } =
        DescriptorProbeTarget.SharedDescriptorTable;

    /// <summary>
    /// Creates a mapping set from an AOT mapping-pack payload.
    /// </summary>
    /// <param name="payload">The decoded mapping-pack payload.</param>
    /// <returns>The materialized mapping set.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown until AOT mapping-pack decode support is implemented.
    /// </exception>
    public static MappingSet FromPayload(MappingPackPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        throw new NotSupportedException(
            "AOT mapping-pack decode is not implemented yet for MappingSet.FromPayload(MappingPackPayload). "
                + $"See E05-S05: {AotMappingPackDecodeStoryRef}."
        );
    }
}

/// <summary>
/// Placeholder mapping-pack payload contract.
/// </summary>
public sealed record MappingPackPayload;

/// <summary>
/// Whether a natural-key reference-target probe reads a concrete root table or an abstract identity table.
/// </summary>
public enum NaturalKeyProbeTargetKind
{
    /// <summary>
    /// The probe reads a concrete resource's root table.
    /// </summary>
    Concrete,

    /// <summary>
    /// The probe reads an abstract resource's identity table.
    /// </summary>
    Abstract,
}

/// <summary>
/// How a request-side natural-key value is bound to a probe key column.
/// </summary>
public abstract record NaturalKeyProbeKeyBinding
{
    private NaturalKeyProbeKeyBinding() { }

    /// <summary>
    /// The request-side value is compared directly to a scalar column.
    /// </summary>
    /// <param name="ScalarType">The scalar type of the key column.</param>
    public sealed record Scalar(RelationalScalarType ScalarType) : NaturalKeyProbeKeyBinding;

    /// <summary>
    /// The request-side value is a descriptor URI. It is resolved through the descriptor probe using the
    /// descriptor resource's <c>ResourceKeyId</c>, and the resulting Int32 <c>DescriptorId</c> is compared
    /// to the key column. The owning descriptor <c>DocumentId</c> remains a separate result.
    /// </summary>
    /// <param name="DescriptorResource">The descriptor resource the key column references.</param>
    public sealed record Descriptor(QualifiedResourceName DescriptorResource) : NaturalKeyProbeKeyBinding;

    /// <summary>
    /// The request-side value is a document reference identity that participates in the owning resource's
    /// identity. It is resolved through the indexed <see cref="DocumentReferenceBinding"/>, and the resulting
    /// referenced <c>DocumentId</c> is compared to the key column. Valid only in
    /// <see cref="OwnNaturalKeyProbe.KeyColumns"/>; mapping-set validation rejects it in
    /// <see cref="NaturalKeyProbeTarget.KeyColumns"/>.
    /// </summary>
    /// <param name="DocumentReferenceBindingIndex">
    /// The index into the resource's <c>DocumentReferenceBindings</c>.
    /// </param>
    public sealed record DocumentReference(int DocumentReferenceBindingIndex) : NaturalKeyProbeKeyBinding;
}

/// <summary>
/// One natural-key probe key entry. There is one entry per identity JSON path, in semantic identity order;
/// key-unified paths may share a column.
/// </summary>
/// <param name="IdentityJsonPath">The canonical identity JSON path, also used as the diagnostic name.</param>
/// <param name="Column">The bound key column.</param>
/// <param name="Binding">How the request-side value is bound to <paramref name="Column"/>.</param>
public sealed record NaturalKeyProbeKeyColumn(
    JsonPathExpression IdentityJsonPath,
    DbColumnName Column,
    NaturalKeyProbeKeyBinding Binding
);

/// <summary>
/// A natural-key probe used to resolve references to a resource.
/// </summary>
/// <param name="Resource">The referenced resource.</param>
/// <param name="Kind">Whether the probe reads a concrete root table or an abstract identity table.</param>
/// <param name="Table">The probed table.</param>
/// <param name="KeyColumns">Storage-resolved key entries in semantic identity order.</param>
/// <param name="DocumentIdColumn">The column holding the resolved <c>DocumentId</c>.</param>
/// <param name="ResourceKeyIdColumn">
/// The concrete member <c>ResourceKeyId</c> column; present only for abstract probes.
/// </param>
public sealed record NaturalKeyProbeTarget(
    QualifiedResourceName Resource,
    NaturalKeyProbeTargetKind Kind,
    DbTableName Table,
    IReadOnlyList<NaturalKeyProbeKeyColumn> KeyColumns,
    DbColumnName DocumentIdColumn,
    DbColumnName? ResourceKeyIdColumn = null
);

/// <summary>
/// A resource's probe for its own natural key, sharing the <c>UX_&lt;R&gt;_NK</c> column contract.
/// </summary>
/// <param name="Resource">The resource.</param>
/// <param name="RootTable">The resource's root table.</param>
/// <param name="KeyColumns">
/// Key entries in semantic natural-key order, bound to the emitted <c>UX_&lt;R&gt;_NK</c> columns.
/// <see cref="NaturalKeyProbeKeyBinding.DocumentReference"/> bindings are valid here.
/// </param>
/// <param name="DocumentIdColumn">The root table's <c>DocumentId</c> column.</param>
public sealed record OwnNaturalKeyProbe(
    QualifiedResourceName Resource,
    DbTableName RootTable,
    IReadOnlyList<NaturalKeyProbeKeyColumn> KeyColumns,
    DbColumnName DocumentIdColumn
);

/// <summary>
/// The natural-key probe for descriptor resources stored in the shared descriptor table.
/// </summary>
/// <param name="DescriptorTable">The shared descriptor table.</param>
/// <param name="NamespaceColumn">The descriptor namespace column.</param>
/// <param name="CodeValueColumn">The descriptor code value column.</param>
/// <param name="ResourceKeyIdColumn">The descriptor resource key column.</param>
/// <param name="DescriptorIdColumn">The Int32 descriptor key column.</param>
/// <param name="DocumentIdColumn">The Int64 owning document column.</param>
public sealed record DescriptorProbeTarget(
    DbTableName DescriptorTable,
    DbColumnName NamespaceColumn,
    DbColumnName CodeValueColumn,
    DbColumnName ResourceKeyIdColumn,
    DbColumnName DescriptorIdColumn,
    DbColumnName DocumentIdColumn
)
{
    /// <summary>
    /// The PostgreSQL collation that folds a lowered descriptor URI independently of the database default.
    /// The descriptor index and every PostgreSQL descriptor probe fold under it, on both the column and the
    /// parameter side.
    /// </summary>
    public const string PostgresqlUriFoldCollation = "pg_c_utf8";

    /// <summary>
    /// The probe for <c>dms.Descriptor</c>, which core DDL owns and which is the same for every schema.
    /// </summary>
    public static DescriptorProbeTarget SharedDescriptorTable { get; } =
        new(
            new DbTableName(new DbSchemaName("dms"), "Descriptor"),
            DescriptorIdentityTextColumns.Namespace,
            DescriptorIdentityTextColumns.CodeValue,
            new DbColumnName("ResourceKeyId"),
            new DbColumnName("DescriptorId"),
            new DbColumnName("DocumentId")
        );
}
