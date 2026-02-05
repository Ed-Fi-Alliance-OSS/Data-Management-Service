// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Detects collisions introduced by deterministic identifier shortening.
/// </summary>
internal sealed class IdentifierCollisionDetector
{
    private const string DmsSchemaName = "dms";
    private const string DescriptorTableName = "Descriptor";
    private readonly CollisionDetectorCore _core = new();
    private readonly ISqlDialectRules _dialectRules;
    private readonly IdentifierCollisionStage _stage;

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
    /// Registers a schema identifier for collision detection.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="origin">The collision origin details.</param>
    public void RegisterSchema(DbSchemaName schema, IdentifierCollisionOrigin origin)
    {
        Register(
            new IdentifierCollisionScope(IdentifierCollisionKind.Schema, string.Empty, string.Empty),
            schema.Value,
            origin
        );
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
        Register(BuildIndexScope(table), indexName.Value, origin);
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
        Register(BuildTriggerScope(table), triggerName.Value, origin);
    }

    /// <summary>
    /// Throws when any shortened identifier maps to multiple distinct original identifiers.
    /// </summary>
    public void ThrowIfCollisions()
    {
        _core.ThrowIfCollisions(
            _stage,
            "Identifier shortening collisions detected: ",
            IsSharedDescriptorElement
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

        _core.Register(
            scope,
            shortenedName,
            new IdentifierCollisionSource(originalName, shortenedName, origin)
        );
    }

    private IdentifierCollisionScope BuildIndexScope(DbTableName table)
    {
        return _dialectRules.Dialect switch
        {
            SqlDialect.Pgsql => new IdentifierCollisionScope(
                IdentifierCollisionKind.Index,
                table.Schema.Value,
                string.Empty
            ),
            SqlDialect.Mssql => new IdentifierCollisionScope(
                IdentifierCollisionKind.Index,
                table.Schema.Value,
                table.Name
            ),
            _ => new IdentifierCollisionScope(
                IdentifierCollisionKind.Index,
                table.Schema.Value,
                string.Empty
            ),
        };
    }

    private IdentifierCollisionScope BuildTriggerScope(DbTableName table)
    {
        return _dialectRules.Dialect switch
        {
            SqlDialect.Pgsql => new IdentifierCollisionScope(
                IdentifierCollisionKind.Trigger,
                table.Schema.Value,
                table.Name
            ),
            SqlDialect.Mssql => new IdentifierCollisionScope(
                IdentifierCollisionKind.Trigger,
                table.Schema.Value,
                string.Empty
            ),
            _ => new IdentifierCollisionScope(
                IdentifierCollisionKind.Trigger,
                table.Schema.Value,
                string.Empty
            ),
        };
    }

    private static bool IsSharedDescriptorElement(
        IdentifierCollisionScope scope,
        string finalName,
        IdentifierCollisionOrigin origin
    )
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
            IdentifierCollisionKind.Constraint
            or IdentifierCollisionKind.Index
            or IdentifierCollisionKind.Trigger => origin.Description.Contains(
                $"{DmsSchemaName}.{DescriptorTableName}",
                StringComparison.Ordinal
            ),
            _ => false,
        };
    }
}
