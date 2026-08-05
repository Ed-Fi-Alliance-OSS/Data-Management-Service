// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// A partition-boundary result from a partition handler.
/// </summary>
/// <remarks>
/// Provider-neutral by construction: it carries typed inclusive DocumentId ranges only, never token
/// text and no provider syntax. Core encodes each range as a page token at the HTTP contract boundary.
/// </remarks>
public abstract record PartitionResult
{
    /// <summary>
    /// Successful partition boundaries, ascending. An empty list means no accessible candidates.
    /// </summary>
    /// <param name="Ranges">
    /// The inclusive DocumentId ranges a client can walk independently. Every range but the last is
    /// bounded above, so a later insert cannot move into a completed partition.
    /// </param>
    public sealed record PartitionSuccess(IReadOnlyList<CursorRange> Ranges) : PartitionResult;

    private PartitionResult() { }
}
