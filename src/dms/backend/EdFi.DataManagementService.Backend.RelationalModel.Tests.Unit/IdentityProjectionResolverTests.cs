// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Pure-function tests for the canonical stored-column resolution shared by the trigger and
/// tracked-change derivation passes via <see cref="IdentityProjectionResolver"/>.
/// </summary>
[TestFixture]
public class Given_Canonical_Stored_Column_Resolution
{
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "School");
    private static readonly DbSchemaName _schema = new("edfi");

    /// <summary>
    /// Builds a single-table model with a stored canonical column and a unified-alias column over it.
    /// </summary>
    private static DbTableModel BuildTable()
    {
        var canonical = new DbColumnModel(
            new DbColumnName("SchoolId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

        var alias = new DbColumnModel(
            new DbColumnName("EducationOrganizationId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId"), PresenceColumn: null)
        );

        return new DbTableModel(
            new DbTableName(_schema, "School"),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey("PK_School", [new DbKeyColumn(new DbColumnName("Id"), ColumnKind.Scalar)]),
            [canonical, alias],
            []
        );
    }

    /// <summary>
    /// It should return a stored column unchanged.
    /// </summary>
    [Test]
    public void It_should_return_a_stored_column_unchanged()
    {
        var resolved = IdentityProjectionResolver.ResolveToStoredColumn(
            new DbColumnName("SchoolId"),
            BuildTable(),
            _resource
        );

        resolved.Value.Should().Be("SchoolId");
    }

    /// <summary>
    /// It should unwrap a unified-alias column to its canonical storage column.
    /// </summary>
    [Test]
    public void It_should_unwrap_a_unified_alias_to_its_canonical_column()
    {
        var resolved = IdentityProjectionResolver.ResolveToStoredColumn(
            new DbColumnName("EducationOrganizationId"),
            BuildTable(),
            _resource
        );

        resolved.Value.Should().Be("SchoolId");
    }

    /// <summary>
    /// It should throw when the requested column is not present on the table.
    /// </summary>
    [Test]
    public void It_should_throw_for_a_missing_column()
    {
        var act = () =>
            IdentityProjectionResolver.ResolveToStoredColumn(
                new DbColumnName("DoesNotExist"),
                BuildTable(),
                _resource
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*DoesNotExist*");
    }

    /// <summary>
    /// It should de-duplicate columns that resolve to the same canonical storage column.
    /// </summary>
    [Test]
    public void It_should_de_duplicate_columns_resolving_to_the_same_canonical_column()
    {
        var resolved = IdentityProjectionResolver.ResolveColumnsToStored(
            [
                new DbColumnName("EducationOrganizationId"),
                new DbColumnName("SchoolId"),
                new DbColumnName("EducationOrganizationId"),
            ],
            BuildTable(),
            _resource
        );

        resolved.Select(column => column.Value).Should().Equal("SchoolId");
    }

    /// <summary>
    /// It should preserve first-seen order while de-duplicating.
    /// </summary>
    [Test]
    public void It_should_preserve_first_seen_order_while_de_duplicating()
    {
        var idColumn = new DbColumnModel(
            new DbColumnName("Id"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

        var table = BuildTable() with { Columns = [.. BuildTable().Columns, idColumn] };

        var resolved = IdentityProjectionResolver.ResolveColumnsToStored(
            [
                new DbColumnName("Id"),
                new DbColumnName("EducationOrganizationId"),
                new DbColumnName("SchoolId"),
            ],
            table,
            _resource
        );

        resolved.Select(column => column.Value).Should().Equal("Id", "SchoolId");
    }
}
