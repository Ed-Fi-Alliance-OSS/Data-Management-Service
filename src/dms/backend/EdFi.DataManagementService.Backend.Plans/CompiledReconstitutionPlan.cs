// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

internal sealed class ScopeKey : IEquatable<ScopeKey>
{
    public ScopeKey(IEnumerable<object?> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        Parts = [.. parts.Select(CanonicalizePart)];
    }

    public ImmutableArray<object?> Parts { get; }

    public bool Equals(ScopeKey? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || Parts.Length != other.Parts.Length)
        {
            return false;
        }

        for (var index = 0; index < Parts.Length; index++)
        {
            if (!Equals(Parts[index], other.Parts[index]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is ScopeKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var part in Parts)
        {
            hash.Add(part);
        }

        return hash.ToHashCode();
    }

    private static object? CanonicalizePart(object? part)
    {
        return part switch
        {
            null => null,
            byte value => (long)value,
            sbyte value => (long)value,
            short value => (long)value,
            ushort value => (long)value,
            int value => (long)value,
            uint value => (long)value,
            long value => value,
            _ => part,
        };
    }
}

internal sealed record DescriptorReconstitutionBinding(
    JsonPathExpression DescriptorValuePath,
    QualifiedResourceName DescriptorResource,
    int DescriptorIdColumnOrdinal
);

internal sealed record TableReconstitutionPlan
{
    public TableReconstitutionPlan(
        DbTableModel TableModel,
        IEnumerable<int> RootScopeLocatorOrdinals,
        IEnumerable<int> ImmediateParentScopeLocatorOrdinals,
        IEnumerable<int> PhysicalRowIdentityOrdinals,
        int? OrdinalColumnOrdinal,
        IEnumerable<ReferenceIdentityProjectionBinding> ReferenceBindingsInOrder,
        IEnumerable<DescriptorReconstitutionBinding> DescriptorBindingsInOrder
    )
    {
        this.TableModel = TableModel;
        this.RootScopeLocatorOrdinals = [.. RootScopeLocatorOrdinals];
        this.ImmediateParentScopeLocatorOrdinals = [.. ImmediateParentScopeLocatorOrdinals];
        this.PhysicalRowIdentityOrdinals = [.. PhysicalRowIdentityOrdinals];
        this.OrdinalColumnOrdinal = OrdinalColumnOrdinal;
        this.ReferenceBindingsInOrder = [.. ReferenceBindingsInOrder];
        this.DescriptorBindingsInOrder = [.. DescriptorBindingsInOrder];
    }

    public DbTableModel TableModel { get; init; }

    public DbTableName Table => TableModel.Table;

    public ImmutableArray<int> RootScopeLocatorOrdinals { get; init; }

    public ImmutableArray<int> ImmediateParentScopeLocatorOrdinals { get; init; }

    public ImmutableArray<int> PhysicalRowIdentityOrdinals { get; init; }

    public int? OrdinalColumnOrdinal { get; init; }

    public ImmutableArray<ReferenceIdentityProjectionBinding> ReferenceBindingsInOrder { get; init; }

    public ImmutableArray<DescriptorReconstitutionBinding> DescriptorBindingsInOrder { get; init; }

    public DbTableName? ImmediateParentTable { get; init; }

    public ImmutableArray<DbTableName> ImmediateChildrenInDependencyOrder { get; init; } = [];

    public int ResolveSingleRootScopeLocatorOrdinalOrThrow()
    {
        if (RootScopeLocatorOrdinals.Length != 1)
        {
            throw new InvalidOperationException(
                $"Compiled reconstitution for table '{Table}' requires exactly one root-scope locator ordinal, "
                    + $"but found {RootScopeLocatorOrdinals.Length}."
            );
        }

        return RootScopeLocatorOrdinals[0];
    }

