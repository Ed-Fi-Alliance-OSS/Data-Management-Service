// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Writes a representative DocumentCache qualification directory through a staging folder and
/// validates the finished artifact set before publishing it to the requested path.
/// </summary>
public static class DocumentCacheQualificationArtifactWriter
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteThresholdResults(
        string resultsDirectory,
        IReadOnlyList<DocumentCacheQualificationResult> thresholdResults
    )
    {
        IReadOnlyList<DocumentCacheQualificationValidationFailure> inMemoryFailures =
            DocumentCacheQualificationArtifactValidator.ValidateThresholdResults(
                resultsDirectory,
                [.. thresholdResults]
            );
        if (inMemoryFailures.Count > 0)
        {
            throw new PerfArtifactValidationException([
                .. inMemoryFailures.Select(failure => failure.ToString()),
            ]);
        }

        string target = Path.GetFullPath(resultsDirectory);
        WriteText(target, "threshold-results.json", PerfArtifactJson.Serialize(thresholdResults));

        string writtenJson = File.ReadAllText(Path.Combine(target, "threshold-results.json"));
        IReadOnlyList<DocumentCacheQualificationResult?> reloadedResults = PerfArtifactJson.Deserialize<
            List<DocumentCacheQualificationResult?>
        >(writtenJson);
        IReadOnlyList<DocumentCacheQualificationValidationFailure> diskFailures =
            DocumentCacheQualificationArtifactValidator.ValidateThresholdResults(target, reloadedResults);
        if (diskFailures.Count > 0)
        {
            throw new PerfArtifactValidationException([
                .. diskFailures.Select(failure => failure.ToString()),
            ]);
        }
    }

    public static void Write(
        string resultsDirectory,
        DocumentCacheQualificationRunManifest manifest,
        IReadOnlyList<DocumentCacheQualificationResult> thresholdResults,
        IReadOnlyList<PerfArtifactFile> auxiliaryFiles
    )
    {
        string target = Path.GetFullPath(resultsDirectory);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new PerfArtifactValidationException([
                $"DocumentCache qualification result directory '{target}' is not empty.",
            ]);
        }

        string parent =
            Path.GetDirectoryName(target)
            ?? throw new PerfArtifactValidationException([
                $"DocumentCache qualification result directory '{target}' has no parent directory.",
            ]);
        Directory.CreateDirectory(parent);

        string staging = Path.Combine(parent, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.staging");
        try
        {
            Directory.CreateDirectory(staging);
            WriteText(staging, "run-manifest.json", PerfArtifactJson.Serialize(manifest));
            WriteText(staging, "threshold-results.json", PerfArtifactJson.Serialize(thresholdResults));

            foreach (PerfArtifactFile file in auxiliaryFiles)
            {
                WriteText(staging, file.RelativePath, file.Content);
            }

            DocumentCacheQualificationArtifactValidator.EnsureValidDirectory(staging);

            if (Directory.Exists(target))
            {
                Directory.Delete(target);
            }

            Directory.Move(staging, target);
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            throw;
        }
    }

    private static void WriteText(string root, string relativePath, string content)
    {
        if (IsInvalidRelativePath(relativePath))
        {
            throw new PerfArtifactValidationException([
                $"DocumentCache qualification artifact path '{relativePath}' must be relative and stay inside the run directory.",
            ]);
        }

        string fullPath = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)
        );
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)
                ?? throw new PerfArtifactValidationException([
                    $"DocumentCache qualification artifact path '{relativePath}' has no directory.",
                ])
        );
        File.WriteAllText(fullPath, content, _utf8NoBom);
    }

    private static bool IsInvalidRelativePath(string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath)
        || relativePath.StartsWith('/')
        || relativePath.StartsWith('\\')
        || (
            relativePath.Length >= 3
            && relativePath[0] is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
            && relativePath[1] == ':'
            && relativePath[2] is '/' or '\\'
        )
        || Array.Exists(relativePath.Split(['/', '\\'], StringSplitOptions.None), segment => segment == "..");
}
