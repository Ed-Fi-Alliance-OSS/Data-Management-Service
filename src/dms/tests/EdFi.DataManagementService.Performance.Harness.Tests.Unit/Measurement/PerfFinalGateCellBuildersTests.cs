// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_The_Final_Gate_Cell_Builders
{
    private static readonly PerfFixtureDefinition _primary = new(PerfFixtureKind.Smoke10k);

    private static readonly PerfDescriptorFixtureDefinition _descriptors = new(
        PerfDescriptorFixtureKind.DescriptorsSmoke2k
    );

    [Test]
    public void It_builds_the_unfiltered_first_cell_from_the_fixture_analytics()
    {
        PerfCursorCellRequest cell = PerfFinalGateCellBuilders.StudentCursorCell(
            PerfFinalGateVariant.Unfiltered,
            PerfCursorRange.First,
            _primary,
            pageSize: 25,
            filterQueryString: null
        );

        cell.ScenarioId.Should().Be("cursor-unfiltered-first");
        cell.StartAnchorDocumentId.Should().Be(PerfFixtureDefinition.DocumentIdFor(1));
        cell.ExpectedNextTokenInclusiveMinimum.Should().Be(PerfFixtureDefinition.DocumentIdFor(25) + 1);
        cell.ExpectedDocumentUuids.Should().HaveCount(25);
        cell.ExpectedDocumentUuids[0].Should().Be(PerfFixtureDefinition.DocumentUuidFor(1));
        cell.CaptureChannel.Should().Be(PerfCursorCaptureChannel.HydrationKeyset);
    }

    [Test]
    public void It_builds_the_authorized_last_cell_over_even_ordinals_only()
    {
        PerfCursorCellRequest cell = PerfFinalGateCellBuilders.StudentCursorCell(
            PerfFinalGateVariant.Authorized,
            PerfCursorRange.Last,
            _primary,
            pageSize: 25,
            filterQueryString: null
        );

        // 5,000 authorized candidates; the last 25 are ordinals 9952, 9954, ..., 10000.
        cell.StartAnchorDocumentId.Should().Be(PerfFixtureDefinition.DocumentIdFor(9_952));
        cell.ExpectedNextTokenInclusiveMinimum.Should().Be(PerfFixtureDefinition.DocumentIdFor(10_000) + 1);
        cell.ExpectedDocumentUuids[^1].Should().Be(PerfFixtureDefinition.DocumentUuidFor(10_000));
    }

    [Test]
    public void It_passes_the_filter_query_through_for_the_filtered_variant()
    {
        PerfCursorCellRequest cell = PerfFinalGateCellBuilders.StudentCursorCell(
            PerfFinalGateVariant.Filtered,
            PerfCursorRange.Middle,
            _primary,
            pageSize: 25,
            PerfFinalGateCellBuilders.FilteredQueryString
        );

        cell.FilterQueryString.Should().Be("birthDate=2010-06-15");
        // 1,000 filtered candidates; the middle page starts at candidate 488 = ordinal 4880.
        cell.StartAnchorDocumentId.Should().Be(PerfFixtureDefinition.DocumentIdFor(4_880));
    }

    [Test]
    public void It_builds_descriptor_cells_on_the_relational_channel_over_odd_ordinals()
    {
        PerfCursorCellRequest cell = PerfFinalGateCellBuilders.DescriptorCursorCell(
            PerfCursorRange.Last,
            _descriptors,
            pageSize: 25
        );

        cell.ScenarioId.Should().Be("cursor-descriptor-last");
        cell.ResourceEndpoint.Should().Be(PerfDescriptorFixtureDefinition.ResourceEndpoint);
        cell.CaptureChannel.Should().Be(PerfCursorCaptureChannel.RelationalCommand);
        // 1,000 accessible candidates; the last 25 are ordinals 1951, 1953, ..., 1999.
        cell.StartAnchorDocumentId.Should().Be(1_951);
        cell.ExpectedNextTokenInclusiveMinimum.Should().Be(2_000);
        cell.ExpectedDocumentUuids[^1].Should().Be(PerfDescriptorFixtureDefinition.DocumentUuidFor(1_999));
    }
}

[TestFixture]
public class Given_A_Recorded_Relational_Command
{
    [Test]
    public void It_normalizes_parameter_names_and_hashes_the_text()
    {
        EdFi.DataManagementService.Backend.RelationalCommand command = new(
            "SELECT 1 WHERE x = @cursorMin;",
            [new("@cursorMin", 100L), new("pageSize", 25L), new("nullable", null)]
        );

        PageSelectionQueryCapture capture = RelationalCommandCapture.ToPageSelectionCapture(command);

        capture.PageDocumentIdSql.Should().Be("SELECT 1 WHERE x = @cursorMin;");
        capture.ParameterValues.Should().ContainKey("cursorMin").WhoseValue.Should().Be(100L);
        capture.ParameterValues.Should().ContainKey("pageSize").WhoseValue.Should().Be(25L);
        capture.ParameterValues.Should().ContainKey("nullable").WhoseValue.Should().BeNull();
        capture.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }
}
