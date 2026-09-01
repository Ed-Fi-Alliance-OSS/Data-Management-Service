// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

internal sealed class DocumentCacheQualificationArtifactSample : IDisposable
{
    public string ResultDirectory { get; } =
        Path.Combine(
            Path.GetTempPath(),
            "document-cache-qualification-validator-tests",
            Guid.NewGuid().ToString("N")
        );

    public List<DocumentCacheQualificationResult> Results { get; private set; } = CreateRows();

    public static DocumentCacheQualificationArtifactSample Create()
    {
        DocumentCacheQualificationArtifactSample sample = new();
        sample.WriteRequiredArtifacts();
        sample.WriteThresholdResults();
        return sample;
    }

    public static List<DocumentCacheQualificationResult> CreateRows() =>
        [
            .. DocumentCacheQualification
                .OrderedThresholds()
                .Select(threshold => new DocumentCacheQualificationResult(
                    PerfProviders.ArtifactName(threshold.Provider),
                    threshold.Id,
                    threshold.Area,
                    threshold.Measurement,
                    threshold.Maximum / 2,
                    threshold.Maximum,
                    threshold.Unit,
                    Passed: true,
                    EvidencePath: EvidencePathFor(threshold),
                    ReviewerNote: $"Measured {threshold.Id} against the representative DocumentCache workload."
                )),
        ];

    public void RewriteResults(IEnumerable<DocumentCacheQualificationResult> rows)
    {
        Results = [.. rows];
        WriteThresholdResults();
    }

    public void RewriteFirstRow(
        Func<DocumentCacheQualificationResult, DocumentCacheQualificationResult> mutate
    )
    {
        RewriteResults(Results.Select((row, index) => index == 0 ? mutate(row) : row));
    }

    public void RewriteRow(
        string thresholdId,
        Func<DocumentCacheQualificationResult, DocumentCacheQualificationResult> mutate
    )
    {
        RewriteResults(Results.Select(row => row.ThresholdId == thresholdId ? mutate(row) : row));
    }

    public void RemoveArtifact(string relativePath)
    {
        string path = FullPath(relativePath.TrimEnd('/'));
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void WriteRawThresholdResults(string json)
    {
        File.WriteAllText(FullPath("threshold-results.json"), json);
    }

    public void Dispose()
    {
        if (Directory.Exists(ResultDirectory))
        {
            Directory.Delete(ResultDirectory, recursive: true);
        }
    }

    private static string EvidencePathFor(DocumentCacheQualificationThreshold threshold) =>
        threshold.Area == "databaseCpu"
            ? DocumentCacheOperatorMetricsEvidence.RelativePath
            : $"phase-metrics/{threshold.Id}.json";

    private void WriteRequiredArtifacts()
    {
        foreach (string artifact in DocumentCacheQualification.RequiredRepresentativeArtifacts)
        {
            if (artifact.EndsWith('/'))
            {
                Directory.CreateDirectory(FullPath(artifact.TrimEnd('/')));
            }
            else if (artifact == DocumentCacheOperatorMetricsEvidence.RelativePath)
            {
                WriteText(
                    artifact,
                    PerfArtifactJson.Serialize(
                        DocumentCacheOperatorMetricsEvidence.CreateSample(
                            PerfProviders.ArtifactName(PerfProvider.Postgresql),
                            PerfProviders.ArtifactName(PerfProvider.Mssql)
                        )
                    )
                );
            }
            else if (artifact != "threshold-results.json")
            {
                WriteText(artifact, $"# {artifact}");
            }
        }

        foreach (DocumentCacheQualificationThreshold threshold in DocumentCacheQualification.Thresholds)
        {
            string evidencePath = EvidencePathFor(threshold);
            if (evidencePath != DocumentCacheOperatorMetricsEvidence.RelativePath)
            {
                WriteText(evidencePath, """{"measured":true}""");
            }
        }
    }

    private void WriteThresholdResults()
    {
        WriteText("threshold-results.json", PerfArtifactJson.Serialize(Results));
    }

    private void WriteText(string relativePath, string content)
    {
        string path = FullPath(relativePath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException($"Artifact path '{relativePath}' has no directory.")
        );
        File.WriteAllText(path, content);
    }

    private string FullPath(string relativePath) =>
        Path.Combine(
            ResultDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)
        );
}

[TestFixture]
public class Given_A_Valid_DocumentCacheQualification_Result_Directory
{
    [Test]
    public void It_reports_no_validation_failures()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .BeEmpty();
    }

    [Test]
    public void It_passes_ensure_valid()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();

        FluentActions
            .Invoking(() =>
                DocumentCacheQualificationArtifactValidator.EnsureValidDirectory(sample.ResultDirectory)
            )
            .Should()
            .NotThrow();
    }

    [Test]
    public void It_serializes_rows_with_lower_camel_property_names()
    {
        string json = PerfArtifactJson.Serialize(DocumentCacheQualificationArtifactSample.CreateRows());

        json.Should().Contain("\"thresholdId\"");
        json.Should().Contain("\"measuredValue\"");
        json.Should().NotContain("\"ThresholdId\"");
    }
}

