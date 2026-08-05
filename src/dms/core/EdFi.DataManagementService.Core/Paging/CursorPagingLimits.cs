// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Paging;

/// <summary>
/// Sizing limits derived from the configured maximum page size.
/// </summary>
internal static class CursorPagingLimits
{
    /// <summary>
    /// A partition is at least this many maximum-sized pages, so a small collection is not sliced into
    /// partitions that cost more to coordinate than to read.
    /// </summary>
    internal const int MinimumPartitionPageMultiplier = 5;

    /// <summary>
    /// The minimum partition size in candidate rows.
    /// </summary>
    /// <remarks>
    /// The cast precedes the multiplication so the product is computed in 64-bit width. Multiplying in
    /// 32-bit width would wrap negative at a large configured page size, which would defeat the
    /// max(computed, minimum) guard and produce absurd partition counts. The checked context pins that
    /// required arithmetic shape.
    /// </remarks>
    internal static long MinimumPartitionSize(int maximumPageSize) =>
        checked((long)maximumPageSize * MinimumPartitionPageMultiplier);
}
