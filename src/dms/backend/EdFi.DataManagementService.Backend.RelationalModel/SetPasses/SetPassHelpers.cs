// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Shared helper types and methods used across multiple set-level derivation passes.
/// </summary>
internal static class SetPassHelpers
{
    /// <summary>
    /// Counts the number of array wildcard segments in the scope, used for depth-first ordering.
    /// </summary>
    internal static int CountArrayDepth(JsonPathExpression scope)
    {
        return scope.Segments.Count(segment => segment is JsonPathSegment.AnyArrayElement);
    }
}

/// <summary>
/// Captures a concrete resource model and its index within the builder's canonical resource ordering.
/// Used by multiple set passes that need to track both the model and its positional index.
/// </summary>
internal sealed record BaseResourceEntry(int Index, ConcreteResourceModel Model);
