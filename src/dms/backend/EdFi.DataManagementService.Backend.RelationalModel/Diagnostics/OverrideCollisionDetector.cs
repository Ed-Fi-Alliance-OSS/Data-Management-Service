// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Diagnostics;

/// <summary>
/// Detects collisions introduced by name overrides by comparing original and final identifiers.
/// </summary>
internal sealed class OverrideCollisionDetector
{
    private const string DmsSchemaName = "dms";
    private const string DescriptorTableName = "Descriptor";
    private readonly CollisionDetectorCore _core = new();
    private readonly IdentifierCollisionStage _stage = IdentifierCollisionStage.AfterOverrideNormalization;

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
    /// Registers a column identifier for override collision detection when the column is not already guarded
    /// by a per-table accumulator.
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
        _core.ThrowIfCollisions(
            _stage,
            "Identifier override collisions detected: ",
            IsSharedDescriptorElement
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

        _core.Register(scope, finalName, new IdentifierCollisionSource(resolvedOriginal, finalName, origin));
    }

    private static bool IsSharedDescriptorElement(
        IdentifierCollisionScope scope,
        string finalName,
        IdentifierCollisionOrigin _
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
            _ => false,
        };
    }
}
