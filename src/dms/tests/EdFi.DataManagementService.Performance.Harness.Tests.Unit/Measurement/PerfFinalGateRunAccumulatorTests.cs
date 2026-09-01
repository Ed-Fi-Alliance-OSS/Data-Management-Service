// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_The_Final_Gate_Run_Accumulator
{
    private static PerfFinalGateRunAccumulator Create() =>
        new(
            PerfProvider.Postgresql,
            new PerfFixtureDefinition(PerfFixtureKind.Smoke10k),
            deepOffset: 9_000,
            warmupIterations: 5,
            measuredIterations: 30,
            ResultSamples.RunnerCommit,
            ResultSamples.SubjectCommit,
            worktreeDirtyPaths: []
        );

    private static PerfFinalGateCellArtifacts Cell() =>
        new(FinalGateResultSamples.Row(PerfFinalGateScenarios.PrimaryCellsInExecutionOrder[0]), []);

    private static PerfFinalGatePhaseLogEntry Mutation(PerfPrimaryPhase phase) =>
        new(PerfFinalGateScenarios.PhaseName(phase), "test mutation", [new PerfSetting("fact", "1")]);

    [Test]
    public void It_walks_the_three_phases_in_order_to_completion()
    {
        PerfFinalGateRunAccumulator accumulator = Create();

        accumulator.BeginPhase(PerfPrimaryPhase.Pristine);
        accumulator.AddCell(Cell());
        accumulator.CompletePhase(PerfPrimaryPhase.Pristine);

        accumulator.BeginPhase(PerfPrimaryPhase.AuthorizedSeeded);
        accumulator.RecordMutation(Mutation(PerfPrimaryPhase.AuthorizedSeeded));
        accumulator.AddCell(Cell());
        accumulator.CompletePhase(PerfPrimaryPhase.AuthorizedSeeded);

        accumulator.BeginPhase(PerfPrimaryPhase.FilteredOverlay);
        accumulator.RecordMutation(Mutation(PerfPrimaryPhase.FilteredOverlay));
        accumulator.CompletePhase(PerfPrimaryPhase.FilteredOverlay);

        accumulator.AllPhasesComplete.Should().BeTrue();
        accumulator.Cells.Should().HaveCount(2);
        accumulator.PhaseLog.Should().HaveCount(2);
    }

    [Test]
    public void It_refuses_to_begin_a_later_phase_first()
    {
        PerfFinalGateRunAccumulator accumulator = Create();

        Action act = () => accumulator.BeginPhase(PerfPrimaryPhase.AuthorizedSeeded);

        act.Should().Throw<PerfObservationException>().WithMessage("*Pristine*");
    }

    [Test]
    public void It_refuses_to_reenter_a_completed_phase()
    {
        PerfFinalGateRunAccumulator accumulator = Create();
        accumulator.BeginPhase(PerfPrimaryPhase.Pristine);
        accumulator.CompletePhase(PerfPrimaryPhase.Pristine);

        Action act = () => accumulator.BeginPhase(PerfPrimaryPhase.Pristine);

        act.Should().Throw<PerfObservationException>();
    }

    [Test]
    public void It_refuses_to_begin_while_a_phase_is_open()
    {
        PerfFinalGateRunAccumulator accumulator = Create();
        accumulator.BeginPhase(PerfPrimaryPhase.Pristine);

        Action act = () => accumulator.BeginPhase(PerfPrimaryPhase.AuthorizedSeeded);

        act.Should().Throw<PerfObservationException>().WithMessage("*still open*");
    }

    [Test]
    public void It_refuses_cells_outside_an_open_phase()
    {
        PerfFinalGateRunAccumulator accumulator = Create();

        Action act = () => accumulator.AddCell(Cell());

        act.Should().Throw<PerfObservationException>();
    }

    [Test]
    public void It_refuses_mutations_in_the_pristine_phase()
    {
        PerfFinalGateRunAccumulator accumulator = Create();
        accumulator.BeginPhase(PerfPrimaryPhase.Pristine);

        Action act = () => accumulator.RecordMutation(Mutation(PerfPrimaryPhase.AuthorizedSeeded));

        act.Should().Throw<PerfObservationException>();
    }

    [Test]
    public void It_refuses_to_complete_a_phase_that_is_not_open()
    {
        PerfFinalGateRunAccumulator accumulator = Create();
        accumulator.BeginPhase(PerfPrimaryPhase.Pristine);

        Action act = () => accumulator.CompletePhase(PerfPrimaryPhase.AuthorizedSeeded);

        act.Should().Throw<PerfObservationException>();
    }
}
