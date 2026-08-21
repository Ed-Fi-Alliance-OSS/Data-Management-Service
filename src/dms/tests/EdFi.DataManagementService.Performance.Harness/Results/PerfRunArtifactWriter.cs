// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// One auxiliary artifact file: a run-directory-relative path (for example
/// "plans/postgresql.traditional-offset-zero.25.explain.json") and its full text content.
/// </summary>
public sealed record PerfArtifactFile(string RelativePath, string Content);

/// <summary>
/// Writes one run's artifact directory. Validation runs first: nothing is written when the
/// artifacts are invalid. Every results row's plan file must be among the files being
/// written and be named for the row's provider/scenario/page-size cell, and when it is a
/// .plans.json index, every per-statement plan file and the statistics file it names must
/// be among the written files too — so a results.json can never reference plan evidence
/// that does not exist. All files are UTF-8 without BOM.
/// </summary>
public static class PerfRunArtifactWriter
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(
        string resultsDirectory,
        PerfRunManifest manifest,
        PerfResultsDocument results,
        PerfFixtureManifest fixtureManifest,
        IReadOnlyList<PerfArtifactFile> auxiliaryFiles
    )
    {
        PerfArtifactValidator.EnsureValid(manifest, results);
        EnsurePlanEvidencePresent(results, auxiliaryFiles);

        Directory.CreateDirectory(resultsDirectory);
        WriteText(resultsDirectory, "run-manifest.json", PerfArtifactJson.Serialize(manifest));
        WriteText(resultsDirectory, "results.json", PerfArtifactJson.Serialize(results));
        WriteText(resultsDirectory, "results.csv", PerfResultsCsvWriter.Write(results.Results));
        WriteText(resultsDirectory, "fixture-manifest.json", PerfArtifactJson.Serialize(fixtureManifest));

        foreach (PerfArtifactFile file in auxiliaryFiles)
        {
            WriteText(resultsDirectory, file.RelativePath, file.Content);
        }
    }

    private static void EnsurePlanEvidencePresent(
        PerfResultsDocument results,
        IReadOnlyList<PerfArtifactFile> auxiliaryFiles
    )
    {
        Dictionary<string, string> contentByPath = auxiliaryFiles.ToDictionary(
            file => file.RelativePath,
            file => file.Content
        );

        List<string> errors = [];
        foreach (PerfScenarioResult row in results.Results)
        {
            string expectedPrefix = $"plans/{row.Provider}.{row.ScenarioId}.{row.PageSize}.";
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
                foreach (string referent in PlanIndexReferents(row.PlanFile, planContent, errors))
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

    /// <summary>
    /// The plan file paths and statistics file a .plans.json index names. A malformed index
    /// is reported through <paramref name="errors" /> rather than thrown, so every problem
    /// across the run is listed at once.
    /// </summary>
    private static IReadOnlyList<string> PlanIndexReferents(
        string indexPath,
        string indexJson,
        List<string> errors
    )
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(indexJson);
        }
        catch (System.Text.Json.JsonException)
        {
            errors.Add($"plan index '{indexPath}' is not valid JSON.");
            return [];
        }

        List<string> referents = [];
        if (root?["planFiles"] is not JsonArray planFiles || planFiles.Count == 0)
        {
            errors.Add($"plan index '{indexPath}' carries no planFiles entries.");
        }
        else
        {
            foreach (JsonNode? entry in planFiles)
            {
                if (entry is JsonValue value && value.TryGetValue(out string? path))
                {
                    referents.Add(path);
                }
                else
                {
                    errors.Add($"plan index '{indexPath}' carries a non-string planFiles entry.");
                }
            }
        }

        if (
            root?["statisticsFile"] is JsonValue statistics
            && statistics.TryGetValue(out string? statisticsPath)
        )
        {
            referents.Add(statisticsPath);
        }
        else
        {
            errors.Add($"plan index '{indexPath}' carries no statisticsFile.");
        }

        return referents;
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
