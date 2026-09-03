// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Runs;

/// <summary>
/// The report step: loads the configured baseline and final-gate evidence directories,
/// evaluates every gate, and writes final-report.md/final-report.json. Needs no database —
/// it reads artifacts only — so reviewers can regenerate the report from retained evidence
/// without rerunning any performance measurement. The test succeeds when the report is
/// produced; the gate verdict lives in the report itself, where Fail and Inconclusive are
/// first-class outcomes rather than test failures.
/// </summary>
[TestFixture]
[Explicit("Report step: evaluates existing evidence directories and writes the final report")]
[Category("Performance")]
public class Given_FinalGateReportRun
{
    [Test]
    public async Task It_evaluates_the_configured_evidence_and_writes_the_report()
    {
        PerfFinalGateReportSettings settings = PerfFinalGateReportSettings.FromEnvironment();

        List<PerfFinalGateProviderEvidence> evidence = [];
        foreach (PerfFinalGateReportProviderDirectories provider in settings.Providers)
        {
            evidence.Add(
                new PerfFinalGateProviderEvidence(
                    PerfFinalGateEvidenceLoader.LoadBaseline(provider.BaselineDirectory),
                    PerfFinalGateEvidenceLoader.LoadFinalGate(provider.PrimaryDirectory),
                    PerfFinalGateEvidenceLoader.LoadFinalGate(provider.DescriptorsDirectory)
                )
            );
        }

        PerfFinalGateEvaluation evaluation = PerfFinalGateEvaluator.Evaluate(evidence);
        PerfFinalGateReportWriter.Write(settings.ReportDirectory, evaluation);

        await TestContext.Out.WriteLineAsync(
            $"Final-gate report ({evaluation.OverallStatus}) written to {settings.ReportDirectory}"
        );
    }
}
