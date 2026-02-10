// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;

namespace EdFi.DataManagementService.Backend.RelationalModel.Diagnostics;

/// <summary>
/// Aggregates identifier sources by scope and final name, and emits deterministic collision diagnostics.
/// </summary>
internal sealed class CollisionDetectorCore
{
    private readonly Dictionary<
        IdentifierCollisionScope,
        Dictionary<string, List<IdentifierCollisionSource>>
    > _sources = new();

    /// <summary>
    /// Registers a single identifier source under the specified collision scope.
    /// </summary>
    public void Register(
        IdentifierCollisionScope scope,
        string finalIdentifier,
        IdentifierCollisionSource source
    )
    {
        if (!_sources.TryGetValue(scope, out var entries))
        {
            entries = new Dictionary<string, List<IdentifierCollisionSource>>(StringComparer.Ordinal);
            _sources[scope] = entries;
        }

        if (!entries.TryGetValue(finalIdentifier, out var sources))
        {
            sources = [];
            entries[finalIdentifier] = sources;
        }

        sources.Add(source);
    }

    /// <summary>
    /// Throws an <see cref="InvalidOperationException"/> when any collisions are detected.
    /// </summary>
    public void ThrowIfCollisions(
        IdentifierCollisionStage stage,
        string messagePrefix,
        Func<IdentifierCollisionScope, string, IdentifierCollisionOrigin, bool> isSharedDescriptorElement
    )
    {
        ArgumentNullException.ThrowIfNull(messagePrefix);

        var collisions = CollectCollisions(stage, isSharedDescriptorElement);

        if (collisions.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            messagePrefix + string.Join("; ", collisions.Select(collision => collision.Format()))
        );
    }

    /// <summary>
    /// Collects collision records where a single final identifier maps to multiple distinct origins.
    /// </summary>
    public IReadOnlyList<IdentifierCollisionRecord> CollectCollisions(
        IdentifierCollisionStage stage,
        Func<IdentifierCollisionScope, string, IdentifierCollisionOrigin, bool> isSharedDescriptorElement
    )
    {
        ArgumentNullException.ThrowIfNull(isSharedDescriptorElement);

        List<IdentifierCollisionRecord> collisions = [];

        var orderedScopes = _sources
            .Keys.OrderBy(scope => scope.Kind)
            .ThenBy(scope => scope.Schema, StringComparer.Ordinal)
            .ThenBy(scope => scope.Table, StringComparer.Ordinal)
            .ToArray();

        foreach (var scope in orderedScopes)
        {
            var names = _sources[scope];

            foreach (var finalName in names.Keys.OrderBy(name => name, StringComparer.Ordinal))
            {
                var sources = names[finalName]
                    .OrderBy(source => source.OriginalIdentifier, StringComparer.Ordinal)
                    .ThenBy(source => source.Origin.Description, StringComparer.Ordinal)
                    .ThenBy(
                        source => NormalizeOriginPart(source.Origin.ResourceLabel),
                        StringComparer.Ordinal
                    )
                    .ThenBy(source => NormalizeOriginPart(source.Origin.JsonPath), StringComparer.Ordinal)
                    .ThenBy(source => source.FinalIdentifier, StringComparer.Ordinal)
                    .DistinctBy(source =>
                        BuildOriginKey(scope, finalName, source.Origin, isSharedDescriptorElement)
                    )
                    .ToArray();

                if (sources.Length > 1)
                {
                    collisions.Add(new IdentifierCollisionRecord(stage, scope, sources));
                }
            }
        }

        return collisions;
    }

    /// <summary>
    /// Normalizes an optional origin value for deterministic sorting.
    /// </summary>
    private static string NormalizeOriginPart(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }

    /// <summary>
    /// Builds a de-duplication key for a collision origin, collapsing shared descriptor elements across
    /// resources when <paramref name="isSharedDescriptorElement"/> returns true.
    /// </summary>
    private static (string Description, string ResourceLabel, string JsonPath) BuildOriginKey(
        IdentifierCollisionScope scope,
        string finalName,
        IdentifierCollisionOrigin origin,
        Func<IdentifierCollisionScope, string, IdentifierCollisionOrigin, bool> isSharedDescriptorElement
    )
    {
        var resourceLabel = NormalizeOriginPart(origin.ResourceLabel);

        if (isSharedDescriptorElement(scope, finalName, origin))
        {
            resourceLabel = string.Empty;
        }

        return (origin.Description, resourceLabel, NormalizeOriginPart(origin.JsonPath));
    }
}
