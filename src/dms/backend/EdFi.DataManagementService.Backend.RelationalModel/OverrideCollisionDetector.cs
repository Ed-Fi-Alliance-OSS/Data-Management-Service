// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Detects collisions introduced by name overrides by comparing original and final identifiers.
/// </summary>
internal sealed class OverrideCollisionDetector
{
    private const string DmsSchemaName = "dms";
    private const string DescriptorTableName = "Descriptor";
    private readonly IdentifierCollisionStage _stage = IdentifierCollisionStage.AfterOverrideNormalization;
    private readonly Dictionary<
        IdentifierCollisionScope,
        Dictionary<string, List<IdentifierCollisionSource>>
    > _sources = new();

    /// <summary>
    /// Registers a table identifier for override collision detection.
    /// </summary>
    public void RegisterTable(DbTableName table, string originalName, IdentifierCollisionOrigin origin)
    {
        Register(
            new IdentifierCollisionScope(IdentifierCollisionKind.Table, table.Schema.Value, string.Empty),
            originalName,
            table.Name,
            origin
        );
    }

    /// <summary>
    /// Registers a column identifier for override collision detection.
    /// </summary>
    public void RegisterColumn(
        DbTableName table,
        DbColumnName column,
        string originalName,
        IdentifierCollisionOrigin origin
    )
    {
        Register(
            new IdentifierCollisionScope(IdentifierCollisionKind.Column, table.Schema.Value, table.Name),
            originalName,
            column.Value,
            origin
        );
    }

    /// <summary>
    /// Throws when any overridden identifier maps multiple distinct original identifiers to the same final name.
    /// </summary>
    public void ThrowIfCollisions()
    {
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
                    .DistinctBy(source => BuildOriginKey(scope, finalName, source.Origin))
                    .ToArray();

                if (sources.Length > 1)
                {
                    collisions.Add(new IdentifierCollisionRecord(_stage, scope, sources));
                }
            }
        }

        if (collisions.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Identifier override collisions detected: "
                + string.Join("; ", collisions.Select(collision => collision.Format()))
        );
    }

    private void Register(
        IdentifierCollisionScope scope,
        string originalName,
        string finalName,
        IdentifierCollisionOrigin origin
    )
    {
        var resolvedOriginal = string.IsNullOrWhiteSpace(originalName) ? finalName : originalName;

        if (!_sources.TryGetValue(scope, out var entries))
        {
            entries = new Dictionary<string, List<IdentifierCollisionSource>>(StringComparer.Ordinal);
            _sources[scope] = entries;
        }

        if (!entries.TryGetValue(finalName, out var sources))
        {
            sources = [];
            entries[finalName] = sources;
        }

        sources.Add(new IdentifierCollisionSource(resolvedOriginal, finalName, origin));
    }

    private static string NormalizeOriginPart(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }

    private static (string Description, string ResourceLabel, string JsonPath) BuildOriginKey(
        IdentifierCollisionScope scope,
        string finalName,
        IdentifierCollisionOrigin origin
    )
    {
        var resourceLabel = NormalizeOriginPart(origin.ResourceLabel);

        if (IsSharedDescriptorElement(scope, finalName))
        {
            resourceLabel = string.Empty;
        }

        return (origin.Description, resourceLabel, NormalizeOriginPart(origin.JsonPath));
    }

    private static bool IsSharedDescriptorElement(IdentifierCollisionScope scope, string finalName)
    {
        if (!string.Equals(scope.Schema, DmsSchemaName, StringComparison.Ordinal))
        {
            return false;
        }

        return scope.Kind switch
        {
            IdentifierCollisionKind.Table => string.Equals(
                finalName,
                DescriptorTableName,
                StringComparison.Ordinal
            ),
            IdentifierCollisionKind.Column => string.Equals(
                scope.Table,
                DescriptorTableName,
                StringComparison.Ordinal
            ),
            _ => false,
        };
    }
}
