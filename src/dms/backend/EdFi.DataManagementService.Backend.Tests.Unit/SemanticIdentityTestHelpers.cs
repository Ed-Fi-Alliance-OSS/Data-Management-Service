// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Test-only helpers for materializing <see cref="SemanticIdentityPart"/> values without
/// the JSON-side presence probes used by production code. Lives in the test project so
/// the inference fallback is invisible to production callers.
/// </summary>
internal static class SemanticIdentityTestHelpers
{
    /// <summary>
    /// Lossy inference helper for tests that do not need to differentiate a missing
    /// nullable identity property from an explicit JSON null. Treats every non-null
    /// value as <c>IsPresent: true</c> and every null value as <c>IsPresent: false</c>,
    /// which collapses the missing-vs-explicit-null distinction the
    /// <see cref="SemanticIdentityPart"/> contract preserves.
    /// </summary>
    /// <remarks>
    /// Production callers MUST build <see cref="SemanticIdentityPart"/> instances from
    /// JSON-side presence probes (see
    /// <c>RelationalWriteFlattener.MaterializeSemanticIdentityParts</c>) and pass them
    /// to the <c>CollectionWriteCandidate</c> constructor. This helper exists only so
    /// test fixtures whose scenarios do not exercise presence semantics can opt into
    /// the inference at the call site — keeping the fallback visible rather than
    /// implicit.
    /// </remarks>
    public static ImmutableArray<SemanticIdentityPart> InferSemanticIdentityInOrderForTests(
        TableWritePlan tableWritePlan,
        IEnumerable<object?> semanticIdentityValues
    )
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        ArgumentNullException.ThrowIfNull(semanticIdentityValues);

        var mergePlan =
            tableWritePlan.CollectionMergePlan
            ?? throw new ArgumentException(
                $"{nameof(tableWritePlan)} must have a {nameof(TableWritePlan.CollectionMergePlan)}.",
                nameof(tableWritePlan)
            );

        var values = semanticIdentityValues.ToArray();

        if (values.Length != mergePlan.SemanticIdentityBindings.Length)
        {
            throw new ArgumentException(
                $"{nameof(semanticIdentityValues)} must contain one entry per compiled semantic identity binding. "
                    + $"Expected {mergePlan.SemanticIdentityBindings.Length}, actual {values.Length}.",
                nameof(semanticIdentityValues)
            );
        }

        var builder = ImmutableArray.CreateBuilder<SemanticIdentityPart>(
            mergePlan.SemanticIdentityBindings.Length
        );

        for (var i = 0; i < mergePlan.SemanticIdentityBindings.Length; i++)
        {
            var value = values[i];
            builder.Add(
                new SemanticIdentityPart(
                    mergePlan.SemanticIdentityBindings[i].RelativePath.Canonical,
                    value is null ? null : JsonValue.Create(value),
                    IsPresent: value is not null
                )
            );
        }

        return builder.MoveToImmutable();
    }
}
