// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for constraint names within dialect limits.
/// </summary>
[TestFixture]
public class Given_Constraint_Names_Within_Dialect_Limits
{
    private string _foreignKeyName = default!;
    private string _primaryKeyName = default!;
    private string _uniqueName = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var dialectRules = new PgsqlDialectRules();
        var table = new DbTableName(new DbSchemaName("edfi"), "School");
        var localColumns = new[] { new DbColumnName("Student_DocumentId") };
        var targetTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var targetColumns = new[] { RelationalNameConventions.DocumentIdColumnName };

        var fkBase = ConstraintNaming.BuildReferenceForeignKeyName(table, "Student", isComposite: false);
        var fkIdentity = ConstraintIdentity.ForForeignKey(
            table,
            localColumns,
            targetTable,
            targetColumns,
            ReferentialAction.NoAction,
            ReferentialAction.NoAction
        );
        _foreignKeyName = ConstraintNaming.ApplyDialectLimit(fkBase, fkIdentity, dialectRules);

        var primaryColumns = new[] { RelationalNameConventions.DocumentIdColumnName };
        var primaryBase = ConstraintNaming.BuildPrimaryKeyName(table);
        var primaryIdentity = ConstraintIdentity.ForPrimaryKey(table, primaryColumns);
        _primaryKeyName = ConstraintNaming.ApplyDialectLimit(primaryBase, primaryIdentity, dialectRules);

        var uniqueBase = ConstraintNaming.BuildNaturalKeyUniqueName(table);
        var uniqueIdentity = ConstraintIdentity.ForUnique(table, new[] { new DbColumnName("SchoolId") });
        _uniqueName = ConstraintNaming.ApplyDialectLimit(uniqueBase, uniqueIdentity, dialectRules);
    }

    /// <summary>
    /// It should not append hash for foreign keys within limits.
    /// </summary>
    [Test]
    public void It_should_not_append_hash_for_foreign_keys_within_limits()
    {
        _foreignKeyName.Should().Be("FK_School_Student");
    }

    /// <summary>
    /// It should not append hash for primary keys within limits.
    /// </summary>
    [Test]
    public void It_should_not_append_hash_for_primary_keys_within_limits()
    {
        _primaryKeyName.Should().Be("PK_School");
    }

    /// <summary>
    /// It should not append hash for unique constraints within limits.
    /// </summary>
    [Test]
    public void It_should_not_append_hash_for_unique_constraints_within_limits()
    {
        _uniqueName.Should().Be("UX_School_NK");
    }
}

/// <summary>
/// Test fixture for constraint names exceeding dialect limits.
/// </summary>
[TestFixture]
public class Given_Constraint_Names_Exceeding_Dialect_Limits
{
    private string _foreignKeyName = default!;
    private string _primaryKeyName = default!;
    private string _uniqueName = default!;
    private string _foreignKeyHash = default!;
    private string _primaryKeyHash = default!;
    private string _uniqueHash = default!;
    private PgsqlDialectRules _dialectRules = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _dialectRules = new PgsqlDialectRules();

        var table = new DbTableName(new DbSchemaName("edfi"), new string('A', 70));
        var referenceBaseName = new string('B', 18);
        var localColumns = new[]
        {
            new DbColumnName($"{referenceBaseName}_DocumentId"),
            new DbColumnName($"{referenceBaseName}_Key"),
        };
        var targetTable = new DbTableName(new DbSchemaName("edfi"), new string('C', 35));
        var targetColumns = new[] { RelationalNameConventions.DocumentIdColumnName, new DbColumnName("Key") };

        var fkBase = ConstraintNaming.BuildReferenceForeignKeyName(
            table,
            referenceBaseName,
            isComposite: true
        );
        var fkIdentity = ConstraintIdentity.ForForeignKey(
            table,
            localColumns,
            targetTable,
            targetColumns,
            ReferentialAction.NoAction,
            ReferentialAction.NoAction
        );
        _foreignKeyName = ConstraintNaming.ApplyDialectLimit(fkBase, fkIdentity, _dialectRules);

        var primaryColumns = new[]
        {
            RelationalNameConventions.DocumentIdColumnName,
            new DbColumnName("Ordinal"),
        };
        var primaryBase = ConstraintNaming.BuildPrimaryKeyName(table);
        var primaryIdentity = ConstraintIdentity.ForPrimaryKey(table, primaryColumns);
        _primaryKeyName = ConstraintNaming.ApplyDialectLimit(primaryBase, primaryIdentity, _dialectRules);

        var uniqueColumns = new[]
        {
            new DbColumnName(new string('D', 32)),
            new DbColumnName(new string('E', 32)),
        };
        var uniqueBase = ConstraintNaming.BuildArrayUniquenessName(table, uniqueColumns);
        var uniqueIdentity = ConstraintIdentity.ForUnique(table, uniqueColumns);
        _uniqueName = ConstraintNaming.ApplyDialectLimit(uniqueBase, uniqueIdentity, _dialectRules);