    public int ResolveSingleImmediateParentScopeLocatorOrdinalOrThrow()
    {
        if (ImmediateParentScopeLocatorOrdinals.Length != 1)
        {
            throw new InvalidOperationException(
                $"Compiled reconstitution for table '{Table}' requires exactly one immediate-parent locator ordinal, "
                    + $"but found {ImmediateParentScopeLocatorOrdinals.Length}."
            );
        }

        return ImmediateParentScopeLocatorOrdinals[0];
    }
}

internal sealed record CompiledReconstitutionPlan
{
    public CompiledReconstitutionPlan(
        ResourceReadPlan ReadPlan,
        IEnumerable<TableReconstitutionPlan> TablePlansInDependencyOrder,
        PropertyOrderNode PropertyOrder
    )
    {
        this.ReadPlan = ReadPlan;
        this.TablePlansInDependencyOrder = [.. TablePlansInDependencyOrder];
        this.PropertyOrder = PropertyOrder;

        Dictionary<DbTableName, TableReconstitutionPlan> tablePlansByTable = [];

        foreach (var tablePlan in this.TablePlansInDependencyOrder)
        {
            if (!tablePlansByTable.TryAdd(tablePlan.Table, tablePlan))
            {
                throw new InvalidOperationException(
                    $"Compiled reconstitution plan for resource '{ReadPlan.Model.Resource.ProjectName}.{ReadPlan.Model.Resource.ResourceName}' "
                        + $"contains duplicate table '{tablePlan.Table}'."
                );
            }
        }

        this.TablePlansByTable = tablePlansByTable.ToFrozenDictionary();
    }

    public ResourceReadPlan ReadPlan { get; init; }

    public ImmutableArray<TableReconstitutionPlan> TablePlansInDependencyOrder { get; init; }

    public FrozenDictionary<DbTableName, TableReconstitutionPlan> TablePlansByTable { get; init; }

    public PropertyOrderNode PropertyOrder { get; init; }

    public TableReconstitutionPlan GetTablePlanOrThrow(DbTableName table)
    {
        if (TablePlansByTable.TryGetValue(table, out var tablePlan))
        {
            return tablePlan;
        }

        throw new KeyNotFoundException(
            $"Compiled reconstitution plan for resource '{ReadPlan.Model.Resource.ProjectName}.{ReadPlan.Model.Resource.ResourceName}' "
                + $"does not contain table '{table}'."
        );
    }
}

internal static class CompiledReconstitutionPlanCache
{
    private static readonly ConditionalWeakTable<
        ResourceReadPlan,
        CompiledReconstitutionPlan
    > PlansByReadPlan = new();

    public static CompiledReconstitutionPlan GetOrBuild(ResourceReadPlan readPlan)
    {
        ArgumentNullException.ThrowIfNull(readPlan);

        return PlansByReadPlan.GetValue(readPlan, CompiledReconstitutionPlanBuilder.Build);
    }
}

internal static class CompiledReconstitutionPlanBuilder
{
    public static CompiledReconstitutionPlan Build(ResourceReadPlan readPlan)
    {
        ArgumentNullException.ThrowIfNull(readPlan);

        var columnOrdinalsByTable = BuildColumnOrdinalsByTable(readPlan);
        var referenceBindingsByTable = BuildReferenceBindingsByTable(readPlan);
        var descriptorBindingsByTable = BuildDescriptorBindingsByTable(readPlan);

        var tablePlans = readPlan
            .TablePlansInDependencyOrder.Select(tablePlan =>
            {
                var tableModel = tablePlan.TableModel;
                var columnOrdinals = ResolveColumnOrdinalsOrThrow(
                    tableModel.Table,
                    columnOrdinalsByTable,
                    "compiled reconstitution plan"
                );

                return new TableReconstitutionPlan(
                    TableModel: tableModel,
                    RootScopeLocatorOrdinals: ResolveColumnOrdinalsOrThrow(
                        tableModel,
                        columnOrdinals,
                        ResolveRootScopeLocatorColumns(tableModel),
                        "root-scope locator"
                    ),
                    ImmediateParentScopeLocatorOrdinals: ResolveColumnOrdinalsOrThrow(
                        tableModel,
                        columnOrdinals,
                        ResolveImmediateParentScopeLocatorColumns(tableModel),
                        "immediate-parent locator"
                    ),
                    PhysicalRowIdentityOrdinals: ResolveColumnOrdinalsOrThrow(
                        tableModel,
                        columnOrdinals,
                        ResolvePhysicalRowIdentityColumns(tableModel),
                        "physical-row identity"
                    ),
                    OrdinalColumnOrdinal: ResolveOrdinalColumnOrdinal(tableModel),
                    ReferenceBindingsInOrder: referenceBindingsByTable.TryGetValue(
                        tableModel.Table,
                        out var referenceBindings
                    )
                        ? referenceBindings
                        : [],
                    DescriptorBindingsInOrder: descriptorBindingsByTable.TryGetValue(
                        tableModel.Table,
                        out var descriptorBindings
                    )
                        ? descriptorBindings
                        : []
                );
            })
            .ToArray();

        tablePlans = ApplyTopologyOrThrow(readPlan, tablePlans);

        return new CompiledReconstitutionPlan(
            ReadPlan: readPlan,
            TablePlansInDependencyOrder: tablePlans,
            PropertyOrder: BuildPropertyOrderTree(tablePlans)
        );
    }

