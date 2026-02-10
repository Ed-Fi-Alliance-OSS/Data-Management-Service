// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for semantic constraint identity detection after renaming.
/// </summary>
[TestFixture]
public class Given_Renamed_Constraint_Names
{
    private DbTableName _table = default!;
    private DbTableName _targetTable = default!;
    private DbColumnName[] _uniqueColumns = default!;
    private DbColumnName[] _localColumns = default!;
    private DbColumnName[] _targetColumns = default!;
    private DbColumnName[] _dependentColumns = default!;
    private IReadOnlyList<TableConstraint> _constraints = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _table = new DbTableName(new DbSchemaName("edfi"), "Enrollment");
        _targetTable = new DbTableName(new DbSchemaName("edfi"), "School");
        _uniqueColumns = [new DbColumnName("ColumnA"), new DbColumnName("ColumnB")];
        _localColumns = [new DbColumnName("FkA"), new DbColumnName("FkB")];
        _targetColumns = [new DbColumnName("PkA"), new DbColumnName("PkB")];
        _dependentColumns = [new DbColumnName("DepA"), new DbColumnName("DepB")];

        _constraints = new TableConstraint[]
        {
            new TableConstraint.Unique("UX_Renamed", _uniqueColumns),
            new TableConstraint.ForeignKey(
                "FK_Renamed",
                _localColumns,
                _targetTable,
                _targetColumns,
                OnDelete: ReferentialAction.Cascade,
                OnUpdate: ReferentialAction.NoAction
            ),
            new TableConstraint.AllOrNoneNullability("CK_Renamed", _localColumns[0], _dependentColumns),
        };
    }

    /// <summary>
    /// It should match unique constraints by semantic identity.
    /// </summary>
    [Test]
    public void It_should_match_unique_constraints_by_semantic_identity()
    {
        ConstraintDerivationHelpers
            .ContainsUniqueConstraint(_constraints, _table, _uniqueColumns)
            .Should()
            .BeTrue();
    }

    /// <summary>
    /// It should match foreign key constraints by semantic identity.
    /// </summary>
    [Test]
    public void It_should_match_foreign_key_constraints_by_semantic_identity()
    {
        ConstraintDerivationHelpers
            .ContainsForeignKeyConstraint(
                _constraints,
                _table,
                _localColumns,
                _targetTable,
                _targetColumns,
                ReferentialAction.Cascade,
                ReferentialAction.NoAction
            )
            .Should()
            .BeTrue();
    }

    /// <summary>
    /// It should match all-or-none constraints by semantic identity.
    /// </summary>
    [Test]
    public void It_should_match_all_or_none_constraints_by_semantic_identity()
    {
        ConstraintDerivationHelpers
            .ContainsAllOrNoneConstraint(_constraints, _table, _localColumns[0], _dependentColumns)
            .Should()
            .BeTrue();
    }

    /// <summary>
    /// It should treat foreign keys with different referential actions as distinct.
    /// </summary>
    [Test]
    public void It_should_treat_different_referential_actions_as_distinct()
    {
        ConstraintDerivationHelpers
            .ContainsForeignKeyConstraint(
                _constraints,
                _table,
                _localColumns,
                _targetTable,
                _targetColumns,
                ReferentialAction.NoAction,
                ReferentialAction.NoAction
            )
            .Should()
            .BeFalse();
    }
}

/// <summary>
/// Test fixture for foreign key constraint identity semantics.
/// </summary>
[TestFixture]
public class Given_Foreign_Key_Constraint_Identity
{
    /// <summary>
    /// It should produce equal hash codes for equal foreign key identities.
    /// </summary>
    [Test]
    public void It_should_produce_equal_hash_codes_for_equal_foreign_key_identities()
    {
        var table = new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation");
        var targetTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var localColumns = new[]
        {
            new DbColumnName("SchoolId"),
            new DbColumnName("EducationOrganizationId"),
        };
        var targetColumns = new[]
        {
            new DbColumnName("SchoolId"),
            new DbColumnName("EducationOrganizationId"),
        };

        var firstIdentity = ConstraintIdentity.ForForeignKey(
            table,
            localColumns,
            targetTable,
            targetColumns,
            ReferentialAction.NoAction,
            ReferentialAction.Cascade
        );
        var secondIdentity = ConstraintIdentity.ForForeignKey(
            table,
            localColumns,
            targetTable,
            targetColumns,
            ReferentialAction.NoAction,
            ReferentialAction.Cascade
        );

        firstIdentity.Should().Be(secondIdentity);
        firstIdentity.GetHashCode().Should().Be(secondIdentity.GetHashCode());
    }

    /// <summary>
    /// It should fail fast when a malformed foreign key identity omits target table.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_foreign_key_target_table_is_null()
    {
        var malformedIdentity = BuildForeignKeyIdentityWithNullTargetTable();

        Action act = () => _ = malformedIdentity.GetHashCode();

        act.Should().Throw<InvalidOperationException>().WithMessage("*ForeignKey*TargetTable*");
    }

    private static ConstraintIdentity BuildForeignKeyIdentityWithNullTargetTable()
    {
        var constructor = typeof(ConstraintIdentity).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[]
            {
                typeof(ConstraintIdentityKind),
                typeof(DbTableName),
                typeof(DbColumnName[]),
                typeof(DbTableName?),
                typeof(DbColumnName[]),
                typeof(DbColumnName[]),
                typeof(ReferentialAction),
                typeof(ReferentialAction),
            },
            modifiers: null
        );

        constructor.Should().NotBeNull();

        return (ConstraintIdentity)
            constructor!.Invoke(
                new object?[]
                {
                    ConstraintIdentityKind.ForeignKey,
                    new DbTableName(new DbSchemaName("edfi"), "Enrollment"),
                    new[] { new DbColumnName("SchoolId") },
                    null,
                    new[] { new DbColumnName("SchoolId") },
                    Array.Empty<DbColumnName>(),
                    ReferentialAction.NoAction,
                    ReferentialAction.NoAction,
                }
            );
    }
}
