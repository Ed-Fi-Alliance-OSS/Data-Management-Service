// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// The harness's only access to page-token text, delegating to the production
/// <see cref="PageTokenCodec" /> so crafted first/middle/last tokens and decoded partition
/// tokens can never drift from the wire format the server validates. Every final-gate cell is
/// DocumentId-anchored — no cell requests a change-version window — so only that ordering
/// mode is expressible here.
/// </summary>
public static class PerfCursorTokens
{
    /// <summary>
    /// A token whose page begins exactly at the given DocumentId (inclusive) and is unbounded
    /// above, the shape a client walking a whole collection holds mid-walk.
    /// </summary>
    public static string DocumentIdRangeFrom(long inclusiveMinimumDocumentId) =>
        PageTokenCodec.Encode(CursorRange.From(inclusiveMinimumDocumentId), PageOrderingMode.DocumentId);

    /// <summary>
    /// Decodes a server-issued token, accepting only a DocumentId-anchored range: a
    /// change-version-anchored token in a final-gate response is an observation failure, not
    /// an alternative encoding.
    /// </summary>
    public static bool TryDecodeDocumentIdRange(
        string? pageToken,
        out long inclusiveMinimum,
        out long inclusiveMaximum
    )
    {
        inclusiveMinimum = 0;
        inclusiveMaximum = 0;
        if (
            !PageTokenCodec.TryDecode(pageToken, out CursorRange? range, out PageOrderingMode orderingMode)
            || range is null
            || orderingMode != PageOrderingMode.DocumentId
        )
        {
            return false;
        }

        inclusiveMinimum = range.InclusiveMinimum;
        inclusiveMaximum = range.InclusiveMaximum;
        return true;
    }
}
