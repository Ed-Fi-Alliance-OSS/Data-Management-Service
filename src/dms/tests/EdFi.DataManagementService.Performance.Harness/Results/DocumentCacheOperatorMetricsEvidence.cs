// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Strict operator-supplied CPU/IO evidence for DocumentCache representative qualification.
/// The harness does not fabricate database CPU or I/O utilization from provider DMVs because
/// those values are environment-specific and often host or managed-service metrics.
/// </summary>
public sealed record DocumentCacheOperatorMetricsEvidence(
    string? SchemaVersion,
    string? CapturedAtUtc,
    string? RunWindowStartedAtUtc,
    string? RunWindowEndedAtUtc,
    string? Source,
    IReadOnlyList<DocumentCacheOperatorProviderMetrics>? ProviderMetrics
)
{
    public const string RelativePath = "provider-metrics/operator-cpu-io.json";

    private static readonly IReadOnlySet<string> _topLevelProperties = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "schemaVersion",
        "capturedAtUtc",
        "runWindowStartedAtUtc",
        "runWindowEndedAtUtc",
        "source",
        "providerMetrics",
    };

    private static readonly IReadOnlySet<string> _providerMetricProperties = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "provider",
        "sampleCount",
        "averageDatabaseCpuPercent",
        "averageDatabaseIoUtilizationPercent",
        "reviewerNote",
    };

    public static DocumentCacheOperatorMetricsEvidence LoadFromFile(string path, string expectedProvider)
    {
        string json = File.ReadAllText(path);
        IReadOnlyList<string> failures = ValidateJson(json, expectedProvider);
        if (failures.Count > 0)
        {
            throw new PerfObservationException(
                $"DocumentCache operator CPU/IO metrics file '{path}' is invalid: "
                    + string.Join("; ", failures)
            );
        }

        return PerfArtifactJson.Deserialize<DocumentCacheOperatorMetricsEvidence>(json);
    }

    public static IReadOnlyList<string> ValidateFile(string path, string expectedProvider)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ["operator CPU/IO metrics evidence path is required."];
        }

        if (!File.Exists(path))
        {
            return [$"operator CPU/IO metrics evidence file '{path}' does not exist."];
        }

        return ValidateJson(File.ReadAllText(path), expectedProvider);
    }

    public static IReadOnlyList<string> ValidateJson(string json, string expectedProvider)
    {
        List<string> failures = [];
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return [$"operator CPU/IO metrics JSON is invalid: {ex.Message}"];
        }

        if (root is not JsonObject rootObject)
        {
            return ["operator CPU/IO metrics JSON root must be an object."];
        }

        AddMissingOrUnexpectedProperties(rootObject, _topLevelProperties, "operator metrics root", failures);
        JsonNode? providerMetricsNode = rootObject["providerMetrics"];
        if (providerMetricsNode is JsonArray providerMetricsArray)
        {
            for (int index = 0; index < providerMetricsArray.Count; index++)
            {
                if (providerMetricsArray[index] is JsonObject providerMetricObject)
                {
                    AddMissingOrUnexpectedProperties(
                        providerMetricObject,
                        _providerMetricProperties,
                        $"providerMetrics[{index.ToString(CultureInfo.InvariantCulture)}]",
                        failures
                    );
                }
                else
                {
                    failures.Add(
                        $"providerMetrics[{index.ToString(CultureInfo.InvariantCulture)}] must be an object."
                    );
                }
            }
        }
        else
        {
            failures.Add("providerMetrics must be a non-empty array.");
        }

        DocumentCacheOperatorMetricsEvidence? evidence = null;
        try
        {
            evidence = PerfArtifactJson.Deserialize<DocumentCacheOperatorMetricsEvidence>(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            failures.Add($"operator CPU/IO metrics could not be deserialized: {ex.Message}");
        }

        if (evidence is null)
        {
            return failures;
        }

        AddRequiredString(evidence.SchemaVersion, "schemaVersion", failures);
        if (evidence.SchemaVersion is not null && evidence.SchemaVersion != PerfArtifactSchema.Version)
        {
            failures.Add($"schemaVersion must be '{PerfArtifactSchema.Version}'.");
        }

        AddRequiredInstant(evidence.CapturedAtUtc, "capturedAtUtc", failures);
        AddRequiredInstant(evidence.RunWindowStartedAtUtc, "runWindowStartedAtUtc", failures);
        AddRequiredInstant(evidence.RunWindowEndedAtUtc, "runWindowEndedAtUtc", failures);
        AddRequiredString(evidence.Source, "source", failures);

        if (
            TryParseInstant(evidence.RunWindowStartedAtUtc, out DateTimeOffset startedAt)
            && TryParseInstant(evidence.RunWindowEndedAtUtc, out DateTimeOffset endedAt)
            && endedAt < startedAt
        )
        {
            failures.Add("runWindowEndedAtUtc must be greater than or equal to runWindowStartedAtUtc.");
        }

        IReadOnlyList<DocumentCacheOperatorProviderMetrics> providerMetrics = evidence.ProviderMetrics ?? [];
        if (providerMetrics.Count == 0)
        {
            failures.Add("providerMetrics must contain at least one provider row.");
        }

        foreach (
            var duplicate in providerMetrics
                .Where(metric => !string.IsNullOrWhiteSpace(metric.Provider))
                .GroupBy(metric => metric.Provider, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
        )
        {
            failures.Add($"providerMetrics contains duplicate provider '{duplicate.Key}'.");
        }

        string[] canonicalProviders =
        [
            PerfProviders.ArtifactName(PerfProvider.Postgresql),
            PerfProviders.ArtifactName(PerfProvider.Mssql),
        ];
        foreach (DocumentCacheOperatorProviderMetrics providerMetric in providerMetrics)
        {
            AddRequiredString(providerMetric.Provider, "provider", failures);
            if (
                !string.IsNullOrWhiteSpace(providerMetric.Provider)
                && !canonicalProviders.Contains(providerMetric.Provider)
            )
            {
                failures.Add($"provider '{providerMetric.Provider}' is not supported.");
            }

            AddRequiredPositiveInt(providerMetric.SampleCount, "sampleCount", failures);
            AddRequiredPercent(
                providerMetric.AverageDatabaseCpuPercent,
                "averageDatabaseCpuPercent",
                failures
            );
            AddRequiredPercent(
                providerMetric.AverageDatabaseIoUtilizationPercent,
                "averageDatabaseIoUtilizationPercent",
                failures
            );
            AddRequiredString(providerMetric.ReviewerNote, "reviewerNote", failures);
        }

        if (!providerMetrics.Any(metric => metric.Provider == expectedProvider))
        {
            failures.Add($"providerMetrics must include provider '{expectedProvider}'.");
        }

        return failures;
    }

    public DocumentCacheOperatorProviderMetrics MetricsFor(string provider) =>
        ProviderMetrics?.SingleOrDefault(metric => metric.Provider == provider)
        ?? throw new PerfObservationException(
            $"DocumentCache operator CPU/IO metrics did not include provider '{provider}'."
        );

    public static DocumentCacheOperatorMetricsEvidence CreateSample(params string[] providers) =>
        new(
            PerfArtifactSchema.Version,
            "2026-09-01T00:00:00Z",
            "2026-09-01T00:00:00Z",
            "2026-09-01T00:10:00Z",
            "sample operator metrics for harness validation",
            [
                .. providers.Select(provider => new DocumentCacheOperatorProviderMetrics(
                    provider,
                    SampleCount: 10,
                    AverageDatabaseCpuPercent: 42.5m,
                    AverageDatabaseIoUtilizationPercent: 37.25m,
                    ReviewerNote: "Sample CPU/IO metrics for a smoke or validator fixture."
                )),
            ]
        );

    private static void AddMissingOrUnexpectedProperties(
        JsonObject jsonObject,
        IReadOnlySet<string> expectedProperties,
        string label,
        List<string> failures
    )
    {
        HashSet<string> actualProperties = jsonObject
            .Select(property => property.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string expected in expectedProperties)
        {
            if (!actualProperties.Contains(expected))
            {
                failures.Add($"{label} is missing required property '{expected}'.");
            }
        }

        foreach (string actual in actualProperties)
        {
            if (!expectedProperties.Contains(actual))
            {
                failures.Add($"{label} has unexpected property '{actual}'.");
            }
        }
    }

    private static void AddRequiredString(string? value, string propertyName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{propertyName} is required.");
        }
    }

    private static void AddRequiredInstant(string? value, string propertyName, List<string> failures)
    {
        AddRequiredString(value, propertyName, failures);
        if (value is not null && !TryParseInstant(value, out _))
        {
            failures.Add($"{propertyName} must be an ISO-8601 UTC timestamp.");
        }
    }

    private static void AddRequiredPositiveInt(int? value, string propertyName, List<string> failures)
    {
        if (value is null)
        {
            failures.Add($"{propertyName} is required.");
            return;
        }

        if (value < 1)
        {
            failures.Add($"{propertyName} must be at least 1.");
        }
    }

    private static void AddRequiredPercent(decimal? value, string propertyName, List<string> failures)
    {
        if (value is null)
        {
            failures.Add($"{propertyName} is required.");
            return;
        }

        if (value < 0 || value > 100)
        {
            failures.Add($"{propertyName} must be between 0 and 100.");
        }
    }

    private static bool TryParseInstant(string? value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out instant
        );
}

/// <summary>
/// Provider row inside the strict operator CPU/IO metrics evidence file.
/// </summary>
public sealed record DocumentCacheOperatorProviderMetrics(
    string? Provider,
    int? SampleCount,
    decimal? AverageDatabaseCpuPercent,
    decimal? AverageDatabaseIoUtilizationPercent,
    string? ReviewerNote
);
