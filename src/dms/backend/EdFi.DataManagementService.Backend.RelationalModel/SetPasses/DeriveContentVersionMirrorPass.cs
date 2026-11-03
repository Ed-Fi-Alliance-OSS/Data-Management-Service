// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Synthesizes the row-local <c>ContentVersion</c> mirror and the authoritative
/// <c>ContentLastModifiedAt</c> and <c>ContentCreatedAt</c> timestamps on the root table of every concrete
/// relational resource. The columns have no source JSONPath and no target resource; they are maintained only by document-stamping
/// triggers and are kept out of client-writable projections via <see cref="DbColumnModel.IsWritable"/>.
/// Descriptor resources (<see cref="ResourceStorageKind.SharedDescriptorTable"/>) are skipped; their
/// version mirror and authoritative timestamps live on the shared <c>dms.Descriptor</c> table.
/// </summary>
public sealed class DeriveContentVersionMirrorPass : IRelationalModelSetPass
{
    /// <summary>
    /// Appends the version mirror and authoritative timestamps to each relational resource root.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Mark the feature active so downstream passes (index inventory) derive change-version support
        // indexes, including the shared descriptor composite index for descriptor-only schemas.
        context.ContentVersionMirrorDerivationHasRun = true;

        for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
        {
            var resource = context.ConcreteResourcesInNameOrder[index];

            if (resource.StorageKind != ResourceStorageKind.RelationalTables)
            {
                continue;
            }

            var model = resource.RelationalModel;
            var root = model.Root;
            var updatedRoot = root with { Columns = [.. root.Columns, .. BuildBackendStampColumns()] };

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
    /// Builds the synthesized backend-owned stamp columns. They are stored, non-writable, non-nullable, and carry no
    /// source JSONPath or target resource.
    /// </summary>
    private static DbColumnModel[] BuildBackendStampColumns() =>
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
                ColumnKind.AuthoritativeContentLastModifiedAt,
                new RelationalScalarType(ScalarKind.DateTime),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            )
            {
                IsWritable = false,
            },
            new DbColumnModel(
                RelationalNameConventions.ContentCreatedAtColumnName,
                ColumnKind.AuthoritativeContentCreatedAt,
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
