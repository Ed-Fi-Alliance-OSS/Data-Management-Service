// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// Which operation a resource path names: the collection itself, one item by document uuid, or the
/// sibling partitions operation.
/// </summary>
/// <remarks>
/// An explicit choice rather than an optional identifier plus a route-shape flag, so "by id with no
/// uuid" and "collection carrying a uuid" are both unrepresentable. The hierarchy is closed by a
/// private constructor, so every consumer can pattern match it exhaustively.
/// </remarks>
internal abstract record ResourcePathOperation
{
    private ResourcePathOperation() { }

    /// <summary>
    /// The resource collection, with no third route segment.
    /// </summary>
    public sealed record Collection : ResourcePathOperation
    {
        internal static Collection Instance { get; } = new();
    }

    /// <summary>
    /// One item of the collection, addressed by document uuid.
    /// </summary>
    /// <param name="DocumentUuid">The well-formed document uuid from the third route segment.</param>
    public sealed record ById(DocumentUuid DocumentUuid) : ResourcePathOperation;

    /// <summary>
    /// The sibling partitions operation on the collection.
    /// </summary>
    public sealed record Partitions : ResourcePathOperation
    {
        internal static Partitions Instance { get; } = new();
    }
}
