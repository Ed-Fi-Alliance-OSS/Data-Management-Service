// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Writes one final-gate run's artifact directory. Validation runs first: nothing is written
/// when the artifacts are invalid. Every results row's plan file must be among the files
/// being written and be named for the row's provider/scenario/cell-key, and a .plans.json
/// index's per-statement plan files and statistics file must be among the written files too —
/// so a results.json can never reference plan evidence that does not exist. The fixture
/// manifest arrives pre-serialized because the two run kinds carry different manifest shapes.
/// All files are UTF-8 without BOM.
/// </summary>
public static class PerfFinalGateArtifactWriter
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(
        string resultsDirectory,
        PerfFinalGateRunManifest manifest,
        PerfFinalGateResultsDocument results,
        string fixtureManifestJson,
        IReadOnlyList<PerfArtifactFile> auxiliaryFiles
    )
    {
        PerfFinalGateArtifactValidator.EnsureValid(manifest, results);
        EnsurePlanEvidencePresent(results, auxiliaryFiles);

        Directory.CreateDirectory(resultsDirectory);
        WriteText(resultsDirectory, "run-manifest.json", PerfArtifactJson.Serialize(manifest));
        WriteText(resultsDirectory, "results.json", PerfArtifactJson.Serialize(results));
        WriteText(resultsDirectory, "results.csv", PerfFinalGateResultsCsvWriter.Write(results.Results));
        WriteText(resultsDirectory, "fixture-manifest.json", fixtureManifestJson);

        foreach (PerfArtifactFile file in auxiliaryFiles)
        {
            WriteText(resultsDirectory, file.RelativePath, file.Content);
        }
    }

    private static void EnsurePlanEvidencePresent(
        PerfFinalGateResultsDocument results,
        IReadOnlyList<PerfArtifactFile> auxiliaryFiles
    )
    {
        Dictionary<string, string> contentByPath = auxiliaryFiles.ToDictionary(
            file => file.RelativePath,
            file => file.Content
        );

        List<string> errors = [];
        foreach (PerfFinalGateScenarioResult row in results.Results)
        {
            string expectedPrefix = $"plans/{row.Provider}.{row.ScenarioId}.{row.CellKey}.";
            if (!row.PlanFile.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                errors.Add(
                    $"plan file '{row.PlanFile}' must start with '{expectedPrefix}' for its results row."
                );
            }

            if (!contentByPath.TryGetValue(row.PlanFile, out string? planContent))
            {
                errors.Add($"plan file '{row.PlanFile}' is not being written.");
                continue;
            }

            if (row.PlanFile.EndsWith(".plans.json", StringComparison.Ordinal))
            {
                foreach (
                    string referent in PerfRunArtifactWriter.PlanIndexReferents(
                        row.PlanFile,
                        planContent,
                        errors
                    )
                )
                {
                    if (!contentByPath.ContainsKey(referent))
                    {
                        errors.Add(
                            $"plan index '{row.PlanFile}' references '{referent}', "
                                + "which is not being written."
                        );
                    }
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new PerfArtifactValidationException(errors);
        }
    }

    private static void WriteText(string resultsDirectory, string relativePath, string content)
    {
        string fullPath = Path.Combine(
            resultsDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)
        );
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)
                ?? throw new PerfArtifactValidationException([
                    $"artifact path '{relativePath}' has no directory.",
                ])
        );
        File.WriteAllText(fullPath, content, _utf8NoBom);
    }
}
