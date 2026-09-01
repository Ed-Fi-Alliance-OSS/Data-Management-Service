// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Configuration;

[TestFixture]
public class Given_The_Final_Gate_Primary_Cell_Order
{
    private IReadOnlyList<PerfFinalGateCell> _cells = null!;

    [SetUp]
    public void Setup()
    {
        _cells = PerfFinalGateScenarios.PrimaryCellsInExecutionOrder;
    }

    [Test]
    public void It_holds_twenty_nine_cells()
    {
        _cells.Should().HaveCount(29);
    }

    [Test]
    public void It_begins_with_the_six_unchanged_traditional_baseline_cells()
    {
        IReadOnlyList<PerfFinalGateCell> traditional = [.. _cells.Take(6)];

        traditional
            .Select(cell => (cell.ScenarioId, cell.PageSize))
            .Should()
            .Equal(
                PerfScenarios.AllIds.SelectMany(scenarioId =>
                    PerfScenarios.PageSizes.Select(pageSize => (scenarioId, (int?)pageSize))
                )
            );

        traditional.Should().OnlyContain(cell => cell.Family == PerfScenarioFamily.Traditional);
        traditional.Should().OnlyContain(cell => cell.Variant == PerfFinalGateVariant.Unfiltered);
    }

    [Test]
    public void It_orders_phases_pristine_then_authorized_then_filtered()
    {
        IReadOnlyList<PerfPrimaryPhase> phases =
        [
            .. _cells.Select(cell =>
                PerfFinalGateScenarios.PhaseOf(cell.Variant)
                ?? throw new InvalidOperationException("Primary cells must all map to a phase.")
            ),
        ];

        phases.Should().BeInAscendingOrder();
        phases.Should().Contain(PerfPrimaryPhase.Pristine);
        phases.Should().Contain(PerfPrimaryPhase.AuthorizedSeeded);
        phases.Should().Contain(PerfPrimaryPhase.FilteredOverlay);
    }

    [Test]
    public void It_gives_every_regular_variant_three_cursor_ranges_at_both_page_sizes()
    {
        foreach (
            PerfFinalGateVariant variant in (PerfFinalGateVariant[])
                [
                    PerfFinalGateVariant.Unfiltered,
                    PerfFinalGateVariant.Authorized,
                    PerfFinalGateVariant.Filtered,
                ]
        )
        {
            _cells
                .Where(cell => cell.Family == PerfScenarioFamily.Cursor && cell.Variant == variant)
                .Select(cell => (cell.CursorRange, cell.PageSize))
                .Should()
                .Equal(
                    (PerfCursorRange.First, 25),
                    (PerfCursorRange.First, 500),
                    (PerfCursorRange.Middle, 25),
                    (PerfCursorRange.Middle, 500),
                    (PerfCursorRange.Last, 25),
                    (PerfCursorRange.Last, 500)
                );
        }
    }

    [Test]
    public void It_measures_partition_numbers_one_ten_two_hundred_on_the_unfiltered_variant()
    {
        _cells
            .Where(cell =>
                cell.Family == PerfScenarioFamily.Partition && cell.Variant == PerfFinalGateVariant.Unfiltered
            )
            .Select(cell => cell.PartitionNumber)
            .Should()
            .Equal(1, 10, 200);
    }

    [Test]
    public void It_measures_partition_number_ten_only_on_the_authorized_and_filtered_variants()
    {
        foreach (
            PerfFinalGateVariant variant in (PerfFinalGateVariant[])
                [PerfFinalGateVariant.Authorized, PerfFinalGateVariant.Filtered]
        )
        {
            _cells
                .Where(cell => cell.Family == PerfScenarioFamily.Partition && cell.Variant == variant)
                .Select(cell => cell.PartitionNumber)
                .Should()
                .Equal(PerfFinalGateScenarios.ScopedPartitionNumber);
        }
    }

    [Test]
    public void It_contains_no_descriptor_cells()
    {
        _cells.Should().OnlyContain(cell => cell.Variant != PerfFinalGateVariant.Descriptor);
    }

    [Test]
    public void It_uses_unique_scenario_cell_identities()
    {
        _cells
            .Select(cell => (cell.ScenarioId, cell.PageSize, cell.PartitionNumber))
            .Should()
            .OnlyHaveUniqueItems();
    }
}

