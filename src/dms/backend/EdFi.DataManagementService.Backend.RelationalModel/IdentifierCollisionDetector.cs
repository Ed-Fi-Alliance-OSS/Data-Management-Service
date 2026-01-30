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
    private readonly Dictionary<IdentifierScope, Dictionary<string, List<IdentifierSource>>> _sources = new();

    /// <summary>
    /// Creates a new collision detector for the specified dialect rules.
    /// </summary>
    /// <param name="dialectRules">The dialect rules used to shorten identifiers.</param>
    public IdentifierCollisionDetector(ISqlDialectRules dialectRules)
    {
        _dialectRules = dialectRules ?? throw new ArgumentNullException(nameof(dialectRules));
    }

    /// <summary>
    /// Registers a table identifier for collision detection.
    /// </summary>
    /// <param name="table">The table name.</param>
    /// <param name="description">A human-readable description for diagnostics.</param>
    public void RegisterTable(DbTableName table, string description)
    {
        Register(
            new IdentifierScope(IdentifierScopeKind.Table, table.Schema.Value),
            table.Name,
            new IdentifierSource(table.Name, description)
        );
    }

    /// <summary>
    /// Registers a column identifier for collision detection.
    /// </summary>
    /// <param name="table">The owning table.</param>
    /// <param name="column">The column name.</param>
    /// <param name="description">A human-readable description for diagnostics.</param>
    public void RegisterColumn(DbTableName table, DbColumnName column, string description)
    {
        Register(
            new IdentifierScope(IdentifierScopeKind.Column, table.Schema.Value, table.Name),
            column.Value,
            new IdentifierSource(column.Value, description)
        );
    }

    /// <summary>
    /// Registers a constraint identifier for collision detection.
    /// </summary>
    /// <param name="table">The table hosting the constraint.</param>
    /// <param name="constraintName">The constraint name.</param>
    /// <param name="description">A human-readable description for diagnostics.</param>
    public void RegisterConstraint(DbTableName table, string constraintName, string description)
    {
        Register(
            new IdentifierScope(IdentifierScopeKind.Constraint, table.Schema.Value),
            constraintName,
            new IdentifierSource(constraintName, description)
        );
    }

    /// <summary>
    /// Registers an index identifier for collision detection.
    /// </summary>
    /// <param name="table">The table hosting the index.</param>
    /// <param name="indexName">The index name.</param>
    /// <param name="description">A human-readable description for diagnostics.</param>
    public void RegisterIndex(DbTableName table, DbIndexName indexName, string description)
    {
        Register(
            new IdentifierScope(IdentifierScopeKind.Index, table.Schema.Value),
            indexName.Value,
            new IdentifierSource(indexName.Value, description)
        );
    }

    /// <summary>
    /// Registers a trigger identifier for collision detection.
    /// </summary>
    /// <param name="table">The table hosting the trigger.</param>
    /// <param name="triggerName">The trigger name.</param>
    /// <param name="description">A human-readable description for diagnostics.</param>
    public void RegisterTrigger(DbTableName table, DbTriggerName triggerName, string description)
    {
        Register(
            new IdentifierScope(IdentifierScopeKind.Trigger, table.Schema.Value),
            triggerName.Value,
            new IdentifierSource(triggerName.Value, description)
        );
    }

    /// <summary>
    /// Throws when any shortened identifier maps to multiple distinct original identifiers.
    /// </summary>
    public void ThrowIfCollisions()
    {
        List<IdentifierCollision> collisions = [];

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
                    .GroupBy(source => source.Name, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group =>
                        group.OrderBy(source => source.Description, StringComparer.Ordinal).First()
                    )
                    .ToArray();

                if (sources.Length > 1)
                {
                    collisions.Add(new IdentifierCollision(scope, shortenedName, sources));
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
    private void Register(IdentifierScope scope, string originalName, IdentifierSource source)
    {
        var shortenedName = _dialectRules.ShortenIdentifier(originalName);

        if (!_sources.TryGetValue(scope, out var entries))
        {
            entries = new Dictionary<string, List<IdentifierSource>>(StringComparer.Ordinal);
            _sources[scope] = entries;
        }

        if (!entries.TryGetValue(shortenedName, out var sources))
        {
            sources = [];
            entries[shortenedName] = sources;
        }

        sources.Add(source);
    }

    /// <summary>
    /// Classifies the namespace in which an identifier must be unique.
    /// </summary>
    private enum IdentifierScopeKind
    {
        Table,
        Column,
        Constraint,
        Index,
        Trigger,
    }

    /// <summary>
    /// Identifies the scope (schema and optional table) in which collisions are evaluated.
    /// </summary>
    private readonly record struct IdentifierScope(
        IdentifierScopeKind Kind,
        string Schema,
        string Table = ""
    );

    /// <summary>
    /// Represents an identifier occurrence and its human-readable description.
    /// </summary>
    private readonly record struct IdentifierSource(string Name, string Description)
    {
        /// <summary>
        /// Formats the identifier source for diagnostics.
        /// </summary>
        /// <returns>A formatted label.</returns>
        public string Format()
        {
            return Description;
        }
    }

    /// <summary>
    /// Represents a collision where multiple original identifiers shorten to the same value.
    /// </summary>
    private sealed record IdentifierCollision(
        IdentifierScope Scope,
        string ShortenedName,
        IReadOnlyList<IdentifierSource> Sources
    )
    {
        /// <summary>
        /// Formats the collision for diagnostics.
        /// </summary>
        /// <returns>A formatted collision message.</returns>
        public string Format()
        {
            var category = Scope.Kind switch
            {
                IdentifierScopeKind.Table => "table name",
                IdentifierScopeKind.Column => "column name",
                IdentifierScopeKind.Constraint => "constraint name",
                IdentifierScopeKind.Index => "index name",
                IdentifierScopeKind.Trigger => "trigger name",
                _ => "identifier",
            };

            var scope = Scope.Kind switch
            {
                IdentifierScopeKind.Column => $"in table '{Scope.Schema}.{Scope.Table}'",
                _ => $"in schema '{Scope.Schema}'",
            };

            var sources = string.Join(", ", Sources.Select(source => source.Format()));

            return $"{category} '{ShortenedName}' {scope}: {sources}";
        }
    }
}