        _foreignKeyHash = ComputeHash(
            BuildForeignKeySignature(table, localColumns, targetTable, targetColumns)
        );
        _primaryKeyHash = ComputeHash(BuildPrimaryKeySignature(table, primaryColumns));
        _uniqueHash = ComputeHash(BuildUniqueSignature(table, uniqueColumns));
    }

    /// <summary>
    /// It should append hash for foreign keys exceeding limits.
    /// </summary>
    [Test]
    public void It_should_append_hash_for_foreign_keys_exceeding_limits()
    {
        _foreignKeyName.Should().EndWith($"_{_foreignKeyHash}");
        _foreignKeyName.Length.Should().BeLessOrEqualTo(_dialectRules.MaxIdentifierLength);
    }

    /// <summary>
    /// It should append hash for primary keys exceeding limits.
    /// </summary>
    [Test]
    public void It_should_append_hash_for_primary_keys_exceeding_limits()
    {
        _primaryKeyName.Should().EndWith($"_{_primaryKeyHash}");
        _primaryKeyName.Length.Should().BeLessOrEqualTo(_dialectRules.MaxIdentifierLength);
    }

    /// <summary>
    /// It should append hash for unique constraints exceeding limits.
    /// </summary>
    [Test]
    public void It_should_append_hash_for_unique_constraints_exceeding_limits()
    {
        _uniqueName.Should().EndWith($"_{_uniqueHash}");
        _uniqueName.Length.Should().BeLessOrEqualTo(_dialectRules.MaxIdentifierLength);
    }

    /// <summary>
    /// Builds the deterministic signature used to compute expected foreign key name hashes in tests.
    /// </summary>
    private static string BuildForeignKeySignature(
        DbTableName table,
        IReadOnlyList<DbColumnName> columns,
        DbTableName targetTable,
        IReadOnlyList<DbColumnName> targetColumns
    )
    {
        return $"ForeignKey|{table}|{string.Join(",", columns.Select(column => column.Value))}|{targetTable}|{string.Join(",", targetColumns.Select(column => column.Value))}|{ReferentialAction.NoAction}|{ReferentialAction.NoAction}";
    }

    /// <summary>
    /// Builds the deterministic signature used to compute expected unique constraint name hashes in tests.
    /// </summary>
    private static string BuildUniqueSignature(DbTableName table, IReadOnlyList<DbColumnName> columns)
    {
        return $"Unique|{table}|{string.Join(",", columns.Select(column => column.Value))}";
    }

    /// <summary>
    /// Builds the deterministic signature used to compute expected primary key name hashes in tests.
    /// </summary>
    private static string BuildPrimaryKeySignature(DbTableName table, IReadOnlyList<DbColumnName> columns)
    {
        return $"PrimaryKey|{table}|{string.Join(",", columns.Select(column => column.Value))}";
    }

    /// <summary>
    /// Computes a stable lowercase hash prefix for a signature, mirroring the production hashing approach.
    /// </summary>
    private static string ComputeHash(string signature)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(signature);
        var hash = sha256.ComputeHash(bytes);

        return Convert.ToHexString(hash).ToLowerInvariant()[..10];
    }
}

/// <summary>
/// Test fixture for deterministic array uniqueness naming regardless of input order.
/// </summary>
[TestFixture]
public class Given_Array_Uniqueness_Constraint_Columns_With_Swapped_Order
{
    /// <summary>
    /// It should generate the same unique name for equivalent column sets in different orders.
    /// </summary>
    [Test]
    public void It_should_generate_the_same_unique_name_for_equivalent_column_sets()
    {
        var table = new DbTableName(new DbSchemaName("edfi"), "Association");

        var columnsInOriginalOrder = new[]
        {
            new DbColumnName("Student_LastSurname"),
            new DbColumnName("Section_SchoolId"),
            new DbColumnName("Student_FirstName"),
        };

        var columnsInSwappedOrder = new[]
        {
            new DbColumnName("Student_FirstName"),
            new DbColumnName("Student_LastSurname"),
            new DbColumnName("Section_SchoolId"),
        };

        var uniqueNameInOriginalOrder = ConstraintNaming.BuildArrayUniquenessName(
            table,
            columnsInOriginalOrder
        );
        var uniqueNameInSwappedOrder = ConstraintNaming.BuildArrayUniquenessName(
            table,
            columnsInSwappedOrder
        );

        uniqueNameInOriginalOrder.Should().Be(uniqueNameInSwappedOrder);
        uniqueNameInOriginalOrder
            .Should()
            .Be("UX_Association_Section_SchoolId_Student_FirstName_LastSurname");
    }
}
