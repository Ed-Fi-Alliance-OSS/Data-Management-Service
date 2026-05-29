// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Synthesizes the row-local change-version mirror columns (<c>ContentVersion</c> and
/// <c>ContentLastModifiedAt</c>) onto the root table of every concrete resource stored as relational tables.
/// The columns have no source JSONPath and no target resource; they are maintained only by document-stamping
/// triggers and are kept out of client-writable projections via <see cref="DbColumnModel.IsWritable"/>.
/// Descriptor resources (<see cref="ResourceStorageKind.SharedDescriptorTable"/>) are skipped; their mirror
/// columns live on the shared <c>dms.Descriptor</c> table added by the core DDL pass.
/// </summary>
public sealed class DeriveContentVersionMirrorPass : IRelationalModelSetPass
{
    /// <summary>
    /// Appends the mirror columns to each <see cref="ResourceStorageKind.RelationalTables"/> resource root.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
        {
            var resource = context.ConcreteResourcesInNameOrder[index];

            if (resource.StorageKind != ResourceStorageKind.RelationalTables)
            {
                continue;
            }

            var model = resource.RelationalModel;
            var root = model.Root;
            var updatedRoot = root with { Columns = [.. root.Columns, .. BuildMirrorColumns()] };

            var updatedTables = model
                .TablesInDependencyOrder.Select(table => table.Table.Equals(root.Table) ? updatedRoot : table)
                .ToArray();

            context.ConcreteResourcesInNameOrder[index] = resource with
            {
                RelationalModel = model with { Root = updatedRoot, TablesInDependencyOrder = updatedTables },
            };
        }
    }

    /// <summary>
    /// Builds the two synthesized mirror columns. They are stored, non-writable, non-nullable, and carry no
    /// source JSONPath or target resource.
    /// </summary>
    private static DbColumnModel[] BuildMirrorColumns() =>
        [
            new DbColumnModel(
                RelationalNameConventions.ContentVersionColumnName,
                ColumnKind.MirroredContentVersion,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            )
            {
                IsWritable = false,
            },
            new DbColumnModel(
                RelationalNameConventions.ContentLastModifiedAtColumnName,
                ColumnKind.MirroredContentLastModifiedAt,
                new RelationalScalarType(ScalarKind.DateTime),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            )
            {
                IsWritable = false,
            },
        ];
}
