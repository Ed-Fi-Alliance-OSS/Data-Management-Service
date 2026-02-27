// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Compiled read plan for a single resource.
/// </summary>
/// <param name="Model">The derived relational model for the resource.</param>
/// <param name="KeysetTable">
/// Keyset-table contract used by hydration queries for this dialect/runtime.
/// </param>
/// <param name="TablePlansInDependencyOrder">Per-table read plans in deterministic dependency order.</param>
/// <param name="ReferenceIdentityProjectionPlansInDependencyOrder">
/// Table-local reference-identity projection metadata in deterministic table dependency order.
/// </param>
/// <param name="DescriptorProjectionPlansInOrder">
/// Descriptor projection plans in deterministic execution order.
/// </param>
public sealed record ResourceReadPlan
{
    public ResourceReadPlan(
        RelationalResourceModel Model,
        KeysetTableContract KeysetTable,
        IEnumerable<TableReadPlan> TablePlansInDependencyOrder,
        IEnumerable<ReferenceIdentityProjectionTablePlan> ReferenceIdentityProjectionPlansInDependencyOrder,
        IEnumerable<DescriptorProjectionPlan> DescriptorProjectionPlansInOrder
    )
    {
        ArgumentNullException.ThrowIfNull(Model);
        ArgumentNullException.ThrowIfNull(KeysetTable);

        this.Model = Model;
        this.KeysetTable = KeysetTable;
        this.TablePlansInDependencyOrder = PlanContractCollectionCloner.ToImmutableArray(
            TablePlansInDependencyOrder,
            nameof(TablePlansInDependencyOrder)
        );
        this.ReferenceIdentityProjectionPlansInDependencyOrder =
            PlanContractCollectionCloner.ToImmutableArray(
                ReferenceIdentityProjectionPlansInDependencyOrder,
                nameof(ReferenceIdentityProjectionPlansInDependencyOrder)
            );
        this.DescriptorProjectionPlansInOrder = PlanContractCollectionCloner.ToImmutableArray(
            DescriptorProjectionPlansInOrder,
            nameof(DescriptorProjectionPlansInOrder)
        );
    }

    public RelationalResourceModel Model { get; init; }

    public KeysetTableContract KeysetTable { get; init; }

    public ImmutableArray<TableReadPlan> TablePlansInDependencyOrder { get; init; }

    public ImmutableArray<ReferenceIdentityProjectionTablePlan> ReferenceIdentityProjectionPlansInDependencyOrder { get; init; }

    public ImmutableArray<DescriptorProjectionPlan> DescriptorProjectionPlansInOrder { get; init; }
}

/// <summary>
/// Compiled read SQL for one table in a resource hydration flow.
/// </summary>
/// <param name="TableModel">The table shape model.</param>
/// <param name="SelectByKeysetSql">
/// Parameterized SQL that joins to a materialized keyset table and returns rows for the page.
/// </param>
public sealed record TableReadPlan(DbTableModel TableModel, string SelectByKeysetSql);

/// <summary>
/// Names the materialized keyset table and its <c>DocumentId</c> column used by hydration SQL.
/// </summary>
/// <param name="Table">Keyset temporary table relation.</param>
/// <param name="DocumentIdColumnName">Keyset table <c>DocumentId</c> column name.</param>
public sealed record KeysetTableContract(SqlRelationRef.TempTable Table, DbColumnName DocumentIdColumnName);
