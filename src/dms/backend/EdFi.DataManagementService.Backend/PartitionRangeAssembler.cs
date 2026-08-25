// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Turns the starting anchor values the partition statement returns into typed inclusive ranges.
/// </summary>
/// <remarks>
/// Pure, provider-neutral, and anchor-neutral: the starts are <c>DocumentId</c> or
/// <c>ContentVersion</c> values depending on how the request resolved its anchor, and both are dense
/// integer sequences, so the same arithmetic assembles both. Both providers return the same ordered
/// starts for equivalent data, so both produce the same ranges and therefore the same tokens. Core
/// encodes the ranges; nothing here touches token text.
/// </remarks>
internal static class PartitionRangeAssembler
{
    /// <summary>
    /// Converts ascending starting anchor values into inclusive ranges. Every range but the last ends
    /// one less than the next start, so a document that arrives later cannot move into a partition a
    /// client has already finished; the last is unbounded above, so newly created documents are still
    /// reachable.
    /// </summary>
    /// <param name="ascendingStarts">
    /// The starting anchor values, strictly ascending. Empty when no candidates are accessible.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the starts are not strictly ascending. The statement orders them, so a violation
    /// means the compiled SQL changed rather than that a client sent something unusual — and repairing
    /// it silently would hand out a duplicated or inverted range that returns nothing.
    /// <para>
    /// Under a <c>ContentVersion</c> anchor this guard also carries the uniqueness assumption that
    /// anchor rests on. Boundaries are cut at row numbers, so two candidates sharing one
    /// <c>ContentVersion</c> would put the same value at two boundaries and produce an inverted range
    /// between them. The change-version sequence assigns a distinct value per document write, so a
    /// duplicate here means that invariant was broken upstream, not that this input is unusual.
    /// </para>
    /// </exception>
    public static IReadOnlyList<CursorRange> ToInclusiveRanges(IReadOnlyList<long> ascendingStarts)
    {
        ArgumentNullException.ThrowIfNull(ascendingStarts);

        if (ascendingStarts.Count == 0)
        {
            return [];
        }

        var ranges = new CursorRange[ascendingStarts.Count];

        for (var index = 0; index < ascendingStarts.Count; index++)
        {
            var start = ascendingStarts[index];

            if (index > 0 && start <= ascendingStarts[index - 1])
            {
                throw new ArgumentException(
                    "Partition start anchor values must be strictly ascending, but "
                        + $"{start.ToString(CultureInfo.InvariantCulture)} at index "
                        + $"{index.ToString(CultureInfo.InvariantCulture)} does not follow "
                        + $"{ascendingStarts[index - 1].ToString(CultureInfo.InvariantCulture)}.",
                    nameof(ascendingStarts)
                );
            }

            // Strictly ascending starts are what make the subtraction safe: only the first start can be
            // long.MinValue, so a following start is always greater than it and cannot underflow.
            var inclusiveMaximum =
                index + 1 < ascendingStarts.Count ? ascendingStarts[index + 1] - 1 : long.MaxValue;

            ranges[index] = new CursorRange(start, inclusiveMaximum);
        }

        return ranges;
    }
}