[TestFixture]
public class Given_The_Final_Gate_Descriptor_Cell_Order
{
    private IReadOnlyList<PerfFinalGateCell> _cells = null!;

    [SetUp]
    public void Setup()
    {
        _cells = PerfFinalGateScenarios.DescriptorCellsInExecutionOrder;
    }

    [Test]
    public void It_holds_seven_cells()
    {
        _cells.Should().HaveCount(7);
    }

    [Test]
    public void It_holds_only_descriptor_variant_cells()
    {
        _cells.Should().OnlyContain(cell => cell.Variant == PerfFinalGateVariant.Descriptor);
    }

    [Test]
    public void It_runs_three_cursor_ranges_at_both_page_sizes_then_one_partition_cell()
    {
        _cells
            .Take(6)
            .Select(cell => (cell.Family, cell.CursorRange, cell.PageSize))
            .Should()
            .Equal(
                (PerfScenarioFamily.Cursor, PerfCursorRange.First, 25),
                (PerfScenarioFamily.Cursor, PerfCursorRange.First, 500),
                (PerfScenarioFamily.Cursor, PerfCursorRange.Middle, 25),
                (PerfScenarioFamily.Cursor, PerfCursorRange.Middle, 500),
                (PerfScenarioFamily.Cursor, PerfCursorRange.Last, 25),
                (PerfScenarioFamily.Cursor, PerfCursorRange.Last, 500)
            );

        _cells[6].Family.Should().Be(PerfScenarioFamily.Partition);
        _cells[6].PartitionNumber.Should().Be(PerfFinalGateScenarios.ScopedPartitionNumber);
    }

    [Test]
    public void It_never_reruns_traditional_scenarios_on_descriptors()
    {
        _cells.Should().OnlyContain(cell => cell.Family != PerfScenarioFamily.Traditional);
    }

    [Test]
    public void It_has_no_primary_phase()
    {
        PerfFinalGateScenarios.PhaseOf(PerfFinalGateVariant.Descriptor).Should().BeNull();
    }
}

[TestFixture]
public class Given_Final_Gate_Scenario_Identifiers
{
    [Test]
    public void It_names_cursor_cells_by_variant_and_range()
    {
        PerfFinalGateScenarios
            .CursorScenarioId(PerfFinalGateVariant.Unfiltered, PerfCursorRange.First)
            .Should()
            .Be("cursor-unfiltered-first");
        PerfFinalGateScenarios
            .CursorScenarioId(PerfFinalGateVariant.Authorized, PerfCursorRange.Middle)
            .Should()
            .Be("cursor-authorized-middle");
        PerfFinalGateScenarios
            .CursorScenarioId(PerfFinalGateVariant.Descriptor, PerfCursorRange.Last)
            .Should()
            .Be("cursor-descriptor-last");
    }

    [Test]
    public void It_names_partition_cells_by_variant_and_requested_number()
    {
        PerfFinalGateScenarios
            .PartitionScenarioId(PerfFinalGateVariant.Unfiltered, 200)
            .Should()
            .Be("partition-unfiltered-200");
        PerfFinalGateScenarios
            .PartitionScenarioId(PerfFinalGateVariant.Filtered, 10)
            .Should()
            .Be("partition-filtered-10");
    }

    [Test]
    public void It_keeps_scenario_ids_unique_across_primary_and_descriptor_runs()
    {
        IReadOnlyList<(string, int?, int?)> identities =
        [
            .. PerfFinalGateScenarios
                .PrimaryCellsInExecutionOrder.Concat(PerfFinalGateScenarios.DescriptorCellsInExecutionOrder)
                .Select(cell => (cell.ScenarioId, cell.PageSize, cell.PartitionNumber)),
        ];

        identities.Should().OnlyHaveUniqueItems();
        identities.Should().HaveCount(36);
    }

    [Test]
    public void It_never_collides_with_the_traditional_baseline_ids()
    {
        foreach (
            PerfFinalGateCell cell in PerfFinalGateScenarios.PrimaryCellsInExecutionOrder.Where(cell =>
                cell.Family != PerfScenarioFamily.Traditional
            )
        )
        {
            PerfScenarios.IsKnown(cell.ScenarioId).Should().BeFalse(cell.ScenarioId);
        }
    }
}
