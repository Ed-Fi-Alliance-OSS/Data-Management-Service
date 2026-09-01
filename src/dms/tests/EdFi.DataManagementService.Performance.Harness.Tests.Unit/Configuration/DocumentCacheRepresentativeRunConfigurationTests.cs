// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Configuration;

[TestFixture]
public class Given_A_Valid_DocumentCacheRepresentativeRun_Configuration
{
    private DocumentCacheRepresentativeRunConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        _configuration = DocumentCacheRepresentativeRunConfigurationLoader.Load(
            PerfProvider.Postgresql,
            EvidenceSettingsTestValues.ReaderFor(ValidValues())
        );
    }

    [Test]
    public void It_uses_the_fixture_provider_and_representative_defaults()
    {
        _configuration.Provider.Should().Be(PerfProvider.Postgresql);
        _configuration.Fixture.Should().Be(PerfFixtureKind.Primary500k);
        _configuration.HighWaterMark.Should().Be(PerfFixtureKind.Primary500k.RowCount);
        _configuration
            .PageSize.Should()
            .Be(DocumentCacheRepresentativeRunConfigurationLoader.DefaultPageSize);
        _configuration
            .ProjectorConcurrency.Should()
            .Be(DocumentCacheRepresentativeRunConfigurationLoader.DefaultProjectorConcurrency);
        _configuration
            .WarmupStatusSamples.Should()
            .Be(DocumentCacheRepresentativeRunConfigurationLoader.DefaultWarmupStatusSamples);
        _configuration
            .MeasuredStatusSamples.Should()
            .Be(DocumentCacheRepresentativeRunConfigurationLoader.DefaultMeasuredStatusSamples);
        _configuration
            .OutageDistinctDocumentWrites.Should()
            .Be(DocumentCacheQualification.RepresentativeOutageDistinctDocumentWrites);
        _configuration
            .SameDocumentContention.Should()
            .Be(DocumentCacheQualification.RepresentativeSameDocumentContention);
    }

    [Test]
    public void It_reuses_the_evidence_settings_contract()
    {
        _configuration.EvidenceSettings.StorageNote.Should().Be("local docker volume, not tmpfs");
        _configuration.EvidenceSettings.ImageDigest.Should().Be(EvidenceSettingsTestValues.Digest);
        _configuration.OperatorMetricsFile.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void It_normalizes_the_runner_commit()
    {
        _configuration.RunnerCommit.Should().Be("abcdefabcdefabcdefabcdefabcdefabcdefabcd");
    }

    private static Dictionary<string, string?> ValidValues()
    {
        Dictionary<string, string?> values = EvidenceSettingsTestValues.Valid();
        values[PerfEnvironmentVariables.ResultsDirectory] = Path.Combine(
            Path.GetTempPath(),
            "document-cache-results"
        );
        values[PerfEnvironmentVariables.RunnerCommit] = "ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD";
        values[PerfEnvironmentVariables.DocumentCacheOperatorMetricsFile] =
            DocumentCacheRepresentativeRunTestValues.OperatorMetricsFile();
        return values;
    }
}

[TestFixture]
public class Given_DocumentCacheRepresentativeRun_Provider_Guards
{
    [Test]
    public void It_accepts_a_matching_optional_provider()
    {
        Dictionary<string, string?> values = ValidValues();
        values[PerfEnvironmentVariables.DocumentCacheProvider] = "mssql";

        DocumentCacheRepresentativeRunConfiguration configuration =
            DocumentCacheRepresentativeRunConfigurationLoader.Load(
                PerfProvider.Mssql,
                EvidenceSettingsTestValues.ReaderFor(values)
            );

        configuration.Provider.Should().Be(PerfProvider.Mssql);
    }

    [Test]
    public void It_rejects_a_mismatched_optional_provider()
    {
        Dictionary<string, string?> values = ValidValues();
        values[PerfEnvironmentVariables.DocumentCacheProvider] = "postgresql";

        PerfConfigurationException exception = Assert.Throws<PerfConfigurationException>(() =>
            DocumentCacheRepresentativeRunConfigurationLoader.Load(
                PerfProvider.Mssql,
                EvidenceSettingsTestValues.ReaderFor(values)
            )
        );

        exception.Errors.Should().Contain(error => error.Contains("this fixture is for mssql"));
    }

    private static Dictionary<string, string?> ValidValues()
    {
        Dictionary<string, string?> values = EvidenceSettingsTestValues.Valid();
        values[PerfEnvironmentVariables.ResultsDirectory] = Path.Combine(
            Path.GetTempPath(),
            "document-cache-results"
        );
        values[PerfEnvironmentVariables.RunnerCommit] = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
        values[PerfEnvironmentVariables.DocumentCacheOperatorMetricsFile] =
            DocumentCacheRepresentativeRunTestValues.OperatorMetricsFile();
        return values;
    }
}