    internal static PropertyOrderNode BuildPropertyOrderTree(
        IReadOnlyList<TableReconstitutionPlan> tablePlansInDependencyOrder
    )
    {
        List<JsonPathExpression> orderedPaths = [];

        foreach (var tablePlan in tablePlansInDependencyOrder)
        {
            var tableModel = tablePlan.TableModel;

            if (!string.Equals(tableModel.JsonScope.Canonical, "$", StringComparison.Ordinal))
            {
                orderedPaths.Add(tableModel.JsonScope);
            }

            foreach (var column in tableModel.Columns)
            {
                if (column.SourceJsonPath is JsonPathExpression sourcePath)
                {
                    orderedPaths.Add(sourcePath);
                }
            }

            foreach (var binding in tablePlan.ReferenceBindingsInOrder)
            {
                orderedPaths.Add(binding.ReferenceObjectPath);

                foreach (var field in binding.IdentityFieldOrdinalsInOrder)
                {
                    orderedPaths.Add(field.ReferenceJsonPath);
                }
            }

            foreach (var binding in tablePlan.DescriptorBindingsInOrder)
            {
                orderedPaths.Add(binding.DescriptorValuePath);
            }
        }

        orderedPaths.Sort(
            static (left, right) => string.Compare(left.Canonical, right.Canonical, StringComparison.Ordinal)
        );

        var root = new PropertyOrderNode();
        HashSet<string> seenPaths = new(StringComparer.Ordinal);

        foreach (var path in orderedPaths)
        {
            if (!seenPaths.Add(path.Canonical))
            {
                continue;
            }

            AddPath(root, path);
        }

        return root;
    }

    private static TableReconstitutionPlan[] ApplyTopologyOrThrow(
        ResourceReadPlan readPlan,
        TableReconstitutionPlan[] tablePlansInDependencyOrder
    )
    {
        Dictionary<DbTableName, int> originalOrderByTable = tablePlansInDependencyOrder
            .Select((tablePlan, index) => new KeyValuePair<DbTableName, int>(tablePlan.Table, index))
            .ToDictionary();
        Dictionary<DbTableName, DbTableName?> immediateParentByTable = [];
        Dictionary<DbTableName, List<DbTableName>> immediateChildrenByTable =
            tablePlansInDependencyOrder.ToDictionary(
                static tablePlan => tablePlan.Table,
                static _ => new List<DbTableName>()
            );

        foreach (var childTablePlan in tablePlansInDependencyOrder)
        {
            var immediateParentTable = ResolveImmediateParentTableOrThrow(
                readPlan,
                tablePlansInDependencyOrder,
                childTablePlan
            );

            immediateParentByTable[childTablePlan.Table] = immediateParentTable?.Table;

            if (immediateParentTable is not null)
            {
                immediateChildrenByTable[immediateParentTable.Table].Add(childTablePlan.Table);
            }
        }

        var rootTablePlans = tablePlansInDependencyOrder.Where(tablePlan =>
            immediateParentByTable[tablePlan.Table] is null
        );

        if (rootTablePlans.Count() != 1)
        {
            throw new InvalidOperationException(
                $"Cannot build compiled reconstitution plan for '{GetResourceDisplayName(readPlan)}': "
                    + $"expected exactly one root table in page topology, but found {rootTablePlans.Count()}."
            );
        }

        List<TableReconstitutionPlan> reorderedTablePlans = [];

        void AppendSubtree(TableReconstitutionPlan tablePlan)
        {
            reorderedTablePlans.Add(tablePlan);

            foreach (
                var childTable in immediateChildrenByTable[tablePlan.Table]
                    .OrderBy(childTable => originalOrderByTable[childTable])
            )
            {
                AppendSubtree(
                    tablePlansInDependencyOrder.Single(candidate => candidate.Table.Equals(childTable))
                );
            }
        }

        AppendSubtree(rootTablePlans.Single());

        if (reorderedTablePlans.Count != tablePlansInDependencyOrder.Length)
        {
            throw new InvalidOperationException(
                $"Cannot build compiled reconstitution plan for '{GetResourceDisplayName(readPlan)}': "
                    + "page topology ordering did not include every table exactly once."
            );
        }

        return
        [
            .. reorderedTablePlans.Select(tablePlan =>
                tablePlan with
                {
                    ImmediateParentTable = immediateParentByTable[tablePlan.Table],
                    ImmediateChildrenInDependencyOrder =
                    [
                        .. immediateChildrenByTable[tablePlan.Table]
                            .OrderBy(childTable => originalOrderByTable[childTable]),
                    ],
                }
            ),
        ];
    }

