// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

internal enum ConstraintIdentityKind
{
    PrimaryKey,
    Unique,
    ForeignKey,
    AllOrNone,
}

internal sealed class ConstraintIdentity : IEquatable<ConstraintIdentity>
{
    private readonly DbColumnName[] _columns;
    private readonly DbColumnName[] _targetColumns;
    private readonly DbColumnName[] _dependentColumns;

    private ConstraintIdentity(
        ConstraintIdentityKind kind,
        DbTableName table,
        DbColumnName[] columns,
        DbTableName? targetTable,
        DbColumnName[] targetColumns,
        DbColumnName[] dependentColumns,
        ReferentialAction onDelete,
        ReferentialAction onUpdate
    )
    {
        Kind = kind;
        Table = table;
        _columns = columns;
        TargetTable = targetTable;
        _targetColumns = targetColumns;
        _dependentColumns = dependentColumns;
        OnDelete = onDelete;
        OnUpdate = onUpdate;
    }

    public ConstraintIdentityKind Kind { get; }

    public DbTableName Table { get; }

    public DbTableName? TargetTable { get; }

    public ReferentialAction OnDelete { get; }

    public ReferentialAction OnUpdate { get; }

    public IReadOnlyList<DbColumnName> Columns => _columns;

    public IReadOnlyList<DbColumnName> TargetColumns => _targetColumns;

    public IReadOnlyList<DbColumnName> DependentColumns => _dependentColumns;

    public static ConstraintIdentity ForPrimaryKey(DbTableName table, IReadOnlyList<DbColumnName> columns)
    {
        return new ConstraintIdentity(
            ConstraintIdentityKind.PrimaryKey,
            table,
            CopyColumns(columns),
            targetTable: null,
            Array.Empty<DbColumnName>(),
            Array.Empty<DbColumnName>(),
            ReferentialAction.NoAction,
            ReferentialAction.NoAction
        );
    }

    public static ConstraintIdentity ForUnique(DbTableName table, IReadOnlyList<DbColumnName> columns)
    {
        return new ConstraintIdentity(
            ConstraintIdentityKind.Unique,
            table,
            CopyColumns(columns),
            targetTable: null,
            Array.Empty<DbColumnName>(),
            Array.Empty<DbColumnName>(),
            ReferentialAction.NoAction,
            ReferentialAction.NoAction
        );
    }

    public static ConstraintIdentity ForForeignKey(
        DbTableName table,
        IReadOnlyList<DbColumnName> columns,
        DbTableName targetTable,
        IReadOnlyList<DbColumnName> targetColumns,
        ReferentialAction onDelete,
        ReferentialAction onUpdate
    )
    {
        return new ConstraintIdentity(
            ConstraintIdentityKind.ForeignKey,
            table,
            CopyColumns(columns),
            targetTable,
            CopyColumns(targetColumns),
            Array.Empty<DbColumnName>(),
            onDelete,
            onUpdate
        );
    }

    public static ConstraintIdentity ForAllOrNone(
        DbTableName table,
        DbColumnName fkColumn,
        IReadOnlyList<DbColumnName> dependentColumns
    )
    {
        return new ConstraintIdentity(
            ConstraintIdentityKind.AllOrNone,
            table,
            [fkColumn],
            targetTable: null,
            Array.Empty<DbColumnName>(),
            CopyColumns(dependentColumns),
            ReferentialAction.NoAction,
            ReferentialAction.NoAction
        );
    }

    public bool Equals(ConstraintIdentity? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Kind != other.Kind || !Table.Equals(other.Table))
        {
            return false;
        }

        if (!Columns.SequenceEqual(other.Columns))
        {
            return false;
        }

        if (Kind == ConstraintIdentityKind.ForeignKey)
        {
            if (TargetTable is null || other.TargetTable is null)
            {
                return false;
            }

            if (!TargetTable.Value.Equals(other.TargetTable.Value))
            {
                return false;
            }

            if (!TargetColumns.SequenceEqual(other.TargetColumns))
            {
                return false;
            }

            if (OnDelete != other.OnDelete || OnUpdate != other.OnUpdate)
            {
                return false;
            }
        }

        if (Kind == ConstraintIdentityKind.AllOrNone)
        {
            if (!DependentColumns.SequenceEqual(other.DependentColumns))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj)
    {
        return obj is ConstraintIdentity other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Table);
        AddColumns(ref hash, Columns);

        if (Kind == ConstraintIdentityKind.ForeignKey)
        {
            hash.Add(TargetTable.GetValueOrDefault());
            AddColumns(ref hash, TargetColumns);
            hash.Add(OnDelete);
            hash.Add(OnUpdate);
        }

        if (Kind == ConstraintIdentityKind.AllOrNone)
        {
            AddColumns(ref hash, DependentColumns);
        }

        return hash.ToHashCode();
    }

    private static void AddColumns(ref HashCode hash, IReadOnlyList<DbColumnName> columns)
    {
        hash.Add(columns.Count);

        foreach (var column in columns)
        {
            hash.Add(column);
        }
    }

    private static DbColumnName[] CopyColumns(IReadOnlyList<DbColumnName> columns)
    {
        return columns.Count == 0 ? Array.Empty<DbColumnName>() : columns.ToArray();
    }
}
