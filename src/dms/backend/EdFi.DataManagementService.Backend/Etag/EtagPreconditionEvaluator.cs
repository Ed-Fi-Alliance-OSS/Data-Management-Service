// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>
/// Decides whether a write may proceed under an HTTP conditional precondition, given whether the
/// target currently exists and (when it does) its composed served etag. If-Match and If-None-Match
/// compare the same state-significant projection (ContentVersion, schemaEpoch); only the polarity
/// differs. Reads use full-tag comparison and are handled in the read handler, not here.
/// </summary>
internal static class EtagPreconditionEvaluator
{
    public static bool IsSatisfied(WritePrecondition precondition, bool targetExists, string? currentEtag) =>
        precondition switch
        {
            WritePrecondition.IfMatch m => targetExists
                && (m.IsWildcard || ProjectionEquals(m.Value, currentEtag)),
            WritePrecondition.IfNoneMatch n => !targetExists
                || (!n.IsWildcard && !ProjectionEquals(n.Value, currentEtag)),
            _ => true, // None (and any future arm) imposes no precondition here.
        };

    private static bool ProjectionEquals(string clientTag, string? currentEtag) =>
        string.Equals(
            EtagMatchProjection.Of(clientTag),
            EtagMatchProjection.Of(currentEtag),
            StringComparison.Ordinal
        );
}
