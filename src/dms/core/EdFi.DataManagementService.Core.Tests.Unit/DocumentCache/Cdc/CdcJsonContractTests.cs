// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcJsonContract")]
public class Given_CdcJsonContract
{
    private static readonly DateTimeOffset SampleObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);

    private static SampleCdcContract SampleContract =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcReadiness.NotReady,
            SampleObservedAt,
            new CdcTargetIdentity("deployment-a", "default", "7", "instance-a", 3, CdcProvider.SqlServer)
        );

    [Test]
    public void It_serializes_lower_camel_properties_and_lower_camel_enum_strings()
    {
        string json = CdcJsonContract.Serialize(SampleContract);

        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["contractVersion"]!.GetValue<int>().Should().Be(1);
        root["readiness"]!.GetValue<string>().Should().Be("notReady");
        root["targetIdentity"]!["provider"]!.GetValue<string>().Should().Be("sqlServer");
        root["targetIdentity"]!["dataStoreId"]!.GetValue<string>().Should().Be("7");
        json.Should().NotContain("Readiness");
        json.Should().NotContain("CdcReadiness");
        json.Should().NotContain("\"readiness\":1");
    }

    [Test]
    public void It_deserializes_valid_contract_payloads()
    {
        string json = CdcJsonContract.Serialize(SampleContract);

        CdcContractReadResult<SampleCdcContract> result = CdcJsonContract.Deserialize<SampleCdcContract>(
            json
        );

        result.Succeeded.Should().BeTrue();
        result.Contract.Should().Be(SampleContract);
        result.Diagnostics.Should().BeEmpty();
    }

    [TestCase("1")]
    [TestCase("\"readyNow\"")]
    public void It_rejects_invalid_enum_values_with_typed_diagnostics(string readinessJson)
    {
        string json = $$"""
            {
              "contractVersion": 1,
              "readiness": {{readinessJson}},
              "observedAt": "2026-08-17T13:10:11+00:00",
              "targetIdentity": {
                "deploymentKey": "deployment-a",
                "tenantKey": "default",
                "dataStoreId": "7",
                "instanceKey": "instance-a",
                "generation": 3,
                "provider": "sqlServer"
              }
            }
            """;

        CdcContractReadResult<SampleCdcContract> result = CdcJsonContract.Deserialize<SampleCdcContract>(
            json
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(CdcDiagnosticCategory.InvalidEnumValue);
    }

    [Test]
    public void It_reports_missing_required_contract_version()
    {
        string json = $$"""
            {
              "readiness": "notReady",
              "observedAt": "2026-08-17T13:10:11+00:00",
              "targetIdentity": {
                "deploymentKey": "deployment-a",
                "tenantKey": "default",
                "dataStoreId": "7",
                "instanceKey": "instance-a",
                "generation": 3,
                "provider": "sqlServer"
              }
            }
            """;

        CdcContractReadResult<SampleCdcContract> result = CdcJsonContract.Deserialize<SampleCdcContract>(
            json
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(CdcDiagnosticCategory.MissingRequiredField);
    }

    [Test]
    public void It_reports_invalid_contract_versions()
    {
        string json = CdcJsonContract.Serialize(SampleContract with { ContractVersion = 2 });

        CdcContractReadResult<SampleCdcContract> result = CdcJsonContract.Deserialize<SampleCdcContract>(
            json
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(CdcDiagnosticCategory.InvalidContractVersion);
    }

    [Test]
    public void It_reports_malformed_payloads()
    {
        CdcContractReadResult<SampleCdcContract> result = CdcJsonContract.Deserialize<SampleCdcContract>(
            "{ \"contractVersion\": 1,"
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(CdcDiagnosticCategory.MalformedPayload);
    }

    [Test]
    public void It_reports_future_utc_timestamps()
    {
        DateTimeOffset now = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);

        CdcContractValidationResult result = CdcJsonContract.ValidateNotFutureUtcTimestamp(
            now.AddTicks(1),
            now,
            "$.observedAt"
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(CdcDiagnosticCategory.FutureUtcTimestamp);
    }

    private sealed record SampleCdcContract(
        [property: JsonRequired] int ContractVersion,
        CdcReadiness Readiness,
        DateTimeOffset ObservedAt,
        CdcTargetIdentity TargetIdentity
    ) : ICdcJsonContract;
}
