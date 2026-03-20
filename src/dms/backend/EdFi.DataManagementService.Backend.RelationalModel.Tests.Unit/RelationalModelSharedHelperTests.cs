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
                ("SchoolId", new RelationalScalarType(ScalarKind.Int32)),
                ("Ordinal", new RelationalScalarType(ScalarKind.Int32))
            );
    }
}
