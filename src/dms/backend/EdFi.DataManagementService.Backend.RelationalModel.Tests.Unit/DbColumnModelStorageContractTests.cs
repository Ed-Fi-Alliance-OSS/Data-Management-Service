// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for DbColumnModel storage constructor contract behavior.
/// </summary>
[TestFixture]
public class Given_DbColumnModel_Storage_Constructor_Contract
{
    private DbColumnModel _aliasColumn = default!;
    private DbColumnModel _explicitStoredColumn = default!;
    private DbColumnModel _implicitStoredColumn = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _aliasColumn = new DbColumnModel(
            new DbColumnName("SchoolId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int32),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.UnifiedAlias(
                new DbColumnName("SchoolId_Canonical"),
                new DbColumnName("SchoolId_Present")
            )
        );

        _implicitStoredColumn = new DbColumnModel(
            new DbColumnName("SchoolId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int32),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: null
        );

        _explicitStoredColumn = new DbColumnModel(
            new DbColumnName("SchoolId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int32),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.Stored()
        );
    }

    /// <summary>
    /// It should default the legacy constructor to stored storage.
    /// </summary>
    [Test]
    public void It_should_default_the_legacy_constructor_to_stored_storage()
    {
        _implicitStoredColumn.Should().Be(_explicitStoredColumn);
        _implicitStoredColumn.Storage.Should().BeOfType<ColumnStorage.Stored>();
    }

    /// <summary>
    /// It should include storage in record equality and with-expression comparisons.
    /// </summary>
    [Test]
    public void It_should_include_storage_in_record_equality_and_with_expression_comparisons()
    {
        var aliasClone = _aliasColumn with { };
        var storedVariant = _aliasColumn with { Storage = new ColumnStorage.Stored() };

        aliasClone.Should().Be(_aliasColumn);
        storedVariant.Should().NotBe(_aliasColumn);
        storedVariant.Storage.Should().BeOfType<ColumnStorage.Stored>();
    }

    /// <summary>
    /// It should include storage in record deconstruction.
    /// </summary>
    [Test]
    public void It_should_include_storage_in_record_deconstruction()
    {
        var (columnName, kind, scalarType, isNullable, sourceJsonPath, targetResource, storage) =
            _aliasColumn;

        columnName.Should().Be(new DbColumnName("SchoolId"));
        kind.Should().Be(ColumnKind.Scalar);
        scalarType.Should().Be(new RelationalScalarType(ScalarKind.Int32));
        isNullable.Should().BeTrue();
        sourceJsonPath.Should().BeNull();
        targetResource.Should().BeNull();
        storage
            .Should()
            .Be(
                new ColumnStorage.UnifiedAlias(
                    new DbColumnName("SchoolId_Canonical"),
                    new DbColumnName("SchoolId_Present")
                )
            );
    }
}