[TestFixture]
public class Given_A_DocumentCacheQualification_Result_Directory_With_Unsorted_Rows
{
    [Test]
    public void It_rejects_rows_that_are_not_sorted_by_provider_and_threshold_id()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        sample.RewriteResults(
            sample.Results.OrderByDescending(row => row.ThresholdId, StringComparer.Ordinal)
        );

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .Contain(failure => failure.Code == "thresholdRow.order");
    }
}

[TestFixture]
public class Given_A_DocumentCacheQualification_Result_Directory_Missing_Required_Artifacts
{
    [Test]
    public void It_rejects_the_directory()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        sample.RemoveArtifact("qualification-summary.md");
        sample.RemoveArtifact("command-transcripts/");

        IReadOnlyList<DocumentCacheQualificationValidationFailure> failures =
            DocumentCacheQualificationArtifactValidator.ValidateDirectory(sample.ResultDirectory);

        failures.Should().Contain(failure => failure.Code == "artifact.missing");
        failures.Should().Contain(failure => failure.ArtifactPath == "qualification-summary.md");
        failures.Should().Contain(failure => failure.ArtifactPath == "command-transcripts/");
    }
}

[TestFixture]
public class Given_A_DocumentCacheQualification_Result_Directory_With_Duplicate_Rows
{
    [Test]
    public void It_rejects_the_duplicate_threshold_id()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        sample.RewriteResults([.. sample.Results, sample.Results[0]]);

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .Contain(failure => failure.Code == "thresholdRow.duplicate");
    }
}

[TestFixture]
public class Given_A_DocumentCacheQualification_Result_Directory_With_A_Missing_Row
{
    [Test]
    public void It_rejects_the_missing_threshold_id()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        string removedThresholdId = sample.Results[0].ThresholdId!;
        sample.RewriteResults(sample.Results.Skip(1));

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .Contain(failure =>
                failure.Code == "thresholdRow.missing" && failure.ThresholdId == removedThresholdId
            );
    }
}

[TestFixture]
public class Given_A_DocumentCacheQualification_Result_Directory_With_An_Unknown_Row
{
    [Test]
    public void It_rejects_the_unknown_threshold_id()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        sample.RewriteFirstRow(row => row with { ThresholdId = "postgresql-not-in-the-catalog" });

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .Contain(failure => failure.Code == "thresholdRow.unknown");
    }
}

[TestFixture]
public class Given_A_DocumentCacheQualification_Result_Directory_With_Catalog_Mismatches
{
    [Test]
    public void It_rejects_values_that_do_not_match_the_threshold_catalog()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        sample.RewriteFirstRow(row =>
            row with
            {
                Area = "notTheCatalogArea",
                Measurement = "not the catalog measurement",
                Maximum = row.Maximum + 1,
                Unit = "widgets",
            }
        );

        IReadOnlyList<DocumentCacheQualificationValidationFailure> failures =
            DocumentCacheQualificationArtifactValidator.ValidateDirectory(sample.ResultDirectory);

        failures.Should().Contain(failure => failure.Code == "thresholdRow.areaMismatch");
        failures.Should().Contain(failure => failure.Code == "thresholdRow.measurementMismatch");
        failures.Should().Contain(failure => failure.Code == "thresholdRow.maximumMismatch");
        failures.Should().Contain(failure => failure.Code == "thresholdRow.unitMismatch");
    }
}

[TestFixture]
public class Given_A_DocumentCacheQualification_Result_Directory_With_Invalid_Evidence_Paths
{
    [Test]
    public void It_rejects_absolute_evidence_paths()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        sample.RewriteFirstRow(row => row with { EvidencePath = "/tmp/document-cache-evidence.json" });

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .Contain(failure => failure.Code == "thresholdRow.evidencePathRooted");
    }

    [Test]
    public void It_rejects_parent_directory_traversal()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        sample.RewriteFirstRow(row => row with { EvidencePath = "../document-cache-evidence.json" });

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .Contain(failure => failure.Code == "thresholdRow.evidencePathTraversal");
    }

    [Test]
    public void It_rejects_missing_evidence_files()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        sample.RewriteFirstRow(row => row with { EvidencePath = "phase-metrics/not-written.json" });

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .Contain(failure => failure.Code == "thresholdRow.evidencePathMissing");
    }
}

