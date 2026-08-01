// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// One batch of natural-key reference lookups: the dialect-neutral input to a per-dialect natural-key
/// lookup command builder.
/// </summary>
/// <remarks>
/// A batch becomes exactly one relational command (one database round trip) that produces exactly one
/// result set per <see cref="Groups"/> entry, in <see cref="Groups"/> order. Callers read the result sets
/// with <c>NextResultAsync</c> in that same order.
/// </remarks>
/// <param name="MappingSet">
/// The mapping set the groups were compiled from. Supplies the shared <c>dms.Descriptor</c> probe
/// descriptor and keys the builders' command-text caches.
/// </param>
/// <param name="Groups">The target groups, in the order their result sets are produced.</param>
public sealed record NaturalKeyLookupBatch(
    MappingSet MappingSet,
    IReadOnlyList<NaturalKeyLookupGroup> Groups
);

/// <summary>
/// One target group within a <see cref="NaturalKeyLookupBatch"/> — all the references in the batch that
/// point at a single target resource, resolved by one statement and returned as one result set.
/// </summary>
/// <param name="Target">The reference target resource.</param>
/// <param name="Entries">
/// The group's lookup entries in ordinal order. <see cref="NaturalKeyLookupEntry.Ordinal"/> is the
/// entry's one-based position in this list.
/// </param>
public abstract record NaturalKeyLookupGroup(
    QualifiedResourceName Target,
    IReadOnlyList<NaturalKeyLookupEntry> Entries
);

/// <summary>
/// A group whose target is a concrete or abstract resource, probed through its compiled
/// <see cref="NaturalKeyProbeTarget"/> (its <c>UX_&lt;T&gt;_RefKey</c> index).
/// </summary>
/// <remarks>
/// Projected columns: <c>Ordinal</c>, <c>DocumentId</c>, and — when
/// <see cref="NaturalKeyProbeTarget.IsAbstract"/> — <c>Discriminator</c>.
/// </remarks>
/// <param name="Target">The reference target resource.</param>
/// <param name="Probe">The compiled probe descriptor for <paramref name="Target"/>.</param>
/// <param name="Entries">
/// The group's lookup entries. Each entry carries one value per <see cref="NaturalKeyProbeTarget.Columns"/>
/// element, positionally parallel.
/// </param>
public sealed record NaturalKeyProbeLookupGroup(
    QualifiedResourceName Target,
    NaturalKeyProbeTarget Probe,
    IReadOnlyList<NaturalKeyLookupEntry> Entries
) : NaturalKeyLookupGroup(Target, Entries);

/// <summary>
/// A group whose target is a descriptor resource, probed against the shared <c>dms.Descriptor</c> table
/// by lower-cased URI alone (a prefix seek of <c>UX_Descriptor_UriLowered_Discriminator</c>).
/// </summary>
/// <remarks>
/// The discriminator is deliberately NOT a predicate: the projection returns the matched row's
/// <c>Discriminator</c> and <c>ResourceKeyId</c> so the caller can tell a genuinely missing descriptor
/// (no row) from a URI that resolves to a descriptor of the wrong type (row with a non-matching
/// <c>ResourceKeyId</c>) — the distinction
/// <c>DescriptorReferenceFailureClassifier</c> makes today.
/// </remarks>
/// <param name="Target">The descriptor resource the reference names.</param>
/// <param name="Entries">
/// The group's lookup entries. Each entry carries exactly one value: the already lower-cased descriptor
/// URI.
/// </param>
public sealed record DescriptorLookupGroup(
    QualifiedResourceName Target,
    IReadOnlyList<NaturalKeyLookupEntry> Entries
) : NaturalKeyLookupGroup(Target, Entries);

/// <summary>
/// One reference lookup within a <see cref="NaturalKeyLookupGroup"/>.
/// </summary>
/// <param name="Ordinal">
/// The entry's one-based position within its group's entry list. It is emitted verbatim: PostgreSQL
/// derives it from <c>WITH ORDINALITY</c> over the parallel input arrays, SQL Server writes it as an
/// inline <c>VALUES</c> literal (never <c>ROW_NUMBER()</c>). The builders reject a group whose ordinals
/// are not exactly <c>1..Entries.Count</c>, because the PostgreSQL mechanism cannot express anything else
/// and a silent divergence between the dialects would misattribute every resolved reference.
/// </param>
/// <param name="Values">
/// The already-typed probe values, positionally parallel to the group's probe columns
/// (<see cref="NaturalKeyProbeTarget.Columns"/>), or a single element for a
/// <see cref="DescriptorLookupGroup"/>.
///
/// Values are CLR values, not strings: the caller converts identity strings once through
/// <c>RelationalScalarLiteralParser</c> — the same converter the write flattener uses to turn a
/// <c>DocumentIdentity</c> into stored column values — so probe values and stored values always agree.
/// A probe column with a non-null <see cref="NaturalKeyProbeColumn.DescriptorResource"/> is the one
/// exception: its value is the already lower-cased descriptor URI <see langword="string"/>, not a
/// descriptor document id, because the builder resolves the URI inline against <c>dms.Descriptor</c>.
/// </param>
public sealed record NaturalKeyLookupEntry(int Ordinal, IReadOnlyList<object> Values);

/// <summary>
/// The result-column names every natural-key lookup statement projects, in both dialects.
/// </summary>
/// <remarks>
/// The builders emit these and the reader binds them, so the two cannot drift apart.
/// </remarks>
public static class NaturalKeyLookupColumns
{
    /// <summary>
    /// The matched entry's one-based ordinal within its group. Always projected.
    /// </summary>
    public const string Ordinal = "Ordinal";

    /// <summary>
    /// The matched target document id. Always projected.
    /// </summary>
    public const string DocumentId = "DocumentId";

    /// <summary>
    /// The matched row's discriminator. Projected for abstract probe groups
    /// (<c>{ProjectName}:{ResourceName}</c>) and for descriptor groups (the bare descriptor resource
    /// name).
    /// </summary>
    public const string Discriminator = "Discriminator";

    /// <summary>
    /// The matched descriptor row's resource key id. Projected for descriptor groups only.
    /// </summary>
    public const string ResourceKeyId = "ResourceKeyId";
}