    private static TableReconstitutionPlan? ResolveImmediateParentTableOrThrow(
        ResourceReadPlan readPlan,
        IReadOnlyList<TableReconstitutionPlan> tablePlansInDependencyOrder,
        TableReconstitutionPlan childTablePlan
    )
    {
        var childTableModel = childTablePlan.TableModel;
        var tableKind = childTableModel.IdentityMetadata.TableKind;

        if (tableKind is DbTableKind.Root)
        {
            return null;
        }

        return tableKind switch
        {
            DbTableKind.Collection => ResolveExactScopeParentOrThrow(
                readPlan,
                tablePlansInDependencyOrder,
                childTablePlan,
                JsonScopeAttachmentResolver.ResolveExpectedImmediateParentScopeSegmentsOrThrow(
                    childTableModel.JsonScope,
                    tableKind
                ),
                static kind => kind is DbTableKind.Root or DbTableKind.Collection,
                "root or collection table"
            ),
            DbTableKind.RootExtension => ResolveExactScopeParentOrThrow(
                readPlan,
                tablePlansInDependencyOrder,
                childTablePlan,
                JsonScopeAttachmentResolver.ResolveExpectedImmediateParentScopeSegmentsOrThrow(
                    childTableModel.JsonScope,
                    tableKind
                ),
                static kind => kind is DbTableKind.Root,
                "root table"
            ),
            DbTableKind.CollectionExtensionScope => ResolveExactScopeParentOrThrow(
                readPlan,
                tablePlansInDependencyOrder,
                childTablePlan,
                JsonScopeAttachmentResolver.ResolveExpectedImmediateParentScopeSegmentsOrThrow(
                    childTableModel.JsonScope,
                    tableKind
                ),
                static kind => kind is DbTableKind.Collection,
                "collection table aligned to the extended base scope"
            ),
            DbTableKind.ExtensionCollection => ResolveExactScopeParentOrThrow(
                readPlan,
                tablePlansInDependencyOrder,
                childTablePlan,
                JsonScopeAttachmentResolver.ResolveExpectedImmediateParentScopeSegmentsOrThrow(
                    childTableModel.JsonScope,
                    tableKind
                ),
                static kind =>
                    kind
                        is DbTableKind.RootExtension
                            or DbTableKind.CollectionExtensionScope
                            or DbTableKind.ExtensionCollection,
                "root extension, collection extension scope, or extension collection table"
            ),
            _ => throw new InvalidOperationException(
                $"Cannot build compiled reconstitution plan for '{GetResourceDisplayName(readPlan)}': "
                    + $"table '{childTableModel.Table}' uses unsupported table kind '{tableKind}' for page topology."
            ),
        };
    }

    private static TableReconstitutionPlan ResolveExactScopeParentOrThrow(
        ResourceReadPlan readPlan,
        IReadOnlyList<TableReconstitutionPlan> tablePlansInDependencyOrder,
        TableReconstitutionPlan childTablePlan,
        IReadOnlyList<JsonPathSegment> expectedParentScopeSegments,
        Func<DbTableKind, bool> isAllowedParentKind,
        string expectedParentDescription
    )
    {
        List<TableReconstitutionPlan> candidateParents = [];

        foreach (var candidateParent in tablePlansInDependencyOrder)
        {
            if (!isAllowedParentKind(candidateParent.TableModel.IdentityMetadata.TableKind))
            {
                continue;
            }

            if (candidateParent.Table.Equals(childTablePlan.Table))
            {
                continue;
            }

            if (
                JsonScopeAttachmentResolver.AreSegmentsEqual(
                    JsonScopeAttachmentResolver.GetRestrictedSegments(candidateParent.TableModel.JsonScope),
                    expectedParentScopeSegments
                )
            )
            {
                candidateParents.Add(candidateParent);
            }
        }

        if (candidateParents.Count == 1)
        {
            return candidateParents[0];
        }

        var resourceDisplayName = GetResourceDisplayName(readPlan);
        var expectedScope = JsonScopeAttachmentResolver.FormatScope(expectedParentScopeSegments);

        if (candidateParents.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot build compiled reconstitution plan for '{resourceDisplayName}': "
                    + $"table '{childTablePlan.Table}' at scope '{childTablePlan.TableModel.JsonScope.Canonical}' "
                    + $"expected exactly one {expectedParentDescription} at scope '{expectedScope}', but found none."
            );
        }

