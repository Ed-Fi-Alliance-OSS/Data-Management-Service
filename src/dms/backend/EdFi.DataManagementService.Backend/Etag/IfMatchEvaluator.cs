// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend.Etag;

/// <summary>Outcome of an If-Match evaluation. Only <see cref="IsMatch"/> today; a Reason field is
/// added by later follow-up plans without changing callers.</summary>
public readonly record struct IfMatchResult(bool IsMatch);

/// <summary>
/// Single home for "evaluate If-Match against the current served etag". A wildcard precondition
/// (If-Match: *) matches unconditionally (existence is proven by the caller before calling). A
/// specific tag matches iff its state-significant projection equals the current tag's projection.
/// </summary>
public interface IIfMatchEvaluator
{
    IfMatchResult Evaluate(WritePrecondition.IfMatch precondition, string currentServedEtag);
}

public sealed class IfMatchEvaluator : IIfMatchEvaluator
{
    public IfMatchResult Evaluate(WritePrecondition.IfMatch precondition, string currentServedEtag)
    {
        ArgumentNullException.ThrowIfNull(precondition);

        if (precondition.IsWildcard)
        {
            return new IfMatchResult(true);
        }

        var isMatch = string.Equals(
            EtagMatchProjection.Of(precondition.Value),
            EtagMatchProjection.Of(currentServedEtag),
            StringComparison.Ordinal
        );
        return new IfMatchResult(isMatch);
    }
}
