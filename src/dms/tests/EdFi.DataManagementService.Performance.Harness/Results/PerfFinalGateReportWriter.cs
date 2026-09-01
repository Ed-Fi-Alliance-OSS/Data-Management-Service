// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Renders an evaluation as the final-gate report: a Markdown document for reviewers and the
/// serialized evaluation as JSON for machines. Rendering is pure; <see cref="Write" /> saves
/// both files, UTF-8 without BOM, LF-only.
/// </summary>
public static class PerfFinalGateReportWriter
{
    public const string MarkdownFileName = "final-report.md";

    public const string JsonFileName = "final-report.json";

    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string reportDirectory, PerfFinalGateEvaluation evaluation)
    {
        Directory.CreateDirectory(reportDirectory);
        File.WriteAllText(
            Path.Combine(reportDirectory, MarkdownFileName),
            RenderMarkdown(evaluation),
            _utf8NoBom
        );
        File.WriteAllText(
            Path.Combine(reportDirectory, JsonFileName),
            PerfArtifactJson.Serialize(evaluation),
            _utf8NoBom
        );
    }

    public static string RenderMarkdown(PerfFinalGateEvaluation evaluation)
    {
        StringBuilder builder = new();
        builder.Append("# Performance Final Gate Report\n\n");
        builder.Append($"Overall status: **{StatusText(evaluation.OverallStatus)}**\n\n");
        builder.Append(
            "Latency gates are app-level p50/p95 ratios; SQL Server db CPU/elapsed values are "
                + "indicative only, and per-statement plan evidence lives beside each run's results "
                + "under its `plans/` directory.\n\n"
        );

        builder.Append("## Runs\n\n");
        builder.Append("| Provider | Kind | Run id | Fixture | Subject commit | Machine | Directory |\n");
        builder.Append("| --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (PerfEvaluatedRun run in evaluation.Runs)
        {
            builder.Append(
                $"| {Cell(run.Provider)} | {Cell(run.Kind)} | {Cell(run.RunId)} | {Cell(run.FixtureId)} "
                    + $"| {Cell(run.SubjectCommit[..12])} | {Cell(run.MachineFingerprint)} "
                    + $"| {Cell(run.RunDirectory)} |\n"
            );
        }

        builder.Append("\n## Gate outcomes\n\n");
        builder.Append("| Gate | Provider | Status |\n");
        builder.Append("| --- | --- | --- |\n");
        foreach (PerfGateOutcome gate in evaluation.Gates)
        {
            builder.Append($"| {Cell(gate.GateId)} | {Cell(gate.Provider)} | {StatusText(gate.Status)} |\n");
        }

        builder.Append("\n## Gate details\n");
        foreach (PerfGateOutcome gate in evaluation.Gates)
        {
            builder.Append($"\n### {gate.GateId} ({gate.Provider}) — {StatusText(gate.Status)}\n\n");
            builder.Append(gate.Description);
            builder.Append("\n\n");
            foreach (string detail in gate.Details)
            {
                builder.Append($"- {detail}\n");
            }

            if (gate.EvidenceRows.Count > 0)
            {
                builder.Append($"\nEvidence rows: {string.Join(", ", gate.EvidenceRows)}\n");
            }
        }

        return builder.ToString();
    }

    private static string StatusText(PerfGateStatus status) =>
        status switch
        {
            PerfGateStatus.Pass => "PASS",
            PerfGateStatus.Fail => "FAIL",
            PerfGateStatus.Inconclusive => "INCONCLUSIVE",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
