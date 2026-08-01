// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Reads the multi-result-set output of a natural-key lookup command: exactly one result set per batch
/// group, in group order.
/// </summary>
/// <remarks>
/// Dialect-neutral by construction. Both builders project the same column names
/// (<see cref="NaturalKeyLookupColumns" />) with the same types — notably <c>Ordinal</c> as a 4-byte
/// integer on both engines — and both emit exactly one result set per group even when SQL Server has to
/// chunk a group's <c>VALUES</c> input, because the chunks are <c>UNION ALL</c>-ed inside one statement.
/// </remarks>
internal static class NaturalKeyLookupResultReader
{
    public static async Task<IReadOnlyList<NaturalKeyLookupRow>> ReadAsync(
        NaturalKeyLookupBatch batch,
        IRelationalCommandReader reader,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(reader);

        List<NaturalKeyLookupRow> rows = [];

        for (var groupIndex = 0; groupIndex < batch.Groups.Count; groupIndex++)
        {
            if (groupIndex > 0 && !await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Natural-key lookup command produced fewer result sets than groups: expected "
                        + $"{batch.Groups.Count} but the reader ended after {groupIndex}."
                );
            }

            var group = batch.Groups[groupIndex];
            var projectsDiscriminator =
                group is DescriptorLookupGroup or NaturalKeyProbeLookupGroup { Probe.IsAbstract: true };
            var projectsResourceKeyId = group is DescriptorLookupGroup;

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(
                    new NaturalKeyLookupRow(
                        GroupIndex: groupIndex,
                        Ordinal: reader.GetRequiredFieldValue<int>(NaturalKeyLookupColumns.Ordinal),
                        DocumentId: reader.GetRequiredFieldValue<long>(NaturalKeyLookupColumns.DocumentId),
                        Discriminator: projectsDiscriminator
                            ? reader.GetNullableFieldValue<string>(NaturalKeyLookupColumns.Discriminator)
                            : null,
                        // Mirrored from dms.Document by the descriptor stamping trigger and declared
                        // nullable, so it is read nullable rather than assumed present.
                        ResourceKeyId: projectsResourceKeyId
                            ? ReadNullableInt16(reader, NaturalKeyLookupColumns.ResourceKeyId)
                            : null
                    )
                );
            }
        }

        return rows;
    }

    private static short? ReadNullableInt16(IRelationalCommandReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<short>(ordinal);
    }
}
