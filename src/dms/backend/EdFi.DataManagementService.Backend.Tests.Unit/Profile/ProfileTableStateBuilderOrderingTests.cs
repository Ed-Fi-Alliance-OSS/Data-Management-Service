// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

/// <summary>
/// Direct test for <see cref="ProfileTableStateBuilder.Build"/>. The builder must
/// return collection rows sorted by parent-locator + ordinal so the executor's
/// positional <c>SequenceEqual</c> against the stored hydrated rowset can fire the
/// guarded no-op short-circuit safely.
///
/// Setup: a top-level base-collection <see cref="TableWritePlan"/> built via
/// <see cref="AdapterFactoryTestFixtures.BuildCollectionTableWritePlan"/> (column layout
/// [CollectionItemId, School_DocumentId, Ordinal, AddressType] with
/// <c>OrdinalBindingIndex = 2</c>, <c>ImmediateParentScopeLocatorColumns = ["School_DocumentId"]</c>,
/// and <c>CollectionMergePlan</c> set). Three rows under the same parent (parent locator = 1)
/// with literal ordinals 2, 0, 1 are added in that scrambled order to both the merged-rows
/// and current-rows accumulators. After <see cref="ProfileTableStateBuilder.Build"/>, rows
/// must come back ordered by (parent, ordinal) ascending — i.e. ordinals 0, 1, 2.
/// </summary>
[TestFixture]
public class Given_A_ProfileTableStateBuilder_With_Out_Of_Order_Rows_For_A_Top_Level_Collection_Table
{
    private RelationalWriteMergedTableState _state = null!;

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
        // (parent, ordinal) ascending is ordinals 0, 1, 2 → "Mailing", "Physical",
        // "Billing". Rows are added to the builder in scrambled order (ordinal 2 first,
        // then 0, then 1); without the builder sort wiring, Build() returns insertion order.
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
        var rowOrdinal0 = new RelationalWriteMergedTableRow(
            values:
            [
                new FlattenedWriteValue.Literal(101L),
                new FlattenedWriteValue.Literal(1L),
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

        var builder = new ProfileTableStateBuilder(collectionPlan);

        // Add in reverse-of-natural order (ordinal 2, then 0, then 1) to both accumulators.
        builder.AddCurrentRow(rowOrdinal2);
        builder.AddCurrentRow(rowOrdinal0);
        builder.AddCurrentRow(rowOrdinal1);

        builder.AddMergedRow(rowOrdinal2);
        builder.AddMergedRow(rowOrdinal0);
        builder.AddMergedRow(rowOrdinal1);

        _state = builder.Build();
    }

    [Test]
    public void It_orders_merged_rows_by_parent_locator_then_ordinal()
    {
        _state.MergedRows.Length.Should().Be(3);
        ((FlattenedWriteValue.Literal)_state.MergedRows[0].Values[2]).Value.Should().Be(0);
        ((FlattenedWriteValue.Literal)_state.MergedRows[1].Values[2]).Value.Should().Be(1);
        ((FlattenedWriteValue.Literal)_state.MergedRows[2].Values[2]).Value.Should().Be(2);
    }

    [Test]
    public void It_orders_current_rows_by_parent_locator_then_ordinal()
    {
        _state.CurrentRows.Length.Should().Be(3);
        ((FlattenedWriteValue.Literal)_state.CurrentRows[0].Values[2]).Value.Should().Be(0);
        ((FlattenedWriteValue.Literal)_state.CurrentRows[1].Values[2]).Value.Should().Be(1);
        ((FlattenedWriteValue.Literal)_state.CurrentRows[2].Values[2]).Value.Should().Be(2);
    }
}
