// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Decodes natural-key lookup rows into a match table indexed <c>[GroupOrdinal - 1][Ordinal - 1]</c>.
/// </summary>
/// <remarks>
/// Rows map by their explicit group ordinal and ordinal, never by row or result-set position. At most one row
/// may match each lookup: a second row is invariant corruption, never something to pick from.
/// </remarks>
internal static class NaturalKeyLookupResultReader
{
    /// <summary>
    /// Creates an empty match table for <paramref name="request"/>, validating that group ordinals are 1-based
    /// and sequential and that every group has entries.
    /// </summary>
    public static NaturalKeyLookupMatch?[][] CreateMatchTable(NaturalKeyLookupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var matches = new NaturalKeyLookupMatch?[request.Groups.Count][];

        for (var index = 0; index < request.Groups.Count; index++)
        {
            var group = request.Groups[index];

            if (group.GroupOrdinal != index + 1)
            {
                throw new InvalidOperationException(
                    $"Natural-key lookup group at position {index + 1} declares group ordinal "
                        + $"{group.GroupOrdinal}; group ordinals must be 1-based and sequential."
                );
            }

            if (group.Entries.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Natural-key lookup group {group.GroupOrdinal} for resource '{RelationalWriteSupport.FormatResource(group.Target.Resource)}' "
                        + "has no entries."
                );
            }

            matches[index] = new NaturalKeyLookupMatch?[group.Entries.Count];
        }

