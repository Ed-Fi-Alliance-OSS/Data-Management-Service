// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Constraints;

/// <summary>
/// Identifies the semantic kind of constraint for name generation and collision detection.
/// </summary>
internal enum ConstraintIdentityKind
{
    /// <summary>
    /// Primary key constraint identity.
    /// </summary>
    PrimaryKey,

    /// <summary>
    /// Unique constraint identity.
    /// </summary>
    Unique,

    /// <summary>
    /// Foreign key constraint identity.
    /// </summary>
    ForeignKey,

    /// <summary>
    /// All-or-none nullability check constraint identity.
    /// </summary>
    AllOrNone,
}

/// <summary>
/// Represents a constraint's semantic identity independent of its rendered name.
/// </summary>
internal sealed class ConstraintIdentity : IEquatable<ConstraintIdentity>
{
    private readonly DbColumnName[] _columns;
    private readonly DbColumnName[] _targetColumns;
    private readonly DbColumnName[] _dependentColumns;

    /// <summary>
    /// Initializes a new <see cref="ConstraintIdentity"/> instance.
    /// </summary>
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

    /// <summary>
    /// Gets the constraint kind.
    /// </summary>
    public ConstraintIdentityKind Kind { get; }

    /// <summary>
    /// Gets the table that owns the constraint.
    /// </summary>
    public DbTableName Table { get; }

    /// <summary>
    /// Gets the referenced table for foreign key constraints.
    /// </summary>
    public DbTableName? TargetTable { get; }

    /// <summary>
    /// Gets the foreign key <c>ON DELETE</c> referential action.
    /// </summary>
    public ReferentialAction OnDelete { get; }

    /// <summary>
    /// Gets the foreign key <c>ON UPDATE</c> referential action.
    /// </summary>
    public ReferentialAction OnUpdate { get; }

    /// <summary>
    /// Gets the local columns that participate in the constraint.
    /// </summary>
    public IReadOnlyList<DbColumnName> Columns => _columns;

    /// <summary>
    /// Gets the target columns referenced by a foreign key constraint.
    /// </summary>
    public IReadOnlyList<DbColumnName> TargetColumns => _targetColumns;

    /// <summary>
    /// Gets the dependent columns used by an all-or-none constraint.
    /// </summary>
    public IReadOnlyList<DbColumnName> DependentColumns => _dependentColumns;

    /// <summary>
    /// Creates a primary key identity for the specified table and columns.
    /// </summary>
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

    /// <summary>
    /// Creates a unique constraint identity for the specified table and columns.
    /// </summary>
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

    /// <summary>
    /// Creates a foreign key identity for the specified relationship and referential actions.
    /// </summary>
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

    /// <summary>
    /// Creates an all-or-none nullability check identity for a dependent group keyed by an FK column.
    /// </summary>
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

    /// <summary>
    /// Compares this identity to another identity for semantic equality.
    /// </summary>
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

    /// <summary>
    /// Determines whether this identity equals another object.
    /// </summary>
    public override bool Equals(object? obj)
    {
        return obj is ConstraintIdentity other && Equals(other);
    }

    /// <summary>
    /// Computes a hash code based on the identity components.
    /// </summary>
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

    /// <summary>
    /// Adds a set of columns into a running <see cref="HashCode"/> computation.
    /// </summary>
    private static void AddColumns(ref HashCode hash, IReadOnlyList<DbColumnName> columns)
    {
        hash.Add(columns.Count);

        foreach (var column in columns)
        {
            hash.Add(column);
        }
    }

    /// <summary>
    /// Copies columns into an array to ensure identity immutability for callers using pooled collections.
    /// </summary>
    private static DbColumnName[] CopyColumns(IReadOnlyList<DbColumnName> columns)
    {
        return columns.Count == 0 ? Array.Empty<DbColumnName>() : columns.ToArray();
    }
}