[TestFixture]
public class Given_DocumentCacheRepresentativeRun_Environment_Overrides
{
    [Test]
    public void It_parses_the_document_cache_specific_knobs()
    {
        Dictionary<string, string?> values = ValidValues();
        values[PerfEnvironmentVariables.Fixture] = PerfFixtureKind.Smoke10k.Id;
        values[PerfEnvironmentVariables.DocumentCachePageSize] = "500";
        values[PerfEnvironmentVariables.DocumentCacheHighWaterMark] = "9000";
        values[PerfEnvironmentVariables.DocumentCacheProjectorConcurrency] = "8";
        values[PerfEnvironmentVariables.DocumentCacheWarmupStatusSamples] = "2";
        values[PerfEnvironmentVariables.DocumentCacheMeasuredStatusSamples] = "12";
        values[PerfEnvironmentVariables.DocumentCacheOutageWrites] = "750";
        values[PerfEnvironmentVariables.DocumentCacheSameDocumentContenders] = "16";
        values[PerfEnvironmentVariables.OperatorNote] = "release validation host";

        DocumentCacheRepresentativeRunConfiguration configuration =
            DocumentCacheRepresentativeRunConfigurationLoader.Load(
                PerfProvider.Postgresql,
                EvidenceSettingsTestValues.ReaderFor(values)
            );

        configuration.Fixture.Should().Be(PerfFixtureKind.Smoke10k);
        configuration.PageSize.Should().Be(500);
        configuration.HighWaterMark.Should().Be(9000);
        configuration.ProjectorConcurrency.Should().Be(8);
        configuration.WarmupStatusSamples.Should().Be(2);
        configuration.MeasuredStatusSamples.Should().Be(12);
        configuration.OutageDistinctDocumentWrites.Should().Be(750);
        configuration.SameDocumentContention.Should().Be(16);
        configuration.OperatorNote.Should().Be("release validation host");
    }

    [Test]
    public void It_rejects_values_beyond_the_configured_fixture()
    {
        Dictionary<string, string?> values = ValidValues();
        values[PerfEnvironmentVariables.Fixture] = PerfFixtureKind.Smoke10k.Id;
        values[PerfEnvironmentVariables.DocumentCacheHighWaterMark] = "10001";
        values[PerfEnvironmentVariables.DocumentCacheOutageWrites] = "50000";

        PerfConfigurationException exception = Assert.Throws<PerfConfigurationException>(() =>
            DocumentCacheRepresentativeRunConfigurationLoader.Load(
                PerfProvider.Postgresql,
                EvidenceSettingsTestValues.ReaderFor(values)
            )
        );

        exception.Errors.Should().Contain(error => error.Contains("HIGH_WATER_MARK"));
        exception.Errors.Should().Contain(error => error.Contains("OUTAGE_WRITES"));
    }

    [Test]
    public void It_rejects_a_missing_operator_metrics_file()
    {
        Dictionary<string, string?> values = ValidValues();
        values[PerfEnvironmentVariables.DocumentCacheOperatorMetricsFile] = Path.Combine(
            Path.GetTempPath(),
            "missing-document-cache-operator-metrics.json"
        );

        PerfConfigurationException exception = Assert.Throws<PerfConfigurationException>(() =>
            DocumentCacheRepresentativeRunConfigurationLoader.Load(
                PerfProvider.Postgresql,
                EvidenceSettingsTestValues.ReaderFor(values)
            )
        );

        exception.Errors.Should().Contain(error => error.Contains("file does not exist"));
    }

    [Test]
    public void It_rejects_operator_metrics_without_the_fixture_provider()
    {
        Dictionary<string, string?> values = ValidValues();
        values[PerfEnvironmentVariables.DocumentCacheOperatorMetricsFile] =
            DocumentCacheRepresentativeRunTestValues.OperatorMetricsFile(
                PerfProviders.ArtifactName(PerfProvider.Mssql)
            );

        PerfConfigurationException exception = Assert.Throws<PerfConfigurationException>(() =>
            DocumentCacheRepresentativeRunConfigurationLoader.Load(
                PerfProvider.Postgresql,
                EvidenceSettingsTestValues.ReaderFor(values)
            )
        );

        exception
            .Errors.Should()
            .Contain(error => error.Contains("providerMetrics must include provider 'postgresql'"));
    }

    private static Dictionary<string, string?> ValidValues()
    {
        Dictionary<string, string?> values = EvidenceSettingsTestValues.Valid();
        values[PerfEnvironmentVariables.ResultsDirectory] = Path.Combine(
            Path.GetTempPath(),
            "document-cache-results"
        );
        values[PerfEnvironmentVariables.RunnerCommit] = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
        values[PerfEnvironmentVariables.DocumentCacheOperatorMetricsFile] =
            DocumentCacheRepresentativeRunTestValues.OperatorMetricsFile();
        return values;
    }
}

[TestFixture]
public class Given_Missing_DocumentCacheRepresentativeRun_Configuration
{
    [Test]
    public void It_reports_configuration_and_evidence_errors_together()
    {
        PerfConfigurationException exception = Assert.Throws<PerfConfigurationException>(() =>
            DocumentCacheRepresentativeRunConfigurationLoader.Load(PerfProvider.Postgresql, _ => null)
        );

        exception.Errors.Should().Contain($"{PerfEnvironmentVariables.ResultsDirectory} is required.");
        exception.Errors.Should().Contain($"{PerfEnvironmentVariables.RunnerCommit} is required.");
        exception.Errors.Should().Contain($"{PerfEnvironmentVariables.ImageTag} is required.");
        exception.Errors.Should().Contain($"{PerfEnvironmentVariables.ImageDigest} is required.");
        exception.Errors.Should().Contain($"{PerfEnvironmentVariables.StorageNote} is required.");
        exception
            .Errors.Should()
            .Contain($"{PerfEnvironmentVariables.DocumentCacheOperatorMetricsFile} is required.");
    }
}

internal static class DocumentCacheRepresentativeRunTestValues
{
    public static string OperatorMetricsFile(params string[] providers)
    {
        string[] selectedProviders =
            providers.Length == 0
                ?
                [
                    PerfProviders.ArtifactName(PerfProvider.Postgresql),
                    PerfProviders.ArtifactName(PerfProvider.Mssql),
                ]
                : providers;
        string path = Path.Combine(
            Path.GetTempPath(),
            "document-cache-operator-metrics",
            Guid.NewGuid().ToString("N") + ".json"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            PerfArtifactJson.Serialize(DocumentCacheOperatorMetricsEvidence.CreateSample(selectedProviders))
        );
        return path;
    }
}