        throw new InvalidOperationException(
            $"Cannot build compiled reconstitution plan for '{resourceDisplayName}': "
                + $"table '{childTablePlan.Table}' at scope '{childTablePlan.TableModel.JsonScope.Canonical}' "
                + $"expected exactly one {expectedParentDescription} at scope '{expectedScope}', "
                + $"but found {candidateParents.Count}: {string.Join(", ", candidateParents.Select(static candidate => $"'{candidate.Table}'"))}."
        );
    }

    private static string GetResourceDisplayName(ResourceReadPlan readPlan) =>
        $"{readPlan.Model.Resource.ProjectName}.{readPlan.Model.Resource.ResourceName}";

    private static FrozenDictionary<
        DbTableName,
        IReadOnlyDictionary<DbColumnName, int>
    > BuildColumnOrdinalsByTable(ResourceReadPlan readPlan)
    {
        Dictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>> columnOrdinalsByTable = [];

        foreach (var tablePlan in readPlan.TablePlansInDependencyOrder)
        {
            var tableModel = tablePlan.TableModel;

            if (
                !columnOrdinalsByTable.TryAdd(
                    tableModel.Table,
                    ProjectionMetadataResolver.BuildHydrationColumnOrdinalMapOrThrow(
                        tableModel,
                        duplicateColumn => new InvalidOperationException(
                            $"Cannot build compiled reconstitution plan for '{tableModel.Table}': "
                                + $"duplicate hydration column '{duplicateColumn.Value}' was encountered."
                        )
                    )
                )
            )
            {
                throw new InvalidOperationException(
                    $"Cannot build compiled reconstitution plan for resource '{readPlan.Model.Resource.ProjectName}.{readPlan.Model.Resource.ResourceName}': "
                        + $"duplicate table '{tableModel.Table}' was encountered."
                );
            }
        }

        return columnOrdinalsByTable.ToFrozenDictionary();
    }

