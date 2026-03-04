// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Compiled read plan for a single resource.
/// </summary>
/// <remarks>
/// This contract carries hydration SQL plus deterministic ordinal metadata so executors never infer result shapes or
/// bindings by parsing SQL text.
/// </remarks>
public sealed record ResourceReadPlan
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceReadPlan" /> record.
    /// </summary>
    /// <param name="Model">The derived relational model for the resource.</param>
    /// <param name="KeysetTable">Keyset-table contract used by hydration queries for this dialect/runtime.</param>
    /// <param name="TablePlansInDependencyOrder">Per-table read plans in deterministic dependency order.</param>
    /// <param name="ReferenceIdentityProjectionPlansInDependencyOrder">
    /// Table-local reference-identity projection metadata in deterministic table dependency order.
    /// </param>
    /// <param name="DescriptorProjectionPlansInOrder">
    /// Descriptor projection plans in deterministic execution order.
    /// </param>
    public ResourceReadPlan(
        RelationalResourceModel Model,
        KeysetTableContract KeysetTable,
        IEnumerable<TableReadPlan> TablePlansInDependencyOrder,
        IEnumerable<ReferenceIdentityProjectionTablePlan> ReferenceIdentityProjectionPlansInDependencyOrder,
        IEnumerable<DescriptorProjectionPlan> DescriptorProjectionPlansInOrder
    )
    {
        this.Model = PlanContractArgumentValidator.RequireNotNull(Model, nameof(Model));
        this.KeysetTable = PlanContractArgumentValidator.RequireNotNull(KeysetTable, nameof(KeysetTable));
        this.TablePlansInDependencyOrder = PlanContractArgumentValidator.RequireImmutableArray(
            TablePlansInDependencyOrder,
            nameof(TablePlansInDependencyOrder)
        );
        this.ReferenceIdentityProjectionPlansInDependencyOrder =
            PlanContractArgumentValidator.RequireImmutableArray(
                ReferenceIdentityProjectionPlansInDependencyOrder,
                nameof(ReferenceIdentityProjectionPlansInDependencyOrder)
            );
        this.DescriptorProjectionPlansInOrder = PlanContractArgumentValidator.RequireImmutableArray(
            DescriptorProjectionPlansInOrder,
            nameof(DescriptorProjectionPlansInOrder)
        );
    }

    /// <summary>
    /// The derived relational model the plan was compiled from.
    /// </summary>
    public RelationalResourceModel Model { get; init; }

    /// <summary>
    /// Dialect-specific contract for the materialized keyset relation consumed by hydration SQL.
    /// </summary>
    public KeysetTableContract KeysetTable { get; init; }

    /// <summary>
    /// Per-table hydration plans in deterministic dependency order.
    /// </summary>
    public ImmutableArray<TableReadPlan> TablePlansInDependencyOrder { get; init; }

    /// <summary>
    /// Reference-object identity projection metadata in deterministic table dependency order.
    /// </summary>
    public ImmutableArray<ReferenceIdentityProjectionTablePlan> ReferenceIdentityProjectionPlansInDependencyOrder { get; init; }

    /// <summary>
    /// Descriptor URI projection plans in deterministic execution order.
    /// </summary>
    public ImmutableArray<DescriptorProjectionPlan> DescriptorProjectionPlansInOrder { get; init; }
}

/// <summary>
/// Compiled read SQL for one table in a resource hydration flow.
/// </summary>
public sealed record TableReadPlan
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TableReadPlan" /> record.
    /// </summary>
    /// <param name="TableModel">The table shape model.</param>
    /// <param name="SelectByKeysetSql">
    /// Parameterized SQL that joins to a materialized keyset table and returns rows for the page.
    /// </param>
    public TableReadPlan(DbTableModel TableModel, string SelectByKeysetSql)
    {
        ArgumentNullException.ThrowIfNull(TableModel);
        ArgumentNullException.ThrowIfNull(SelectByKeysetSql);

        this.TableModel = TableModel;
        this.SelectByKeysetSql = SelectByKeysetSql;
    }

    /// <summary>
    /// The table model the SQL targets.
    /// </summary>
    public DbTableModel TableModel { get; init; }

    /// <summary>
    /// Parameterized SQL that joins to a materialized keyset table and returns rows for the page.
    /// </summary>
    public string SelectByKeysetSql { get; init; }
}

/// <summary>
/// Names the materialized keyset table and its <c>DocumentId</c> column used by hydration SQL.
/// </summary>
/// <param name="Table">Keyset temporary table relation.</param>
/// <param name="DocumentIdColumnName">Keyset table <c>DocumentId</c> column name.</param>
public sealed record KeysetTableContract(SqlRelationRef.TempTable Table, DbColumnName DocumentIdColumnName);