        return matches;
    }

    /// <summary>
    /// Decodes the rows of the reader's current result set into <paramref name="matches"/>. It never advances
    /// to another result set, so a composite command's later statements keep their results.
    /// </summary>
    public static async Task ReadCurrentResultSetAsync(
        IRelationalCommandReader reader,
        NaturalKeyLookupRequest request,
        NaturalKeyLookupMatch?[][] matches,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(matches);

        if (matches.Length != request.Groups.Count)
        {
            throw new ArgumentException(
                $"The match table has {matches.Length} groups but the request has {request.Groups.Count}.",
                nameof(matches)
            );
        }

        ResultOrdinals? ordinals = null;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Ordinals are resolved from the first row, because an empty result set need not describe columns.
            ordinals ??= ResultOrdinals.From(reader);

            var groupOrdinal = ReadRequired<int>(
                reader,
                ordinals.GroupOrdinal,
                NaturalKeyLookupColumns.GroupOrdinal
            );
            var ordinal = ReadRequired<int>(reader, ordinals.Ordinal, NaturalKeyLookupColumns.Ordinal);

            if (groupOrdinal < 1 || groupOrdinal > request.Groups.Count)
            {
                throw new InvalidOperationException(
                    $"Natural-key lookup returned group ordinal {groupOrdinal}, but the request has "
                        + $"{request.Groups.Count} groups."
                );
            }

            var group = request.Groups[groupOrdinal - 1];
            var groupMatches = matches[groupOrdinal - 1];

            if (ordinal < 1 || ordinal > groupMatches.Length)
            {
                throw new InvalidOperationException(
                    $"Natural-key lookup returned ordinal {ordinal} for group {groupOrdinal} "
                        + $"('{RelationalWriteSupport.FormatResource(group.Target.Resource)}'), which has {groupMatches.Length} entries."
                );
            }

            if (groupMatches[ordinal - 1] is not null)
            {
                throw new ReferenceLookupCorruptionException(
                    $"Natural-key lookup matched more than one row for ordinal {ordinal} of group {groupOrdinal} "
                        + $"('{RelationalWriteSupport.FormatResource(group.Target.Resource)}'). The identity must match at most one row, so the "
                        + "natural-key uniqueness invariant is broken."
                );
            }

            var match = new NaturalKeyLookupMatch(
                DocumentId: ReadRequired<long>(
                    reader,
                    ordinals.DocumentId,
                    NaturalKeyLookupColumns.DocumentId
                ),
                DescriptorId: reader.IsDBNull(ordinals.DescriptorId)
                    ? null
                    : reader.GetFieldValue<int>(ordinals.DescriptorId),
                ResourceKeyId: ReadRequired<short>(
                    reader,
                    ordinals.ResourceKeyId,
                    NaturalKeyLookupColumns.ResourceKeyId
                )
            );

            EnsureIdRoles(group, ordinal, match);
            groupMatches[ordinal - 1] = match;
        }
    }

    /// <summary>
    /// Decodes every result set of a standalone lookup command, which returns one result set per group.
    /// </summary>
    public static async Task<NaturalKeyLookupMatch?[][]> ReadAllResultSetsAsync(
        IRelationalCommandReader reader,
        NaturalKeyLookupRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        var matches = CreateMatchTable(request);

        do
        {
            await ReadCurrentResultSetAsync(reader, request, matches, cancellationToken)
                .ConfigureAwait(false);
        } while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        return matches;
    }

    /// <summary>
    /// The composite statement read callback: decodes only the embedded lookup's result set. The composite
    /// executor owns the reader and its result-set position, so the reader is neither disposed nor advanced.
    /// </summary>
    public static async Task<object?> ReadCompositeResultSetAsync(
        DbDataReader reader,
        NaturalKeyLookupRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        var matches = CreateMatchTable(request);

        await ReadCurrentResultSetAsync(
                new DbRelationalCommandReader(reader),
                request,
                matches,
                cancellationToken
            )
            .ConfigureAwait(false);

        return matches;
    }

    /// <summary>
    /// Keeps the two document keys in their roles: only descriptor groups return a compact
    /// <c>DescriptorId</c>, and concrete and descriptor groups must return the target's own key.
    /// </summary>
    private static void EnsureIdRoles(NaturalKeyLookupGroup group, int ordinal, NaturalKeyLookupMatch match)
    {
        var target = group.Target;

        if (target.IsDescriptor && match.DescriptorId is null)
        {
            throw new InvalidOperationException(
                $"Natural-key lookup row for ordinal {ordinal} of descriptor group {group.GroupOrdinal} "
                    + $"('{RelationalWriteSupport.FormatResource(target.Resource)}') has no DescriptorId."
            );
        }

        if (!target.IsDescriptor && match.DescriptorId is not null)
        {
            throw new InvalidOperationException(
                $"Natural-key lookup row for ordinal {ordinal} of group {group.GroupOrdinal} "
                    + $"('{RelationalWriteSupport.FormatResource(target.Resource)}') returned a DescriptorId, but the group is not a "
                    + "descriptor target."
            );
        }

        var requiresTargetKey =
            target.IsDescriptor || target.Probe!.Kind is NaturalKeyProbeTargetKind.Concrete;

        if (requiresTargetKey && match.ResourceKeyId != target.ResourceKeyId)
        {
            throw new InvalidOperationException(
                $"Natural-key lookup row for ordinal {ordinal} of group {group.GroupOrdinal} "
                    + $"('{RelationalWriteSupport.FormatResource(target.Resource)}') returned ResourceKeyId {match.ResourceKeyId}, but the "
                    + $"group requires {target.ResourceKeyId}."
            );
        }
    }

    private static T ReadRequired<T>(IRelationalCommandReader reader, int ordinal, string columnName)
    {
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException(
                $"Natural-key lookup returned NULL for required column '{columnName}'."
            );
        }

        return reader.GetFieldValue<T>(ordinal);
    }

    private sealed record ResultOrdinals(
        int GroupOrdinal,
        int Ordinal,
        int DocumentId,
        int DescriptorId,
        int ResourceKeyId
    )
    {
        public static ResultOrdinals From(IRelationalCommandReader reader) =>
            new(
                reader.GetOrdinal(NaturalKeyLookupColumns.GroupOrdinal),
                reader.GetOrdinal(NaturalKeyLookupColumns.Ordinal),
                reader.GetOrdinal(NaturalKeyLookupColumns.DocumentId),
                reader.GetOrdinal(NaturalKeyLookupColumns.DescriptorId),
                reader.GetOrdinal(NaturalKeyLookupColumns.ResourceKeyId)
            );
    }
}