[TestFixture]
public class Given_A_DocumentCacheQualification_Result_Directory_With_Invalid_Operator_Metrics
{
    [Test]
    public void It_rejects_cpu_rows_that_do_not_reference_the_operator_metrics_file()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        string thresholdId = DocumentCacheQualification
            .Thresholds.Single(threshold =>
                threshold.Provider == PerfProvider.Postgresql && threshold.Area == "databaseCpu"
            )
            .Id;
        sample.RewriteRow(
            thresholdId,
            row => row with { EvidencePath = "phase-metrics/postgresql-average-db-cpu-percent.json" }
        );
        File.WriteAllText(
            Path.Combine(sample.ResultDirectory, "phase-metrics", "postgresql-average-db-cpu-percent.json"),
            """{"measured":true}"""
        );

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .Contain(failure => failure.Code == "thresholdRow.operatorMetricsEvidencePath");
    }

    [Test]
    public void It_rejects_a_missing_operator_metrics_file_for_cpu_and_io_rows()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        sample.RemoveArtifact(DocumentCacheOperatorMetricsEvidence.RelativePath);

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .Contain(failure => failure.Code == "thresholdRow.operatorMetricsInvalid");
    }

    [Test]
    public void It_rejects_operator_metrics_without_the_threshold_provider()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        File.WriteAllText(
            Path.Combine(sample.ResultDirectory, DocumentCacheOperatorMetricsEvidence.RelativePath),
            PerfArtifactJson.Serialize(
                DocumentCacheOperatorMetricsEvidence.CreateSample(
                    PerfProviders.ArtifactName(PerfProvider.Postgresql)
                )
            )
        );

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .Contain(failure =>
                failure.Code == "thresholdRow.operatorMetricsInvalid"
                && failure.Message.Contains("provider 'mssql'", StringComparison.Ordinal)
            );
    }
}

[TestFixture]
public class Given_A_DocumentCacheQualification_Result_Directory_With_Missing_Required_Row_Properties
{
    [Test]
    public void It_rejects_rows_that_do_not_use_the_required_lower_camel_contract()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        JsonArray rows = JsonNode.Parse(PerfArtifactJson.Serialize(sample.Results))!.AsArray();
        JsonObject firstRow = rows[0]!.AsObject();
        firstRow.Remove("passed");
        firstRow.Remove("reviewerNote");
        sample.WriteRawThresholdResults(rows.ToJsonString());

        IReadOnlyList<DocumentCacheQualificationValidationFailure> failures =
            DocumentCacheQualificationArtifactValidator.ValidateDirectory(sample.ResultDirectory);

        failures.Should().Contain(failure => failure.Code == "thresholdRow.propertyMissing");
        failures.Should().Contain(failure => failure.Message.Contains("'passed'", StringComparison.Ordinal));
        failures
            .Should()
            .Contain(failure => failure.Message.Contains("'reviewerNote'", StringComparison.Ordinal));
        failures.Should().Contain(failure => failure.Code == "thresholdRow.valueMissing");
    }
}

[TestFixture]
public class Given_A_DocumentCacheQualification_Result_Directory_With_Failed_Interrupted_Restart_Rows
{
    [Test]
    public void It_requires_a_durable_baseline_cursor_ticket_for_restart_from_beginning_failures()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        string thresholdId = DocumentCacheQualification
            .Thresholds.Single(threshold =>
                threshold.Provider == PerfProvider.Postgresql && threshold.Area == "restartFromBeginning"
            )
            .Id;
        sample.RewriteRow(
            thresholdId,
            row =>
                row with
                {
                    MeasuredValue = row.Maximum + 1,
                    Passed = false,
                    DurableBaselineCursorTicket = null,
                }
        );

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .Contain(failure => failure.Code == "thresholdRow.durableBaselineCursorTicketMissing");
    }

    [Test]
    public void It_requires_the_ticket_for_related_database_log_failures()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        string thresholdId = DocumentCacheQualification
            .Thresholds.Single(threshold =>
                threshold.Provider == PerfProvider.Mssql && threshold.Area == "databaseLog"
            )
            .Id;
        sample.RewriteRow(
            thresholdId,
            row =>
                row with
                {
                    MeasuredValue = row.Maximum + 1,
                    Passed = false,
                    ReviewerNote = "Interrupted rebuild restart log pressure exceeded the threshold.",
                    DurableBaselineCursorTicket = null,
                }
        );

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .Contain(failure => failure.Code == "thresholdRow.durableBaselineCursorTicketMissing");
    }

    [Test]
    public void It_accepts_ticketed_failures()
    {
        using DocumentCacheQualificationArtifactSample sample =
            DocumentCacheQualificationArtifactSample.Create();
        string thresholdId = DocumentCacheQualification
            .Thresholds.Single(threshold =>
                threshold.Provider == PerfProvider.Postgresql && threshold.Area == "restartFromBeginning"
            )
            .Id;
        sample.RewriteRow(
            thresholdId,
            row =>
                row with
                {
                    MeasuredValue = row.Maximum + 1,
                    Passed = false,
                    DurableBaselineCursorTicket = "DMS-9999",
                }
        );

        DocumentCacheQualificationArtifactValidator
            .ValidateDirectory(sample.ResultDirectory)
            .Should()
            .BeEmpty();
    }
}
