// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Binary;
using System.Text;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

/// <summary>Expected partition for canonical UUID keys, checked against the existing Kafka vectors.</summary>
internal static class MessageContractPartition
{
    public static int ForUuid(string uuid, int partitionCount, string algorithm)
    {
        if (
            algorithm != CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm
            || partitionCount <= 0
            || !Guid.TryParseExact(uuid, "D", out Guid parsed)
            || parsed.ToString("D") != uuid
        )
        {
            throw new ArgumentException("Invalid CDC partition expectation.");
        }

        // Canonical UUID UTF-8 is exactly 36 bytes, so Murmur2 has no trailing partial word.
        ReadOnlySpan<byte> bytes = Encoding.UTF8.GetBytes(uuid);
        unchecked
        {
            const uint multiplier = 0x5bd1e995;
            uint hash = 0x9747b28c ^ (uint)bytes.Length;
            for (int offset = 0; offset < bytes.Length; offset += 4)
            {
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
                word *= multiplier;
                word ^= word >> 24;
                word *= multiplier;
                hash = (hash * multiplier) ^ word;
            }
            hash ^= hash >> 13;
            hash *= multiplier;
            hash ^= hash >> 15;
            return (int)(hash & 0x7fffffff) % partitionCount;
        }
    }
}
