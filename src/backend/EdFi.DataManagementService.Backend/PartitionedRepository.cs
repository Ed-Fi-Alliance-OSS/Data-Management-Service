// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend
{
    /// <summary>
    /// Abstract base class for all repositories that need a consistent way to determine a partition key given a documentUuid
    /// </summary>
    public abstract class PartitionedRepository
    {
        // Returns an integer in the range 0..15 from the last byte of a Guid (e.g. DocumentUuid, ReferentialId)
        private static int PartitionKeyFor(Guid uuid)
        {
            byte lastByte = uuid.ToByteArray()[^1];
            return lastByte % 16;
        }

        // Returns an integer in the range 0..15 from the last byte of a DocumentUuid
        protected static int PartitionKeyFor(DocumentUuid documentUuid)
        {
            return PartitionKeyFor(documentUuid.Value);
        }

        // Returns an integer in the range 0..15 from the last byte of a DocumentUuid
        protected static int PartitionKeyFor(ReferentialId referentialId)
        {
            return PartitionKeyFor(referentialId.Value);
        }
    }
}
