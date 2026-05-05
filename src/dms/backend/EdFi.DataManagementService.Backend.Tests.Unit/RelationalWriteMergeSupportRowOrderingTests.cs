// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Direct unit tests for the shared row-ordering helpers on
/// <see cref="RelationalWriteMergeSupport"/>. The helpers are shared between the
/// no-profile and profile collection-merge paths and apply a sort by
/// (parent locator, ordinal) for top-level collection tables and by (parent locator)
/// for collection-aligned extension scopes, but only when every row in the input has
/// literal binding values at each ordering binding index ("fully bound" guard). When
/// any binding value at one of the ordering indexes is non-Literal, the helper returns
/// the input rows unchanged.
/// </summary>
[TestFixture]
public class Given_RelationalWriteMergeSupport_Ordering_For_A_Collection_Table_With_Out_Of_Order_Rows
{
    private IReadOnlyList<RelationalWriteMergedTableRow> _input = null!;
    private IReadOnlyList<RelationalWriteMergedTableRow> _result = null!;
    private RelationalWriteMergedTableRow _rowOrdinal2 = null!;
    private RelationalWriteMergedTableRow _rowOrdinal0 = null!;
    private RelationalWriteMergedTableRow _rowOrdinal1 = null!;

    [SetUp]
    public void Setup()
    {
        var collectionTableModel = AdapterFactoryTestFixtures.BuildCollectionTableModel();
        var collectionPlan = AdapterFactoryTestFixtures.BuildCollectionTableWritePlan(collectionTableModel);

        // Layout (binding index → column):
        //   0 = CollectionItemId (Precomputed)
        //   1 = School_DocumentId  ← ImmediateParentScopeLocatorColumns resolves here
        //   2 = Ordinal            ← CollectionMergePlan.OrdinalBindingIndex
        //   3 = AddressType (Scalar)
        //
        // Three rows under the same parent locator (1L). Natural sort order by
        // (parent, ordinal) ascending is ordinals 0, 1, 2.
        _rowOrdinal2 = new RelationalWriteMergedTableRow(
            values:
            [
                new FlattenedWriteValue.Literal(100L),
                new FlattenedWriteValue.Literal(1L),
                new FlattenedWriteValue.Literal(2),
                new FlattenedWriteValue.Literal("Billing"),
            ],
            comparableValues: [new FlattenedWriteValue.Literal("Billing"), new FlattenedWriteValue.Literal(2)]
        );
        _rowOrdinal0 = new RelationalWriteMergedTableRow(
            values:
            [
                new FlattenedWriteValue.Literal(101L),
                new FlattenedWriteValue.Literal(1L),
                new FlattenedWriteValue.Literal(0),
                new FlattenedWriteValue.Literal("Mailing"),
            ],
            comparableValues: [new FlattenedWriteValue.Literal("Mailing"), new FlattenedWriteValue.Literal(0)]
        );
        _rowOrdinal1 = new RelationalWriteMergedTableRow(
            values:
            [
                new FlattenedWriteValue.Literal(102L),
                new FlattenedWriteValue.Literal(1L),
                new FlattenedWriteValue.Literal(1),
                new FlattenedWriteValue.Literal("Physical"),
            ],
            comparableValues:
            [
                new FlattenedWriteValue.Literal("Physical"),
                new FlattenedWriteValue.Literal(1),
            ]
        );

        // Scrambled (parent, ordinal) order: ordinal 2, 0, 1. Natural ascending order
        // is the second, third, first row (ordinals 0, 1, 2).
        _input = [_rowOrdinal2, _rowOrdinal0, _rowOrdinal1];

        _result = RelationalWriteMergeSupport.OrderCollectionRowsForComparisonIfFullyBound(
            collectionPlan,
            _input
        );
    }

    [Test]
    public void It_orders_rows_by_parent_locator_then_ordinal()
    {
        _result.Should().ContainInOrder(_rowOrdinal0, _rowOrdinal1, _rowOrdinal2);
    }

    [Test]
    public void It_returns_a_new_list_not_the_input_list()
    {
        // Sanity: the helper must produce a new collection when sorting so it does not
        // mutate the caller's list. (Implementation today returns a freshly-allocated
        // array via OrderBy().ToArray().)
        _result.Should().NotBeSameAs(_input);
    }
}

/// <summary>
/// Direct unit test for
/// <see cref="RelationalWriteMergeSupport.OrderCollectionAlignedExtensionScopeRowsForComparisonIfFullyBound"/>.
/// Collection-aligned extension scope tables have no ordinal column, so the ordering
/// set is just the table's <c>ImmediateParentScopeLocatorColumns</c>. Rows under
/// different parent locators must come back in ascending parent-locator order.
/// </summary>
[TestFixture]
public class Given_RelationalWriteMergeSupport_Ordering_For_A_Collection_Aligned_Extension_Scope_With_Out_Of_Order_Rows
{
    private IReadOnlyList<RelationalWriteMergedTableRow> _result = null!;
    private RelationalWriteMergedTableRow _rowAtParent2 = null!;
    private RelationalWriteMergedTableRow _rowAtParent1 = null!;

