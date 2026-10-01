// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>
/// Decides whether a write may proceed under an HTTP conditional precondition, given whether the
/// target currently exists and (when it does) its state-significant ETag projection. If-Match
/// compares the projection (ContentVersion, schemaEpoch). Reads use full-tag comparison and are handled in the read handler, not here.
/// </summary>
internal static class EtagPreconditionEvaluator
{
    public static bool IsSatisfied(WritePrecondition precondition, bool targetExists, string? currentEtag) =>
        IsSatisfiedAgainstProjection(precondition, targetExists, EtagMatchProjection.Of(currentEtag));

    public static bool IsSatisfiedByCurrentState(
        WritePrecondition precondition,
        long contentVersion,
        string effectiveSchemaHash
    ) =>
        IsSatisfiedAgainstProjection(
            precondition,
            targetExists: true,
            EtagMatchProjection.OfCurrentState(contentVersion, effectiveSchemaHash)
        );

    private static bool IsSatisfiedAgainstProjection(
        WritePrecondition precondition,
        bool targetExists,
        string currentProjection
    ) =>
        precondition switch
        {
            WritePrecondition.None => true,
            WritePrecondition.IfMatch m => targetExists
                && (m.IsWildcard || ProjectionEquals(m.Value, currentProjection)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(precondition),
                precondition,
                "Unsupported write precondition type."
            ),
        };

    public static ETagPreconditionFailureReason GetFailureReason(WritePrecondition precondition) =>
        precondition switch
        {
            WritePrecondition.IfMatch => ETagPreconditionFailureReason.Concurrency,
            _ => throw new ArgumentException(
                "An ETag precondition failure reason requires If-Match.",
                nameof(precondition)
            ),
        };

    private static bool ProjectionEquals(string clientTag, string currentProjection) =>
        string.Equals(EtagMatchProjection.Of(clientTag), currentProjection, StringComparison.Ordinal);
}
