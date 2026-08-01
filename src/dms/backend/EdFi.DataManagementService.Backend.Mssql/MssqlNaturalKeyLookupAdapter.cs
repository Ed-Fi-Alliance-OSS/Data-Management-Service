// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// Executes a natural-key lookup batch as one SQL Server command per parameter-budget slice.
/// </summary>
/// <remarks>
/// SQL Server's 2100-parameter ceiling is per command, and each entry binds one parameter per probe
/// column, so a batch that would bind more than
/// <see cref="MssqlNaturalKeyLookupCommandBuilder.MssqlMaxCommandParameters" /> parameters is sliced here —
/// splitting groups across slices and, when one group alone is too wide, splitting that group's entries.
/// Each slice re-ordinals its entries from 1 (the builders' contract), so every returned row is mapped
/// back to the caller's <c>(GroupIndex, Ordinal)</c> coordinates before it leaves this adapter. A batch
/// that fits — which is every realistic request — is exactly one command and one round trip.
/// </remarks>
internal sealed class MssqlNaturalKeyLookupAdapter(IRelationalCommandExecutor commandExecutor)
    : INaturalKeyLookupAdapter
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public async Task<IReadOnlyList<NaturalKeyLookupRow>> ResolveAsync(
        NaturalKeyLookupBatch batch,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(batch);

        List<NaturalKeyLookupRow> rows = [];

        foreach (var slice in SliceBatch(batch))
        {
            var sliceRows = await _commandExecutor
                .ExecuteReaderAsync(
                    MssqlNaturalKeyLookupCommandBuilder.Build(slice.Batch),
                    (reader, token) => NaturalKeyLookupResultReader.ReadAsync(slice.Batch, reader, token),
                    cancellationToken
                )
                .ConfigureAwait(false);

            foreach (var row in sliceRows)
            {
                var origin = slice.Origins[row.GroupIndex];

                rows.Add(
                    row with
                    {
                        GroupIndex = origin.GroupIndex,
                        Ordinal = row.Ordinal + origin.EntryOffset,
                    }
                );
            }
        }

        return rows;
    }

    /// <summary>
    /// Splits <paramref name="batch" /> into command-sized slices, each within the driver's parameter
    /// ceiling, recording where every slice group came from so its rows can be re-attributed.
    /// </summary>
    internal static IReadOnlyList<NaturalKeyLookupBatchSlice> SliceBatch(NaturalKeyLookupBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        List<NaturalKeyLookupBatchSlice> slices = [];
        List<NaturalKeyLookupGroup> sliceGroups = [];
        List<NaturalKeyLookupGroupOrigin> sliceOrigins = [];
        var sliceParameterCount = 0;

        void FlushSlice()
        {
            if (sliceGroups.Count == 0)
            {
                return;
            }

            slices.Add(
                new NaturalKeyLookupBatchSlice(
                    new NaturalKeyLookupBatch(batch.MappingSet, [.. sliceGroups]),
                    [.. sliceOrigins]
                )
            );
            sliceGroups.Clear();
            sliceOrigins.Clear();
            sliceParameterCount = 0;
        }

        for (var groupIndex = 0; groupIndex < batch.Groups.Count; groupIndex++)
        {
            var group = batch.Groups[groupIndex];
            var probeWidth = Math.Max(1, NaturalKeyLookupCommandSupport.ProbeValueCount(group));

            if (probeWidth > MssqlNaturalKeyLookupCommandBuilder.MssqlMaxCommandParameters)
            {
                throw new NotSupportedException(
                    $"SQL Server natural-key lookup cannot resolve target "
                        + $"'{group.Target.ProjectName}/{group.Target.ResourceName}': its probe binds {probeWidth} "
                        + $"parameters per entry, over the "
                        + $"{MssqlNaturalKeyLookupCommandBuilder.MssqlMaxCommandParameters}-parameter command ceiling."
                );
            }

            var entryOffset = 0;

            do
            {
                var remainingCapacity =
                    (MssqlNaturalKeyLookupCommandBuilder.MssqlMaxCommandParameters - sliceParameterCount)
                    / probeWidth;

                if (remainingCapacity <= 0 && group.Entries.Count > 0)
                {
                    FlushSlice();
                    continue;
                }

                var takeCount = Math.Min(remainingCapacity, group.Entries.Count - entryOffset);

                sliceGroups.Add(CreateSliceGroup(group, entryOffset, takeCount));
                sliceOrigins.Add(new NaturalKeyLookupGroupOrigin(groupIndex, entryOffset));
                sliceParameterCount += takeCount * probeWidth;
                entryOffset += takeCount;

                if (entryOffset < group.Entries.Count)
                {
                    FlushSlice();
                }
            } while (entryOffset < group.Entries.Count);
        }

        FlushSlice();

        return slices;
    }

    private static NaturalKeyLookupGroup CreateSliceGroup(
        NaturalKeyLookupGroup group,
        int entryOffset,
        int takeCount
    )
    {
        NaturalKeyLookupEntry[] entries =
        [
            .. Enumerable
                .Range(0, takeCount)
                .Select(index => new NaturalKeyLookupEntry(
                    index + 1,
                    group.Entries[entryOffset + index].Values
                )),
        ];

        return group switch
        {
            NaturalKeyProbeLookupGroup probeGroup => new NaturalKeyProbeLookupGroup(
                probeGroup.Target,
                probeGroup.Probe,
                entries
            ),
            DescriptorLookupGroup descriptorGroup => new DescriptorLookupGroup(
                descriptorGroup.Target,
                entries
            ),
            _ => throw new NotSupportedException(
                $"Unsupported natural-key lookup group kind '{group.GetType().Name}'."
            ),
        };
    }
}

/// <summary>
/// One command-sized slice of a natural-key lookup batch plus the origin of each of its groups.
/// </summary>
internal sealed record NaturalKeyLookupBatchSlice(
    NaturalKeyLookupBatch Batch,
    IReadOnlyList<NaturalKeyLookupGroupOrigin> Origins
);

/// <summary>
/// Where a slice group's entries came from in the original batch.
/// </summary>
/// <param name="GroupIndex">The original batch group index.</param>
/// <param name="EntryOffset">
/// The zero-based offset of the slice group's first entry within the original group, added back to every
/// returned ordinal.
/// </param>
internal sealed record NaturalKeyLookupGroupOrigin(int GroupIndex, int EntryOffset);