    [SetUp]
    public void Setup()
    {
        var scopeTableModel = AdapterFactoryTestFixtures.BuildCollectionExtensionScopeTableModel();
        var scopePlan = AdapterFactoryTestFixtures.BuildCollectionExtensionScopeTableWritePlan(
            scopeTableModel
        );

        // Layout (binding index → column):
        //   0 = BaseCollectionItemId (ParentKeyPart) ← only ImmediateParentScopeLocatorColumns entry
        //   1 = FavoriteColor (Scalar)
        //
        // Two rows under different parent locators: parent 2 first, then parent 1.
        // Natural ascending parent order is parent 1, then parent 2.
        _rowAtParent2 = new RelationalWriteMergedTableRow(
            values: [new FlattenedWriteValue.Literal(2L), new FlattenedWriteValue.Literal("Blue")],
            comparableValues: [new FlattenedWriteValue.Literal(2L), new FlattenedWriteValue.Literal("Blue")]
        );
        _rowAtParent1 = new RelationalWriteMergedTableRow(
            values: [new FlattenedWriteValue.Literal(1L), new FlattenedWriteValue.Literal("Red")],
            comparableValues: [new FlattenedWriteValue.Literal(1L), new FlattenedWriteValue.Literal("Red")]
        );

        IReadOnlyList<RelationalWriteMergedTableRow> input = [_rowAtParent2, _rowAtParent1];

        _result =
            RelationalWriteMergeSupport.OrderCollectionAlignedExtensionScopeRowsForComparisonIfFullyBound(
                scopePlan,
                input
            );
    }

    [Test]
    public void It_orders_rows_by_parent_locator()
    {
        _result.Should().ContainInOrder(_rowAtParent1, _rowAtParent2);
    }
}

/// <summary>
/// Direct unit test for the "fully-bound" guard in
/// <see cref="RelationalWriteMergeSupport.OrderCollectionRowsForComparisonIfFullyBound"/>.
/// When any row carries a non-Literal <see cref="FlattenedWriteValue"/> at one of the
/// ordering binding indexes (parent locator or ordinal), the helper must short-circuit
/// and return the input rows unchanged. This keeps the sort safe at synthesizer time
/// before pre-allocation has resolved unresolved <c>CollectionItemId</c> tokens.
/// </summary>
[TestFixture]
public class Given_RelationalWriteMergeSupport_Ordering_With_Non_Literal_Bindings
{
    private IReadOnlyList<RelationalWriteMergedTableRow> _input = null!;
    private IReadOnlyList<RelationalWriteMergedTableRow> _result = null!;

    [SetUp]
    public void Setup()
    {
        var collectionTableModel = AdapterFactoryTestFixtures.BuildCollectionTableModel();
        var collectionPlan = AdapterFactoryTestFixtures.BuildCollectionTableWritePlan(collectionTableModel);

        // Same layout as the top-level collection fixture above. Three rows in scrambled
        // (parent, ordinal) order, but the second row's parent-locator binding (index 1)
        // carries a non-Literal FlattenedWriteValue (UnresolvedCollectionItemId — any
        // non-Literal subclass works for the guard). The guard inspects the ordering
        // bindings (parent locator at index 1, ordinal at index 2) and must short-circuit
        // because index 1 of one row is not a Literal.
        var rowOrdinal2 = new RelationalWriteMergedTableRow(
            values:
            [
                new FlattenedWriteValue.Literal(100L),
                new FlattenedWriteValue.Literal(1L),
                new FlattenedWriteValue.Literal(2),
                new FlattenedWriteValue.Literal("Billing"),
            ],
            comparableValues: [new FlattenedWriteValue.Literal("Billing"), new FlattenedWriteValue.Literal(2)]
        );
        var rowWithNonLiteralParent = new RelationalWriteMergedTableRow(
            values:
            [
                new FlattenedWriteValue.Literal(101L),
                FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                new FlattenedWriteValue.Literal(0),
                new FlattenedWriteValue.Literal("Mailing"),
            ],
            comparableValues: [new FlattenedWriteValue.Literal("Mailing"), new FlattenedWriteValue.Literal(0)]
        );
        var rowOrdinal1 = new RelationalWriteMergedTableRow(
            values:
            [
                new FlattenedWriteValue.Literal(102L),
                new FlattenedWriteValue.Literal(1L),
                new FlattenedWriteValue.Literal(1),
                new FlattenedWriteValue.Literal("Physical"),
            ],
            comparableValues:
            [
                new FlattenedWriteValue.Literal("Physical"),
                new FlattenedWriteValue.Literal(1),
            ]
        );

        _input = [rowOrdinal2, rowWithNonLiteralParent, rowOrdinal1];

        _result = RelationalWriteMergeSupport.OrderCollectionRowsForComparisonIfFullyBound(
            collectionPlan,
            _input
        );
    }

    [Test]
    public void It_returns_input_unchanged_when_a_binding_value_is_not_literal()
    {
        // The guard short-circuits and returns the original IReadOnlyList reference, so
        // the result must be reference-equal to the input (no new array materialized).
        _result.Should().BeSameAs(_input);
    }
}
