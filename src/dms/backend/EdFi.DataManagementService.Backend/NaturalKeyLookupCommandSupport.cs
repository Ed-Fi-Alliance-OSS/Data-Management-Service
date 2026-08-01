// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Dialect-neutral helpers shared by the PostgreSQL and SQL Server natural-key lookup command builders:
/// batch validation, the descriptor discriminator literal lookup, and the command-text cache shape key.
/// </summary>
internal static class NaturalKeyLookupCommandSupport
{
    /// <summary>
    /// The declared width of <c>dms.Descriptor.Uri</c> / <c>dms.Descriptor.UriLowered</c>
    /// (<c>CoreDdlEmitter.cs</c> <c>StringType(306)</c>). SQL Server needs the probe parameter sized to
    /// match the column, otherwise the implicit widening to <c>nvarchar(max)</c> costs the index seek.
    /// </summary>
    public const int DescriptorUriMaxLength = 306;

    /// <summary>
    /// The alias of the target (or abstract identity) table in every emitted statement.
    /// </summary>
    public const string TargetAlias = "t";

    /// <summary>
    /// The alias of the <c>unnest</c> / <c>VALUES</c> input relation in every emitted statement.
    /// </summary>
    public const string InputAlias = "input";

    /// <summary>
    /// The alias of the <c>dms.Descriptor</c> row in a descriptor-target statement.
    /// </summary>
    public const string DescriptorTargetAlias = "descriptor";

    /// <summary>
    /// The input relation's column alias for probe column <paramref name="columnIndex"/>.
    /// </summary>
    public static string InputColumnAlias(int columnIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"c{columnIndex}");

    /// <summary>
    /// The alias of the inline <c>dms.Descriptor</c> join that resolves the descriptor-valued probe column
    /// at <paramref name="columnIndex"/>.
    /// </summary>
    public static string DescriptorPartAlias(int columnIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"d{columnIndex}");

    /// <summary>
    /// The number of probe values every entry in <paramref name="group"/> must carry.
    /// </summary>
    public static int ProbeValueCount(NaturalKeyLookupGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return group switch
        {
            NaturalKeyProbeLookupGroup probeGroup => probeGroup.Probe.Columns.Count,
            DescriptorLookupGroup => 1,
            _ => throw new NotSupportedException(
                $"Unsupported natural-key lookup group kind '{group.GetType().Name}'."
            ),
        };
    }

    /// <summary>
    /// Validates the structural invariants both builders rely on: every group carries a probe with at
    /// least one column, every entry's value list is parallel to that column list, no value is null, and
    /// the group's ordinals are exactly <c>1..Entries.Count</c> in order.
    /// </summary>
    /// <remarks>
    /// The ordinal check is load bearing. PostgreSQL sources the ordinal from <c>WITH ORDINALITY</c>,
    /// which can only ever produce the one-based array position, while SQL Server writes whatever the
    /// entry declares as an inline literal. Accepting an arbitrary ordinal would let the two dialects
    /// attribute the same result row to different references — a silent mis-resolution rather than a
    /// failure — so a mismatch throws here instead.
    /// </remarks>
    public static void ValidateBatch(NaturalKeyLookupBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.MappingSet);
        ArgumentNullException.ThrowIfNull(batch.Groups);

