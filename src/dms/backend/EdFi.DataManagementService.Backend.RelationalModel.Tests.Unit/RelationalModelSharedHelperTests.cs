// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_Relational_Model_Shared_Helpers
{
    /// <summary>
    /// It should match prefix segments for nested array scopes.
    /// </summary>
    [Test]
    public void It_should_match_prefix_segments_for_nested_array_scopes()
    {
        var prefix = JsonPathExpressionCompiler.Compile("$.addresses[*]");
        var path = JsonPathExpressionCompiler.Compile("$.addresses[*].periods[*].beginDate");

        RelationalModelSetSchemaHelpers.IsPrefixOf(prefix.Segments, path.Segments).Should().BeTrue();
    }

    /// <summary>
    /// It should reject mismatched prefix segments.
    /// </summary>
    [Test]
    public void It_should_reject_mismatched_prefix_segments()
    {
        var prefix = JsonPathExpressionCompiler.Compile("$.addresses[*]");
        var path = JsonPathExpressionCompiler.Compile("$.contacts[*].periods[*].beginDate");

        RelationalModelSetSchemaHelpers.IsPrefixOf(prefix.Segments, path.Segments).Should().BeFalse();
    }

    /// <summary>
    /// It should derive scope-relative semantic identity paths.
    /// </summary>
    [Test]
    public void It_should_derive_scope_relative_semantic_identity_paths()
    {
        var jsonScope = JsonPathExpressionCompiler.Compile("$.addresses[*]");
        var path = JsonPathExpressionCompiler.Compile("$.addresses[*].schoolReference.schoolId");

        RelationalModelSetSchemaHelpers
            .DeriveScopeRelativeSemanticIdentityPath(jsonScope, path)
            .Canonical.Should()
            .Be("$.schoolReference.schoolId");
    }

    /// <summary>
    /// It should reject scope-relative semantic identity paths outside the owning scope.
    /// </summary>
    [Test]
    public void It_should_reject_scope_relative_semantic_identity_paths_outside_the_owning_scope()
    {
        var jsonScope = JsonPathExpressionCompiler.Compile("$.addresses[*]");
        var path = JsonPathExpressionCompiler.Compile("$.contacts[*].schoolReference.schoolId");

        Action action = () =>
            RelationalModelSetSchemaHelpers.DeriveScopeRelativeSemanticIdentityPath(jsonScope, path);

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot derive scope-relative semantic identity path for "
                    + "'$.contacts[*].schoolReference.schoolId': scope '$.addresses[*]' is not a prefix."
            );
    }

    /// <summary>
    /// It should reject scope-relative semantic identity paths that cross descendant arrays.
    /// </summary>
    [Test]
    public void It_should_reject_scope_relative_semantic_identity_paths_that_cross_descendant_arrays()
    {
        var jsonScope = JsonPathExpressionCompiler.Compile("$.addresses[*]");
        var path = JsonPathExpressionCompiler.Compile("$.addresses[*].periods[*].beginDate");

        Action action = () =>
            RelationalModelSetSchemaHelpers.DeriveScopeRelativeSemanticIdentityPath(jsonScope, path);

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot derive scope-relative semantic identity path for "
                    + "'$.addresses[*].periods[*].beginDate' under scope '$.addresses[*]': "
                    + "stripped path contains '[*]'."
            );
    }

    /// <summary>
    /// It should assign expected scalar types for seeded key and locator columns.
    /// </summary>
    [Test]
    public void It_should_assign_expected_scalar_types_for_seeded_key_and_locator_columns()
    {
        var columns = RelationalModelSystemColumnFactory.BuildKeyColumns([
            new DbKeyColumn(RelationalNameConventions.DocumentIdColumnName, ColumnKind.ParentKeyPart),
            new DbKeyColumn(RelationalNameConventions.CollectionItemIdColumnName, ColumnKind.CollectionKey),
            new DbKeyColumn(
                RelationalNameConventions.ParentCollectionItemIdColumnName,
                ColumnKind.ParentKeyPart
            ),
            new DbKeyColumn(new DbColumnName("SchoolId"), ColumnKind.ParentKeyPart),
            new DbKeyColumn(RelationalNameConventions.OrdinalColumnName, ColumnKind.Ordinal),
        ]);

        columns
            .Select(column => (column.ColumnName.Value, column.ScalarType))
            .Should()
            .Equal(
                ("DocumentId", new RelationalScalarType(ScalarKind.Int64)),
                ("CollectionItemId", new RelationalScalarType(ScalarKind.Int64)),
                ("ParentCollectionItemId", new RelationalScalarType(ScalarKind.Int64)),
                ("SchoolId", new RelationalScalarType(ScalarKind.Int64)),
                ("Ordinal", new RelationalScalarType(ScalarKind.Int32))
            );
    }

    /// <summary>
    /// It should build stable-identity seeded columns for base and extension table kinds from shared metadata.
    /// </summary>
    [Test]
    public void It_should_build_stable_identity_seeded_columns_for_base_and_extension_table_kinds()
    {
        var baseCollectionColumns = RelationalModelStableIdentityHelper.BuildIdentityColumns(
            RelationalModelStableIdentityHelper.BuildCollectionTableIdentityMetadata(
                "School",
                isNestedCollection: true
            )
        );
        var collectionExtensionScopeColumns = RelationalModelStableIdentityHelper.BuildIdentityColumns(
            RelationalModelStableIdentityHelper.BuildCollectionExtensionScopeIdentityMetadata("School")
        );
        var extensionChildColumns = RelationalModelStableIdentityHelper.BuildIdentityColumns(
            RelationalModelStableIdentityHelper.BuildExtensionChildTableIdentityMetadata(
                "School",
                DbTableKind.CollectionExtensionScope
            )
        );

        baseCollectionColumns
            .Select(column => (column.ColumnName.Value, column.Kind, column.ScalarType))
            .Should()
            .Equal(
                ("CollectionItemId", ColumnKind.CollectionKey, new RelationalScalarType(ScalarKind.Int64)),
                ("School_DocumentId", ColumnKind.ParentKeyPart, new RelationalScalarType(ScalarKind.Int64)),
                (
                    "ParentCollectionItemId",
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64)
                ),
                ("Ordinal", ColumnKind.Ordinal, new RelationalScalarType(ScalarKind.Int32))
            );

        collectionExtensionScopeColumns
            .Select(column => (column.ColumnName.Value, column.Kind, column.ScalarType))
            .Should()
            .Equal(
                (
                    "BaseCollectionItemId",
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64)
                ),
                ("School_DocumentId", ColumnKind.ParentKeyPart, new RelationalScalarType(ScalarKind.Int64))
            );

        extensionChildColumns
            .Select(column => (column.ColumnName.Value, column.Kind, column.ScalarType))
            .Should()
            .Equal(
                ("CollectionItemId", ColumnKind.CollectionKey, new RelationalScalarType(ScalarKind.Int64)),
                ("School_DocumentId", ColumnKind.ParentKeyPart, new RelationalScalarType(ScalarKind.Int64)),
                (
                    "BaseCollectionItemId",
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64)
                ),
                ("Ordinal", ColumnKind.Ordinal, new RelationalScalarType(ScalarKind.Int32))
            );
    }

    /// <summary>
    /// It should build parent-scope FK columns and targets for root, collection, and extension parents.
    /// </summary>
    [Test]
    public void It_should_build_parent_scope_fk_columns_and_targets_for_root_collection_and_extension_parents()
    {
        var rootTable = CreateTable(
            "School",
            RelationalModelStableIdentityHelper.BuildRootTableIdentityMetadata()
        );
        var rootExtensionTable = CreateTable(
            "SchoolExtension",
            RelationalModelStableIdentityHelper.BuildRootExtensionTableIdentityMetadata()
        );
        var baseCollectionTable = CreateTable(
            "SchoolAddress",
            RelationalModelStableIdentityHelper.BuildCollectionTableIdentityMetadata(
                "School",
                isNestedCollection: false
            )
        );
        var nestedCollectionIdentity =
            RelationalModelStableIdentityHelper.BuildCollectionTableIdentityMetadata(
                "School",
                isNestedCollection: true
            );
        var alignedExtensionScopeTable = CreateTable(
            "SchoolExtensionAddress",
            RelationalModelStableIdentityHelper.BuildCollectionExtensionScopeIdentityMetadata("School")
        );
        var alignedExtensionChildIdentity =
            RelationalModelStableIdentityHelper.BuildExtensionChildTableIdentityMetadata(
                "School",
                DbTableKind.CollectionExtensionScope
            );

        RelationalModelStableIdentityHelper
            .BuildParentScopeForeignKeyColumns(baseCollectionTable.IdentityMetadata, rootTable)
            .Select(column => column.Value)
            .Should()
            .Equal("School_DocumentId");
        RelationalModelStableIdentityHelper
            .BuildParentScopeForeignKeyTargetColumns(rootTable)
            .Select(column => column.Value)
            .Should()
            .Equal("DocumentId");

        RelationalModelStableIdentityHelper
            .BuildParentScopeForeignKeyColumns(nestedCollectionIdentity, baseCollectionTable)
            .Select(column => column.Value)
            .Should()
            .Equal("ParentCollectionItemId", "School_DocumentId");
        RelationalModelStableIdentityHelper
            .BuildParentScopeForeignKeyTargetColumns(baseCollectionTable)
            .Select(column => column.Value)
            .Should()
            .Equal("CollectionItemId", "School_DocumentId");

        RelationalModelStableIdentityHelper
            .BuildParentScopeForeignKeyColumns(alignedExtensionChildIdentity, alignedExtensionScopeTable)
            .Select(column => column.Value)
            .Should()
            .Equal("BaseCollectionItemId", "School_DocumentId");
        RelationalModelStableIdentityHelper
            .BuildParentScopeForeignKeyTargetColumns(alignedExtensionScopeTable)
            .Select(column => column.Value)
            .Should()
            .Equal("BaseCollectionItemId", "School_DocumentId");

        RelationalModelStableIdentityHelper
            .BuildParentScopeForeignKeyColumns(rootExtensionTable.IdentityMetadata, rootTable)
            .Select(column => column.Value)
            .Should()
            .Equal("DocumentId");
        RelationalModelStableIdentityHelper
            .BuildParentScopeForeignKeyColumns(
                RelationalModelStableIdentityHelper.BuildExtensionChildTableIdentityMetadata(
                    "School",
                    DbTableKind.RootExtension
                ),
                rootExtensionTable
            )
            .Select(column => column.Value)
            .Should()
            .Equal("School_DocumentId");
        RelationalModelStableIdentityHelper
            .BuildParentScopeForeignKeyTargetColumns(rootExtensionTable)
            .Select(column => column.Value)
            .Should()
            .Equal("DocumentId");
    }

    private static DbTableModel CreateTable(string tableName, DbTableIdentityMetadata identityMetadata)
    {
        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), tableName),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey("PK_" + tableName, []),
            RelationalModelStableIdentityHelper.BuildIdentityColumns(identityMetadata),
            []
        )
        {
            IdentityMetadata = identityMetadata,
        };
    }
}
