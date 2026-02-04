// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Detects collisions introduced by deterministic identifier shortening.
/// </summary>
internal sealed class IdentifierCollisionDetector
{
    private readonly ISqlDialectRules _dialectRules;
    private readonly IdentifierCollisionStage _stage;
    private readonly Dictionary<
        IdentifierCollisionScope,
        Dictionary<string, List<IdentifierCollisionSource>>
    > _sources = new();

    /// <summary>
    /// Creates a new collision detector for the specified dialect rules.
    /// </summary>
    /// <param name="dialectRules">The dialect rules used to shorten identifiers.</param>
    /// <param name="stage">The collision stage used for diagnostics.</param>
    public IdentifierCollisionDetector(ISqlDialectRules dialectRules, IdentifierCollisionStage stage)
    {
        _dialectRules = dialectRules ?? throw new ArgumentNullException(nameof(dialectRules));
        _stage = stage;
    }

    /// <summary>
    /// Registers a table identifier for collision detection.
    /// </summary>
    /// <param name="table">The table name.</param>
    /// <param name="origin">The collision origin details.</param>
    public void RegisterTable(DbTableName table, IdentifierCollisionOrigin origin)
    {
        Register(
            new IdentifierCollisionScope(IdentifierCollisionKind.Table, table.Schema.Value, string.Empty),
            table.Name,
            origin
        );
    }

    /// <summary>
    /// Registers a column identifier for collision detection.
    /// </summary>
    /// <param name="table">The owning table.</param>
    /// <param name="column">The column name.</param>
    /// <param name="origin">The collision origin details.</param>
    public void RegisterColumn(DbTableName table, DbColumnName column, IdentifierCollisionOrigin origin)
    {
        Register(
            new IdentifierCollisionScope(IdentifierCollisionKind.Column, table.Schema.Value, table.Name),
            column.Value,
            origin
        );
    }

    /// <summary>
    /// Registers a constraint identifier for collision detection.
    /// </summary>
    /// <param name="table">The table hosting the constraint.</param>
    /// <param name="constraintName">The constraint name.</param>
    /// <param name="origin">The collision origin details.</param>
    public void RegisterConstraint(DbTableName table, string constraintName, IdentifierCollisionOrigin origin)
    {
        Register(
            new IdentifierCollisionScope(
                IdentifierCollisionKind.Constraint,
                table.Schema.Value,
                string.Empty
            ),
            constraintName,
            origin
        );
    }

    /// <summary>
    /// Registers an index identifier for collision detection.
    /// </summary>
    /// <param name="table">The table hosting the index.</param>
    /// <param name="indexName">The index name.</param>
    /// <param name="origin">The collision origin details.</param>
    public void RegisterIndex(DbTableName table, DbIndexName indexName, IdentifierCollisionOrigin origin)
    {
        Register(
            new IdentifierCollisionScope(IdentifierCollisionKind.Index, table.Schema.Value, string.Empty),
            indexName.Value,
            origin
        );
    }

    /// <summary>
    /// Registers a trigger identifier for collision detection.
    /// </summary>
    /// <param name="table">The table hosting the trigger.</param>
    /// <param name="triggerName">The trigger name.</param>
    /// <param name="origin">The collision origin details.</param>
    public void RegisterTrigger(
        DbTableName table,
        DbTriggerName triggerName,
        IdentifierCollisionOrigin origin
    )
    {
        Register(
            new IdentifierCollisionScope(IdentifierCollisionKind.Trigger, table.Schema.Value, string.Empty),
            triggerName.Value,
            origin
        );
    }

    /// <summary>
    /// Throws when any shortened identifier maps to multiple distinct original identifiers.
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

            foreach (var shortenedName in names.Keys.OrderBy(name => name, StringComparer.Ordinal))
            {
                var sources = names[shortenedName]
                    .GroupBy(source => source.OriginalIdentifier, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group =>
                        group
                            .OrderBy(source => source.Origin.Description, StringComparer.Ordinal)
                            .ThenBy(
                                source => source.Origin.ResourceLabel ?? string.Empty,
                                StringComparer.Ordinal
                            )
                            .ThenBy(source => source.Origin.JsonPath ?? string.Empty, StringComparer.Ordinal)
                            .First()
                    )
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
            "Identifier shortening collisions detected: "
                + string.Join("; ", collisions.Select(collision => collision.Format()))
        );
    }

    /// <summary>
    /// Registers a single identifier in the detector, tracking both the original and shortened forms.
    /// </summary>
    private void Register(
        IdentifierCollisionScope scope,
        string originalName,
        IdentifierCollisionOrigin origin
    )
    {
        var shortenedName = _dialectRules.ShortenIdentifier(originalName);

        if (!_sources.TryGetValue(scope, out var entries))
        {
            entries = new Dictionary<string, List<IdentifierCollisionSource>>(StringComparer.Ordinal);
            _sources[scope] = entries;
        }

        if (!entries.TryGetValue(shortenedName, out var sources))
        {
            sources = [];
            entries[shortenedName] = sources;
        }

        sources.Add(new IdentifierCollisionSource(originalName, shortenedName, origin));
    }
}