        for (var groupIndex = 0; groupIndex < batch.Groups.Count; groupIndex++)
        {
            ValidateGroup(batch.Groups[groupIndex], groupIndex);
        }
    }

    private static void ValidateGroup(NaturalKeyLookupGroup group, int groupIndex)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(group.Entries);

        var expectedValueCount = ProbeValueCount(group);

        if (expectedValueCount == 0)
        {
            throw new InvalidOperationException(
                $"Natural-key lookup group {groupIndex} for target '{Describe(group.Target)}' has no probe columns."
            );
        }

        for (var entryIndex = 0; entryIndex < group.Entries.Count; entryIndex++)
        {
            var entry = group.Entries[entryIndex];

            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Values);

            if (entry.Ordinal != entryIndex + 1)
            {
                throw new InvalidOperationException(
                    $"Natural-key lookup group {groupIndex} for target '{Describe(group.Target)}' declares ordinal "
                        + $"{entry.Ordinal} at position {entryIndex}; ordinals must be the one-based entry position."
                );
            }

            if (entry.Values.Count != expectedValueCount)
            {
                throw new InvalidOperationException(
                    $"Natural-key lookup entry {entry.Ordinal} for target '{Describe(group.Target)}' carries "
                        + $"{entry.Values.Count} values but the probe has {expectedValueCount} columns."
                );
            }

            for (var valueIndex = 0; valueIndex < entry.Values.Count; valueIndex++)
            {
                if (entry.Values[valueIndex] is null)
                {
                    throw new InvalidOperationException(
                        $"Natural-key lookup entry {entry.Ordinal} for target '{Describe(group.Target)}' carries a "
                            + $"null value at column {valueIndex}; identity values are never null."
                    );
                }
            }
        }
    }

    /// <summary>
    /// Resolves the compiled discriminator literal for a descriptor resource.
    /// </summary>
    /// <remarks>
    /// <see cref="MappingSet.DescriptorProbeTarget"/> has a non-null default whose literal map is empty,
    /// so a mapping set that was never run through the natural-key probe compiler would otherwise emit a
    /// statement that silently matches nothing. Failing loudly here is the difference between a broken
    /// build and a fleet of unexplained "reference not found" 400s.
    /// </remarks>
    public static string DescriptorDiscriminatorLiteralOrThrow(
        MappingSet mappingSet,
        QualifiedResourceName descriptorResource
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (
            !mappingSet.DescriptorProbeTarget.DiscriminatorLiteralByResource.TryGetValue(
                descriptorResource,
                out var discriminatorLiteral
            )
        )
        {
            throw new InvalidOperationException(
                $"Mapping set '{RelationalWriteSupport.FormatMappingSetKey(mappingSet.Key)}' "
                    + $"is missing a compiled descriptor discriminator literal for resource '{Describe(descriptorResource)}'."
            );
        }

        return discriminatorLiteral;
    }

    /// <summary>
    /// Builds the command-text cache key for a batch: everything about the batch that can change the
    /// emitted SQL, and nothing else.
    /// </summary>
    /// <param name="batch">The batch being built.</param>
    /// <param name="includeEntryCounts">
    /// <see langword="true"/> when the emitted text varies with a group's entry count. Neither dialect
    /// needs it today — PostgreSQL passes each probe column as one array parameter and SQL Server passes
    /// each group as one <c>OPENJSON</c> payload, so both texts are entry-count invariant — but it stays
    /// so a future dialect that inlines entries cannot silently reuse another batch's statement.
    /// </param>
    public static string BuildShapeKey(NaturalKeyLookupBatch batch, bool includeEntryCounts)
    {
        ArgumentNullException.ThrowIfNull(batch);

        StringBuilder builder = new();

        foreach (var group in batch.Groups)
        {
            builder.Append(Describe(group.Target));

            switch (group)
            {
                case NaturalKeyProbeLookupGroup probeGroup:
                    builder.Append("|probe|").Append(probeGroup.Probe.ProbeTable.ToString());
                    builder.Append('|').Append(probeGroup.Probe.DocumentIdColumn.Value);
                    builder.Append('|').Append(probeGroup.Probe.IsAbstract ? '1' : '0');

                    foreach (var column in probeGroup.Probe.Columns)
                    {
                        builder.Append('|').Append(column.StorageColumn.Value);
                        builder.Append(':').Append(column.ScalarType.Kind);
                        builder
                            .Append(':')
                            .Append(column.ScalarType.MaxLength?.ToString(CultureInfo.InvariantCulture));
                        builder
                            .Append(':')
                            .Append(
                                column.ScalarType.Decimal?.Precision.ToString(CultureInfo.InvariantCulture)
                            )
                            .Append(',')
                            .Append(column.ScalarType.Decimal?.Scale.ToString(CultureInfo.InvariantCulture));
                        builder
                            .Append(':')
                            .Append(
                                column.DescriptorResource is { } descriptorResource
                                    ? Describe(descriptorResource)
                                    : string.Empty
                            );
                    }

                    break;
                case DescriptorLookupGroup:
                    builder.Append("|descriptor");
                    break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported natural-key lookup group kind '{group.GetType().Name}'."
                    );
            }

            if (includeEntryCounts)
            {
                builder.Append("|n=").Append(group.Entries.Count.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append(';');
        }

        return builder.ToString();
    }

    private static string Describe(QualifiedResourceName resource) =>
        RelationalWriteSupport.FormatResource(resource);
}
