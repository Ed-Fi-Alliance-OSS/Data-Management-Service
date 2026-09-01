// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

[TestFixture]
public class Given_A_Valid_DocumentCache_Operator_Metrics_File
{
    private string _json = null!;

    [SetUp]
    public void Setup()
    {
        _json = PerfArtifactJson.Serialize(
            DocumentCacheOperatorMetricsEvidence.CreateSample(
                PerfProviders.ArtifactName(PerfProvider.Postgresql),
                PerfProviders.ArtifactName(PerfProvider.Mssql)
            )
        );
    }

    [Test]
    public void It_validates_for_each_provider()
    {
        DocumentCacheOperatorMetricsEvidence
            .ValidateJson(_json, PerfProviders.ArtifactName(PerfProvider.Postgresql))
            .Should()
            .BeEmpty();
        DocumentCacheOperatorMetricsEvidence
            .ValidateJson(_json, PerfProviders.ArtifactName(PerfProvider.Mssql))
            .Should()
            .BeEmpty();
    }

    [Test]
    public void It_serializes_with_lower_camel_property_names()
    {
        _json.Should().Contain("\"averageDatabaseCpuPercent\"");
        _json.Should().Contain("\"providerMetrics\"");
        _json.Should().NotContain("\"AverageDatabaseCpuPercent\"");
    }
}

[TestFixture]
public class Given_A_Malformed_DocumentCache_Operator_Metrics_File
{
    [Test]
    public void It_rejects_unexpected_properties()
    {
        JsonObject root = JsonNode
            .Parse(
                PerfArtifactJson.Serialize(
                    DocumentCacheOperatorMetricsEvidence.CreateSample(
                        PerfProviders.ArtifactName(PerfProvider.Postgresql)
                    )
                )
            )!
            .AsObject();
        root["extra"] = "not allowed";

        DocumentCacheOperatorMetricsEvidence
            .ValidateJson(root.ToJsonString(), PerfProviders.ArtifactName(PerfProvider.Postgresql))
            .Should()
            .Contain(failure => failure.Contains("unexpected property 'extra'", StringComparison.Ordinal));
    }

    [Test]
    public void It_rejects_missing_provider_rows()
    {
        string json = PerfArtifactJson.Serialize(
            DocumentCacheOperatorMetricsEvidence.CreateSample(
                PerfProviders.ArtifactName(PerfProvider.Postgresql)
            )
        );

        DocumentCacheOperatorMetricsEvidence
            .ValidateJson(json, PerfProviders.ArtifactName(PerfProvider.Mssql))
            .Should()
            .Contain(failure =>
                failure.Contains("providerMetrics must include provider 'mssql'", StringComparison.Ordinal)
            );
    }

    [Test]
    public void It_rejects_out_of_range_percent_values()
    {
        JsonObject root = JsonNode
            .Parse(
                PerfArtifactJson.Serialize(
                    DocumentCacheOperatorMetricsEvidence.CreateSample(
                        PerfProviders.ArtifactName(PerfProvider.Postgresql)
                    )
                )
            )!
            .AsObject();
        JsonObject providerMetrics = root["providerMetrics"]!.AsArray()[0]!.AsObject();
        providerMetrics["averageDatabaseCpuPercent"] = 101;

        DocumentCacheOperatorMetricsEvidence
            .ValidateJson(root.ToJsonString(), PerfProviders.ArtifactName(PerfProvider.Postgresql))
            .Should()
            .Contain(failure => failure.Contains("between 0 and 100", StringComparison.Ordinal));
    }
}
