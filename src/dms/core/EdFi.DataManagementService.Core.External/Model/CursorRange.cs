// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// An inclusive DocumentId window for cursor page selection. Bounds are signed long-width to match
/// the relational bigint DocumentId identity.
/// </summary>
/// <remarks>
/// Negative bounds, and a minimum greater than the maximum, are valid match-nothing ranges rather
/// than errors. An inverted range is how a bounded partition reaches its terminal empty page after
/// returning the item at its upper bound.
/// </remarks>
public sealed record CursorRange(long InclusiveMinimum, long InclusiveMaximum)
{
    /// <summary>
    /// A range starting at the given inclusive minimum and unbounded above.
    /// </summary>
    public static CursorRange From(long inclusiveMinimum) => new(inclusiveMinimum, long.MaxValue);
}
