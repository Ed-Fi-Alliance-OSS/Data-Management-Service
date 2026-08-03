// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Synthesizes the document metadata columns (<c>DocumentUuid</c>, <c>IdentityVersion</c>,
/// <c>IdentityLastModifiedAt</c>, <c>CreatedAt</c>, and <c>CreatedByOwnershipTokenId</c>) onto the root table
/// of every concrete resource stored as relational tables, along with the per-root
/// <c>UX_&lt;Table&gt;_DocumentUuid</c> unique constraint that keeps the public API id unique within the
/// resource. The columns have no source JSONPath and no target resource; they are maintained only by
/// document-stamping triggers and are kept out of client-writable projections via
/// <see cref="DbColumnModel.IsWritable"/>.
/// Descriptor resources (<see cref="ResourceStorageKind.SharedDescriptorTable"/>) are skipped; their metadata
/// lives on the shared <c>dms.Descriptor</c> table added by the core DDL pass.
/// </summary>
public sealed class DeriveDocumentMetadataColumnsPass : IRelationalModelSetPass
{
    /// <summary>
    /// Appends the metadata columns and the <c>DocumentUuid</c> unique constraint to each
    /// <see cref="ResourceStorageKind.RelationalTables"/> resource root.
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

            DbColumnName[] uniqueColumns = [RelationalNameConventions.DocumentUuidColumnName];
            var uniqueName = ConstraintNaming.BuildColumnUniqueName(root.Table, uniqueColumns);

            var updatedRoot = RelationalModelOrdering.CanonicalizeTable(
                root with
                {
                    Columns = [.. root.Columns, .. BuildMetadataColumns()],
                    Constraints =
                    [
                        .. root.Constraints,
                        new TableConstraint.Unique(uniqueName, uniqueColumns),
                    ],
                }
            );

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
    /// Builds the five synthesized metadata columns. They are stored and non-writable, and carry no source
    /// JSONPath or target resource. <c>DocumentUuid</c> and <c>CreatedByOwnershipTokenId</c> carry no scalar
    /// type because their storage types (<c>uuid</c>/<c>uniqueidentifier</c> and <c>smallint</c>) have no
    /// dialect-neutral <see cref="ScalarKind"/>; the DDL emitter renders them per column kind.
    /// </summary>
    private static DbColumnModel[] BuildMetadataColumns() =>
        [
            new DbColumnModel(
                RelationalNameConventions.DocumentUuidColumnName,
                ColumnKind.DocumentUuid,
                ScalarType: null,
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            )
            {
                IsWritable = false,
            },
            new DbColumnModel(
                RelationalNameConventions.IdentityVersionColumnName,
                ColumnKind.MirroredIdentityVersion,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            )
            {
                IsWritable = false,
            },
            new DbColumnModel(
                RelationalNameConventions.IdentityLastModifiedAtColumnName,
                ColumnKind.MirroredIdentityLastModifiedAt,
                new RelationalScalarType(ScalarKind.DateTime),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            )
            {
                IsWritable = false,
            },
            new DbColumnModel(
                RelationalNameConventions.CreatedAtColumnName,
                ColumnKind.CreatedAt,
                new RelationalScalarType(ScalarKind.DateTime),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            )
            {
                IsWritable = false,
            },
            new DbColumnModel(
                RelationalNameConventions.CreatedByOwnershipTokenIdColumnName,
                ColumnKind.CreatedByOwnershipTokenId,
                ScalarType: null,
                IsNullable: true,
                SourceJsonPath: null,
                TargetResource: null
            )
            {
                IsWritable = false,
            },
        ];
}
