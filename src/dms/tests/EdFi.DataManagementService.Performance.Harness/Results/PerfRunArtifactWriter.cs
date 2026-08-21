// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// One auxiliary artifact file: a run-directory-relative path (for example
/// "plans/pg.traditional-offset-zero.25.explain.json") and its full text content.
/// </summary>
public sealed record PerfArtifactFile(string RelativePath, string Content);

/// <summary>
/// Writes one run's artifact directory. Validation runs first: nothing is written when the
/// artifacts are invalid, and every results row's plan file must be among the files being
/// written, so a results.json can never reference plan evidence that does not exist.
/// All files are UTF-8 without BOM.
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

        HashSet<string> auxiliaryPaths = [.. auxiliaryFiles.Select(file => file.RelativePath)];
        List<string> missingPlanFiles =
        [
            .. results
                .Results.Select(result => result.PlanFile)
                .Where(planFile => !auxiliaryPaths.Contains(planFile)),
        ];
        if (missingPlanFiles.Count > 0)
        {
            throw new PerfArtifactValidationException([
                .. missingPlanFiles.Select(planFile => $"plan file '{planFile}' is not being written."),
            ]);
        }

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
