// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// The resource a natural-key lookup group probes: a concrete or abstract reference target read through its
/// compiled <see cref="NaturalKeyProbeTarget"/>, or a descriptor resource read through
/// <see cref="DescriptorProbeTarget"/>.
/// </summary>
internal sealed record NaturalKeyLookupTarget
{
    private NaturalKeyLookupTarget(
        QualifiedResourceName resource,
        short resourceKeyId,
        NaturalKeyProbeTarget? probe
    )
    {
        Resource = resource;
        ResourceKeyId = resourceKeyId;
        Probe = probe;
    }

    /// <summary>
    /// The probed resource.
    /// </summary>
    public QualifiedResourceName Resource { get; }

    /// <summary>
    /// The probed resource's own key. Concrete groups project it as a compile-time literal. Descriptor groups
    /// filter the descriptor join on it and project the matched row's own key, which therefore equals it. The
    /// result reader requires every row of both kinds of group to carry it. Abstract groups project the concrete
    /// member key stored on the identity row instead, so this value is never compared for them.
    /// </summary>
    public short ResourceKeyId { get; }

    /// <summary>
    /// The compiled reference-target probe, or <see langword="null"/> for a descriptor target.
    /// </summary>
    public NaturalKeyProbeTarget? Probe { get; }

    /// <summary>
    /// Whether this group probes <c>dms.Descriptor</c>.
    /// </summary>
    public bool IsDescriptor => Probe is null;

    /// <summary>
    /// Creates a target for a concrete or abstract reference-target probe.
    /// </summary>
    public static NaturalKeyLookupTarget ForProbe(NaturalKeyProbeTarget probe, short resourceKeyId)
    {
        ArgumentNullException.ThrowIfNull(probe);

        return new(probe.Resource, resourceKeyId, probe);
    }

    /// <summary>
    /// Creates a target for a descriptor resource stored in <c>dms.Descriptor</c>.
    /// </summary>
    public static NaturalKeyLookupTarget ForDescriptor(
        QualifiedResourceName descriptorResource,
        short resourceKeyId
    ) => new(descriptorResource, resourceKeyId, null);
}

/// <summary>
/// One deduped natural-key lookup.
/// </summary>
/// <param name="KeyValues">
/// Typed probe values in the probe's key order. A descriptor-valued key, and the single key of a descriptor
/// target, is the raw descriptor URI: the probe folds it in SQL.
/// </param>
internal sealed record NaturalKeyLookupEntry(IReadOnlyList<object> KeyValues);

/// <summary>
/// The lookups that resolve through one target. Entry <c>n</c> is addressed by ordinal <c>n + 1</c>.
/// </summary>
/// <param name="GroupOrdinal">The 1-based position of this group in its request.</param>
/// <param name="Target">The probed target.</param>
/// <param name="Entries">The group's lookups, in ordinal order.</param>
internal sealed record NaturalKeyLookupGroup(
    int GroupOrdinal,
    NaturalKeyLookupTarget Target,
    IReadOnlyList<NaturalKeyLookupEntry> Entries
);

/// <summary>
/// A natural-key lookup request: one group per probed target.
/// </summary>
/// <param name="MappingSet">The mapping set the probes were compiled into.</param>
/// <param name="Groups">The groups, in group-ordinal order.</param>
internal sealed record NaturalKeyLookupRequest(
    MappingSet MappingSet,
    IReadOnlyList<NaturalKeyLookupGroup> Groups
);

/// <summary>
/// The row a lookup matched. The two document keys keep separate roles: <paramref name="DocumentId"/> is the
/// owning <c>dms.Document</c> key, and <paramref name="DescriptorId"/> is the compact descriptor key that
/// stored descriptor references hold.
/// </summary>
/// <param name="DocumentId">The matched document.</param>
/// <param name="DescriptorId">The compact descriptor key for a descriptor target, otherwise null.</param>
/// <param name="ResourceKeyId">
/// The matched resource key. For an abstract target it is the concrete member key stored on the identity row.
/// </param>
internal sealed record NaturalKeyLookupMatch(long DocumentId, int? DescriptorId, short ResourceKeyId);

/// <summary>
/// Limits every natural-key lookup shares across dialects.
/// </summary>
internal static class NaturalKeyLookupLimits
{
    /// <summary>
    /// The longest stored descriptor URI: <c>Namespace</c> (255) + <c>'#'</c> + <c>CodeValue</c> (50). On
    /// PostgreSQL a longer URI cannot match any stored descriptor.
    /// </summary>
    public const int MaxDescriptorUriLength = 306;

    /// <summary>
    /// The longest descriptor URI, in UTF-16 code units, that a SQL Server lookup reads. Its identity collation
    /// ignores unweighted characters, so a URI longer than any stored one can still match; this width leaves room
    /// for that while staying below <c>nvarchar(max)</c>, which would weaken the index seek. OPENJSON would
    /// truncate a longer URI into a false match, so it is never bound.
    /// </summary>
    public const int MaxMssqlDescriptorUriProbeLength = 4000;
}

/// <summary>
/// The columns every natural-key lookup statement projects, in order. A group that cannot supply a value
/// projects a typed NULL so that every group shares one union shape.
/// </summary>
internal static class NaturalKeyLookupColumns
{
    public const string GroupOrdinal = "GroupOrdinal";
    public const string Ordinal = "Ordinal";
    public const string DocumentId = "DocumentId";
    public const string DescriptorId = "DescriptorId";
    public const string ResourceKeyId = "ResourceKeyId";
}

/// <summary>
/// Builds the dialect's natural-key lookup commands.
/// </summary>
internal interface INaturalKeyLookupCommandBuilder
{
    /// <summary>
    /// Builds the standalone command: one statement and one result set per group.
    /// </summary>
    RelationalCommand BuildCommand(NaturalKeyLookupRequest request);

    /// <summary>
    /// Builds the single-statement, single-result-set form that a composite command can embed.
    /// </summary>
    RelationalCommand BuildEmbeddableCommand(NaturalKeyLookupRequest request);
}