    private static FrozenDictionary<
        DbTableName,
        ImmutableArray<ReferenceIdentityProjectionBinding>
    > BuildReferenceBindingsByTable(ResourceReadPlan readPlan)
    {
        Dictionary<DbTableName, List<ReferenceIdentityProjectionBinding>> referenceBindingsByTable = [];

        foreach (var tablePlan in readPlan.ReferenceIdentityProjectionPlansInDependencyOrder)
        {
            if (!referenceBindingsByTable.TryGetValue(tablePlan.Table, out var bindings))
            {
                bindings = [];
                referenceBindingsByTable[tablePlan.Table] = bindings;
            }

            bindings.AddRange(tablePlan.BindingsInOrder);
        }

        return referenceBindingsByTable.ToFrozenDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToImmutableArray()
        );
    }

    private static FrozenDictionary<
        DbTableName,
        ImmutableArray<DescriptorReconstitutionBinding>
    > BuildDescriptorBindingsByTable(ResourceReadPlan readPlan)
    {
        Dictionary<DbTableName, List<DescriptorReconstitutionBinding>> descriptorBindingsByTable = [];

        foreach (var descriptorPlan in readPlan.DescriptorProjectionPlansInOrder)
        {
            foreach (var source in descriptorPlan.SourcesInOrder)
            {
                if (!descriptorBindingsByTable.TryGetValue(source.Table, out var bindings))
                {
                    bindings = [];
                    descriptorBindingsByTable[source.Table] = bindings;
                }

                bindings.Add(
                    new DescriptorReconstitutionBinding(
                        DescriptorValuePath: source.DescriptorValuePath,
                        DescriptorResource: source.DescriptorResource,
                        DescriptorIdColumnOrdinal: source.DescriptorIdColumnOrdinal
                    )
                );
            }
        }

        return descriptorBindingsByTable.ToFrozenDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToImmutableArray()
        );
    }

    private static IReadOnlyDictionary<DbColumnName, int> ResolveColumnOrdinalsOrThrow(
        DbTableName table,
        FrozenDictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>> columnOrdinalsByTable,
        string contextDescription
    )
    {
        if (columnOrdinalsByTable.TryGetValue(table, out var columnOrdinals))
        {
            return columnOrdinals;
        }

        throw new InvalidOperationException(
            $"Cannot build {contextDescription} for '{table}': no hydration column ordinals were found."
        );
    }

    private static ImmutableArray<int> ResolveColumnOrdinalsOrThrow(
        DbTableModel tableModel,
        IReadOnlyDictionary<DbColumnName, int> columnOrdinals,
        IReadOnlyList<DbColumnName> columns,
        string columnGroup
    )
    {
        if (columns.Count == 0)
        {
            return [];
        }

        return
        [
            .. columns.Select(column =>
                ProjectionMetadataResolver.ResolveHydrationColumnOrdinalOrThrow(
                    columnOrdinals,
                    column,
                    missingColumn => new InvalidOperationException(
                        $"Cannot build compiled reconstitution plan for '{tableModel.Table}': "
                            + $"{columnGroup} column '{missingColumn.Value}' does not exist in hydration select-list columns."
                    )
                )
            ),
        ];
    }

    private static IReadOnlyList<DbColumnName> ResolveRootScopeLocatorColumns(DbTableModel tableModel)
    {
        if (tableModel.IdentityMetadata.RootScopeLocatorColumns.Count > 0)
        {
            return tableModel.IdentityMetadata.RootScopeLocatorColumns;
        }

        return
        [
            RelationalResourceModelCompileValidator.ResolveRootScopeLocatorColumnOrThrow(
                tableModel,
                "compiled reconstitution plan"
            ),
        ];
    }

    private static IReadOnlyList<DbColumnName> ResolveImmediateParentScopeLocatorColumns(
        DbTableModel tableModel
    )
    {
        if (tableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns.Count > 0)
        {
            return tableModel.IdentityMetadata.ImmediateParentScopeLocatorColumns;
        }

        return RelationalResourceModelCompileValidator.ResolveImmediateParentScopeLocatorColumnsOrThrow(
            tableModel,
            "compiled reconstitution plan"
        );
    }

    private static IReadOnlyList<DbColumnName> ResolvePhysicalRowIdentityColumns(DbTableModel tableModel)
    {
        if (tableModel.IdentityMetadata.PhysicalRowIdentityColumns.Count > 0)
        {
            return tableModel.IdentityMetadata.PhysicalRowIdentityColumns;
        }

        return [.. tableModel.Key.Columns.Select(static keyColumn => keyColumn.ColumnName)];
    }

    private static int? ResolveOrdinalColumnOrdinal(DbTableModel tableModel)
    {
        int? ordinalColumnOrdinal = null;

        for (var index = 0; index < tableModel.Columns.Count; index++)
        {
            if (tableModel.Columns[index].Kind is not ColumnKind.Ordinal)
            {
                continue;
            }

            if (ordinalColumnOrdinal is not null)
            {
                throw new InvalidOperationException(
                    $"Cannot build compiled reconstitution plan for '{tableModel.Table}': "
                        + "multiple ordinal columns were found."
                );
            }

            ordinalColumnOrdinal = index;
        }

        return ordinalColumnOrdinal;
    }

    private static void AddPath(PropertyOrderNode root, JsonPathExpression path)
    {
        var current = root;

        foreach (var segment in path.Segments)
        {
            if (segment is JsonPathSegment.Property property)
            {
                current = current.GetOrAddChild(property.Name);
            }
        }
    }
}

internal sealed class PropertyOrderNode
{
    public static readonly PropertyOrderNode Empty = new(isReadOnly: true);

    private readonly Dictionary<string, PropertyOrderNode> _childrenByName = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, PropertyOrderNode>> _childrenInOrder = [];
    private readonly bool _isReadOnly;

    public PropertyOrderNode(bool isReadOnly = false)
    {
        _isReadOnly = isReadOnly;
    }

    public IReadOnlyList<KeyValuePair<string, PropertyOrderNode>> ChildrenInOrder => _childrenInOrder;

    public PropertyOrderNode GetOrAddChild(string propertyName)
    {
        if (_isReadOnly)
        {
            throw new InvalidOperationException("Cannot mutate the read-only property-order sentinel node.");
        }

        if (_childrenByName.TryGetValue(propertyName, out var existingChild))
        {
            return existingChild;
        }

        var child = new PropertyOrderNode();
        _childrenByName[propertyName] = child;
        _childrenInOrder.Add(new KeyValuePair<string, PropertyOrderNode>(propertyName, child));

        return child;
    }
}
