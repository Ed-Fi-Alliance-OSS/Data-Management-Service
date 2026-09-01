// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// One measured final-gate cell together with its replay evidence files, ready for assembly.
/// </summary>
public sealed record PerfFinalGateCellArtifacts(
    PerfFinalGateScenarioResult Row,
    IReadOnlyList<PerfArtifactFile> Files
);

/// <summary>
/// Accumulates a primary run's measured cells across its three phases and structurally
/// enforces their order: the pristine cells measure data byte-identical to the baseline
/// capture, the authorization seeding and the filtered overlay each mutate the shared load
/// irreversibly, so a phase can begin only when its predecessor has completed and no earlier
/// phase can be entered again. Construction records the identity facts the pristine
/// guardrails captured.
/// </summary>
public sealed class PerfFinalGateRunAccumulator(
    PerfProvider provider,
    PerfFixtureDefinition definition,
    long deepOffset,
    int warmupIterations,
    int measuredIterations,
    string runnerCommit,
    string subjectCommit,
    IReadOnlyList<string> worktreeDirtyPaths
)
{
    private static readonly IReadOnlyList<PerfPrimaryPhase> _phaseOrder =
    [
        PerfPrimaryPhase.Pristine,
        PerfPrimaryPhase.AuthorizedSeeded,
        PerfPrimaryPhase.FilteredOverlay,
    ];

    private readonly List<PerfFinalGateCellArtifacts> _cells = [];
    private readonly List<PerfFinalGatePhaseLogEntry> _phaseLog = [];
    private PerfPrimaryPhase? _openPhase;
    private int _completedPhaseCount;

    public PerfProvider Provider { get; } = provider;

    public PerfFixtureDefinition Definition { get; } = definition;

    public long DeepOffset { get; } = deepOffset;

    public int WarmupIterations { get; } = warmupIterations;

    public int MeasuredIterations { get; } = measuredIterations;

    public string RunnerCommit { get; } = runnerCommit;

    public string SubjectCommit { get; } = subjectCommit;

    public IReadOnlyList<string> WorktreeDirtyPaths { get; } = worktreeDirtyPaths;

    public IReadOnlyList<PerfFinalGateCellArtifacts> Cells => _cells;

    public IReadOnlyList<PerfFinalGatePhaseLogEntry> PhaseLog => _phaseLog;

    public bool AllPhasesComplete => _completedPhaseCount == _phaseOrder.Count;

    /// <summary>
    /// Opens the next phase, which must be exactly the next one in the mandatory order.
    /// </summary>
    public void BeginPhase(PerfPrimaryPhase phase)
    {
        if (_openPhase is not null)
        {
            throw new PerfObservationException(
                $"Phase '{_openPhase}' is still open; it must complete before another begins."
            );
        }

        if (_completedPhaseCount >= _phaseOrder.Count || _phaseOrder[_completedPhaseCount] != phase)
        {
            string expected =
                _completedPhaseCount < _phaseOrder.Count
                    ? _phaseOrder[_completedPhaseCount].ToString()
                    : "none";
            throw new PerfObservationException(
                $"Phase '{phase}' cannot begin: the next expected phase is '{expected}'."
            );
        }

        _openPhase = phase;
    }

    /// <summary>
    /// Records a mutation applied at the start of the open phase — the authorization seeding
    /// or the filtered overlay. The pristine phase records none by definition.
    /// </summary>
    public void RecordMutation(PerfFinalGatePhaseLogEntry entry)
    {
        if (_openPhase is null or PerfPrimaryPhase.Pristine)
        {
            throw new PerfObservationException(
                "A mutation can only be recorded inside an open authorized or filtered phase."
            );
        }

        _phaseLog.Add(entry);
    }

    public void AddCell(PerfFinalGateCellArtifacts cell)
    {
        if (_openPhase is null)
        {
            throw new PerfObservationException("Cells can only be added inside an open phase.");
        }

        _cells.Add(cell);
    }

    public void CompletePhase(PerfPrimaryPhase phase)
    {
        if (_openPhase != phase)
        {
            throw new PerfObservationException($"Phase '{phase}' is not the open phase and cannot complete.");
        }

        _openPhase = null;
        _completedPhaseCount++;
    }
}
