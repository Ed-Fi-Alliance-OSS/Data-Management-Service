// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Validates the representative DocumentCache qualification result directory. This is stricter
/// than the general page-query artifact validator because DMS-1317 evidence is threshold-based:
/// every provider threshold must have exactly one measured row and a resolvable evidence file.
/// </summary>
public static class DocumentCacheQualificationArtifactValidator
{
    private const string ThresholdResultsPath = "threshold-results.json";

    private static readonly IReadOnlyList<string> _requiredThresholdProperties =
    [
        "provider",
        "thresholdId",
        "area",
        "measurement",
        "measuredValue",
        "maximum",
        "unit",
        "passed",
        "evidencePath",
        "reviewerNote",
    ];

    private static readonly IReadOnlySet<string> _ticketRequiredOnFailureAreas = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "restartFromBeginning",
        "databaseCpu",
        "databaseLog",
        "queueDmlAmplification",
    };

    public static void EnsureValidDirectory(string resultsDirectory)
    {
        IReadOnlyList<DocumentCacheQualificationValidationFailure> failures = ValidateDirectory(
            resultsDirectory
        );
        if (failures.Count > 0)
        {
            throw new PerfArtifactValidationException([.. failures.Select(failure => failure.ToString())]);
        }
    }

    public static IReadOnlyList<DocumentCacheQualificationValidationFailure> ValidateDirectory(
        string resultsDirectory
    )
    {
        List<DocumentCacheQualificationValidationFailure> failures = [];

        if (string.IsNullOrWhiteSpace(resultsDirectory))
        {
            failures.Add(new("resultDirectory.required", "Result directory is required."));
            return failures;
        }

        string root = Path.GetFullPath(resultsDirectory);
        if (!Directory.Exists(root))
        {
            failures.Add(new("resultDirectory.missing", "Result directory does not exist.", root));
            return failures;
        }

        ValidateRequiredArtifacts(root, failures);
        ValidateThresholdResults(root, failures);

        return failures;
    }

    private static void ValidateRequiredArtifacts(
        string root,
        List<DocumentCacheQualificationValidationFailure> failures
    )
    {
        foreach (
            DocumentCacheQualificationArtifact artifact in DocumentCacheQualificationArtifact.RequiredRepresentativeArtifacts()
        )
        {
            string fullPath = Path.Combine(
                root,
                artifact.RelativePath.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)
            );
            bool exists = artifact.IsDirectory ? Directory.Exists(fullPath) : File.Exists(fullPath);
            if (!exists)
            {
                string kind = artifact.IsDirectory ? "directory" : "file";
                failures.Add(
                    new(
                        "artifact.missing",
                        $"Required {kind} is missing: {artifact.Description}",
                        artifact.RelativePath
                    )
                );
            }
        }
    }

    private static void ValidateThresholdResults(
        string root,
        List<DocumentCacheQualificationValidationFailure> failures
    )
    {
        string thresholdResultsFile = Path.Combine(root, ThresholdResultsPath);
        if (!File.Exists(thresholdResultsFile))
        {
            return;
        }

        string json = File.ReadAllText(thresholdResultsFile);
        ValidateRequiredJsonProperties(json, failures);

        List<DocumentCacheQualificationResult?> results;
        try
        {
            results = PerfArtifactJson.Deserialize<List<DocumentCacheQualificationResult?>>(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            failures.Add(
                new(
                    "thresholdResults.invalidJson",
                    $"Unable to deserialize threshold results: {ex.Message}",
                    ThresholdResultsPath
                )
            );
            return;
        }

        ValidateThresholdRows(root, results, failures);
    }

    private static void ValidateRequiredJsonProperties(
        string json,
        List<DocumentCacheQualificationValidationFailure> failures
    )
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            failures.Add(
                new("thresholdResults.invalidJson", $"Invalid JSON: {ex.Message}", ThresholdResultsPath)
            );
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                failures.Add(
                    new(
                        "thresholdResults.shape",
                        "threshold-results.json root must be an array.",
                        ThresholdResultsPath
                    )
                );
                return;
            }

            int index = 0;
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                string rowPath = $"{ThresholdResultsPath}[{index}]";
                if (element.ValueKind != JsonValueKind.Object)
                {
                    failures.Add(new("thresholdRow.shape", "Threshold row must be a JSON object.", rowPath));
                    index++;
                    continue;
                }

                foreach (string property in _requiredThresholdProperties)
                {
                    if (!element.TryGetProperty(property, out JsonElement value))
                    {
                        failures.Add(
                            new(
                                "thresholdRow.propertyMissing",
                                $"Required property '{property}' is missing.",
                                rowPath
                            )
                        );
                    }
                    else if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    {
                        failures.Add(
                            new(
                                "thresholdRow.propertyNull",
                                $"Required property '{property}' is null.",
                                rowPath
                            )
                        );
                    }
                }

                index++;
            }
        }
    }

    private static void ValidateThresholdRows(
        string root,
        IReadOnlyList<DocumentCacheQualificationResult?> results,
        List<DocumentCacheQualificationValidationFailure> failures
    )
    {
        Dictionary<string, DocumentCacheQualificationThreshold> catalog =
            DocumentCacheQualification.Thresholds.ToDictionary(
                threshold => threshold.Id,
                StringComparer.Ordinal
            );

        foreach (
            var duplicate in results
                .Where(row => row?.ThresholdId is not null)
                .GroupBy(row => row!.ThresholdId)
                .Where(group => group.Count() > 1)
        )
        {
            failures.Add(
                new(
                    "thresholdRow.duplicate",
                    $"Threshold id appears {duplicate.Count()} times.",
                    ThresholdId: duplicate.Key ?? "(missing)"
                )
            );
        }

        foreach (DocumentCacheQualificationThreshold threshold in DocumentCacheQualification.Thresholds)
        {
            if (!results.Any(row => row?.ThresholdId == threshold.Id))
            {
                failures.Add(
                    new(
                        "thresholdRow.missing",
                        "Required threshold row is missing.",
                        ThresholdId: threshold.Id
                    )
                );
            }
        }

        for (int index = 0; index < results.Count; index++)
        {
            DocumentCacheQualificationResult? row = results[index];
            if (row is null)
            {
                failures.Add(
                    new(
                        "thresholdRow.required",
                        "Threshold row is required.",
                        $"{ThresholdResultsPath}[{index}]"
                    )
                );
                continue;
            }

            ValidateThresholdRow(root, row, index, catalog, failures);
        }
    }

    private static void ValidateThresholdRow(
        string root,
        DocumentCacheQualificationResult row,
        int index,
        IReadOnlyDictionary<string, DocumentCacheQualificationThreshold> catalog,
        List<DocumentCacheQualificationValidationFailure> failures
    )
    {
        string rowPath = $"{ThresholdResultsPath}[{index}]";
        ValidateRequiredValues(row, rowPath, failures);
        ValidateCanonicalProvider(row.Provider, rowPath, failures);

        if (string.IsNullOrWhiteSpace(row.ThresholdId))
        {
            return;
        }

        if (!catalog.TryGetValue(row.ThresholdId, out DocumentCacheQualificationThreshold? threshold))
        {
            failures.Add(
                new(
                    "thresholdRow.unknown",
                    "Threshold id is not defined in DocumentCacheQualification.Thresholds.",
                    rowPath,
                    row.ThresholdId
                )
            );
            return;
        }

        string expectedProvider = PerfProviders.ArtifactName(threshold.Provider);
        if (row.Provider != expectedProvider)
        {
            failures.Add(
                new(
                    "thresholdRow.providerMismatch",
                    $"Provider must be '{expectedProvider}'.",
                    rowPath,
                    row.ThresholdId
                )
            );
        }

        AddMismatch(row.Area, threshold.Area, "area", rowPath, row.ThresholdId, failures);
        AddMismatch(
            row.Measurement,
            threshold.Measurement,
            "measurement",
            rowPath,
            row.ThresholdId,
            failures
        );
        AddMismatch(row.Unit, threshold.Unit, "unit", rowPath, row.ThresholdId, failures);

        if (row.Maximum != threshold.Maximum)
        {
            failures.Add(
                new(
                    "thresholdRow.maximumMismatch",
                    $"Maximum must be {threshold.Maximum}.",
                    rowPath,
                    row.ThresholdId
                )
            );
        }

        ValidateEvidencePath(root, row.EvidencePath, rowPath, row.ThresholdId, failures);
        ValidateOperatorMetricsEvidencePath(root, row, threshold, rowPath, failures);
        ValidateDurableBaselineCursorTicket(row, threshold, rowPath, failures);
    }

    private static void ValidateRequiredValues(
        DocumentCacheQualificationResult row,
        string rowPath,
        List<DocumentCacheQualificationValidationFailure> failures
    )
    {
        AddRequiredString(row.Provider, "provider", rowPath, failures);
        AddRequiredString(row.ThresholdId, "thresholdId", rowPath, failures);
        AddRequiredString(row.Area, "area", rowPath, failures);
        AddRequiredString(row.Measurement, "measurement", rowPath, failures);
        AddRequiredString(row.Unit, "unit", rowPath, failures);
        AddRequiredString(row.EvidencePath, "evidencePath", rowPath, failures);
        AddRequiredString(row.ReviewerNote, "reviewerNote", rowPath, failures);

        if (row.MeasuredValue is null)
        {
            failures.Add(new("thresholdRow.valueMissing", "measuredValue is required.", rowPath));
        }

        if (row.Maximum is null)
        {
            failures.Add(new("thresholdRow.valueMissing", "maximum is required.", rowPath));
        }

        if (row.Passed is null)
        {
            failures.Add(new("thresholdRow.valueMissing", "passed is required.", rowPath));
        }
    }

    private static void AddRequiredString(
        string? value,
        string propertyName,
        string rowPath,
        List<DocumentCacheQualificationValidationFailure> failures
    )
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add(new("thresholdRow.valueMissing", $"{propertyName} is required.", rowPath));
        }
    }

    private static void ValidateCanonicalProvider(
        string? provider,
        string rowPath,
        List<DocumentCacheQualificationValidationFailure> failures
    )
    {
        string[] canonicalProviders =
        [
            PerfProviders.ArtifactName(PerfProvider.Postgresql),
            PerfProviders.ArtifactName(PerfProvider.Mssql),
        ];
        if (!string.IsNullOrWhiteSpace(provider) && !canonicalProviders.Contains(provider))
        {
            failures.Add(new("thresholdRow.providerUnknown", $"Unknown provider '{provider}'.", rowPath));
        }
    }

    private static void AddMismatch(
        string? actual,
        string expected,
        string propertyName,
        string rowPath,
        string thresholdId,
        List<DocumentCacheQualificationValidationFailure> failures
    )
    {
        if (actual is not null && actual != expected)
        {
            failures.Add(
                new(
                    $"thresholdRow.{propertyName}Mismatch",
                    $"{propertyName} must be '{expected}'.",
                    rowPath,
                    thresholdId
                )
            );
        }
    }

    private static void ValidateEvidencePath(
        string root,
        string? evidencePath,
        string rowPath,
        string thresholdId,
        List<DocumentCacheQualificationValidationFailure> failures
    )
    {
        if (string.IsNullOrWhiteSpace(evidencePath))
        {
            return;
        }

        if (IsRootedPath(evidencePath))
        {
            failures.Add(
                new(
                    "thresholdRow.evidencePathRooted",
                    "Evidence path must be relative.",
                    rowPath,
                    thresholdId
                )
            );
            return;
        }

        if (HasParentTraversal(evidencePath))
        {
            failures.Add(
                new(
                    "thresholdRow.evidencePathTraversal",
                    "Evidence path must not contain parent-directory traversal.",
                    rowPath,
                    thresholdId
                )
            );
            return;
        }

        string fullPath = Path.GetFullPath(
            Path.Combine(root, evidencePath.Replace('/', Path.DirectorySeparatorChar))
        );
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            failures.Add(
                new(
                    "thresholdRow.evidencePathTraversal",
                    "Evidence path must resolve inside the result directory.",
                    rowPath,
                    thresholdId
                )
            );
            return;
        }

        if (!File.Exists(fullPath))
        {
            failures.Add(
                new("thresholdRow.evidencePathMissing", "Evidence file does not exist.", rowPath, thresholdId)
            );
        }
    }

    private static void ValidateOperatorMetricsEvidencePath(
        string root,
        DocumentCacheQualificationResult row,
        DocumentCacheQualificationThreshold threshold,
        string rowPath,
        List<DocumentCacheQualificationValidationFailure> failures
    )
    {
        if (
            threshold.Area is not ("databaseCpu" or "databaseIo")
            || string.IsNullOrWhiteSpace(row.EvidencePath)
        )
        {
            return;
        }

        if (row.EvidencePath != DocumentCacheOperatorMetricsEvidence.RelativePath)
        {
            failures.Add(
                new(
                    "thresholdRow.operatorMetricsEvidencePath",
                    $"CPU and I/O threshold rows must reference '{DocumentCacheOperatorMetricsEvidence.RelativePath}'.",
                    rowPath,
                    threshold.Id
                )
            );
            return;
        }

        string fullPath = Path.GetFullPath(
            Path.Combine(root, row.EvidencePath.Replace('/', Path.DirectorySeparatorChar))
        );
        foreach (
            string failure in DocumentCacheOperatorMetricsEvidence.ValidateFile(
                fullPath,
                row.Provider ?? string.Empty
            )
        )
        {
            failures.Add(new("thresholdRow.operatorMetricsInvalid", failure, rowPath, threshold.Id));
        }
    }

    private static void ValidateDurableBaselineCursorTicket(
        DocumentCacheQualificationResult row,
        DocumentCacheQualificationThreshold threshold,
        string rowPath,
        List<DocumentCacheQualificationValidationFailure> failures
    )
    {
        if (
            row.Passed == false
            && _ticketRequiredOnFailureAreas.Contains(threshold.Area)
            && string.IsNullOrWhiteSpace(row.DurableBaselineCursorTicket)
        )
        {
            failures.Add(
                new(
                    "thresholdRow.durableBaselineCursorTicketMissing",
                    "Failed interrupted-restart threshold rows require durableBaselineCursorTicket.",
                    rowPath,
                    threshold.Id
                )
            );
        }
    }

    private static bool HasParentTraversal(string path) =>
        Array.Exists(path.Split(['/', '\\'], StringSplitOptions.None), segment => segment == "..");

    private static bool IsRootedPath(string path) =>
        path.StartsWith('/')
        || path.StartsWith('\\')
        || (path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\');

    private static bool IsAsciiLetter(char value) => value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
}
