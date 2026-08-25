// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcObservationEnvelope")]
public class Given_CdcObservationEnvelope
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = ObservedAt.AddMinutes(1);

    private const string OperationId = "operation-1";
    private const string SourceFingerprint =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";

    private static CdcTargetIdentity TargetIdentity =>
        new("dms-local", "default", "1", "data-store-1", 1, CdcProvider.Postgresql);

    private static CdcObservationValidationContext Context =>
        new(OperationId, TargetIdentity, SourceFingerprint, Now);

    [Test]
    public void It_serializes_provider_setup_observations_with_the_common_envelope()
    {
        CdcProviderSetupObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            TargetIdentity,
            CdcProvider.Postgresql,
            SourceFingerprint,
            CdcProviderSetupMode.InitialCreateOrExactMatch,
            CdcProviderSetupOutcome.Satisfied,
            CdcProviderSetupState.Matched,
            CdcProviderSetupState.Matched,
            CdcProviderSetupState.Matched,
            CdcProviderSetupState.Matched,
            []
        );

        string json = CdcJsonContract.Serialize(observation);
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root.Select(property => property.Key)
            .Should()
            .ContainInOrder(
                "contractVersion",
                "operationId",
                "observedAt",
                "targetIdentity",
                "provider",
                "physicalSourceFingerprint"
            );
        root["provider"]!.GetValue<string>().Should().Be("postgresql");
        root["setupMode"]!.GetValue<string>().Should().Be("initialCreateOrExactMatch");
        root["setupOutcome"]!.GetValue<string>().Should().Be("satisfied");
        string retiredProviderHistoryMember = "provider" + "HistoryState";
        root.Should().NotContainKey(retiredProviderHistoryMember);
        json.Should().NotContain("manifestPayload");
        json.Should().NotContain("connectionString");
        json.Should().NotContain("databaseName");
        json.Should().NotContain("rawException");

        CdcContractReadResult<CdcProviderSetupObservation> readResult =
            CdcJsonContract.Deserialize<CdcProviderSetupObservation>(json);
        CdcContractValidationResult validationResult = CdcProviderSetupObservationValidator.Validate(
            readResult.Contract!,
            Context
        );

        readResult.Succeeded.Should().BeTrue();
        validationResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_rejects_envelope_operation_target_provider_source_and_future_timestamp_mismatches()
    {
        CdcProviderSetupObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            "operation-2",
            Now.AddSeconds(1),
            TargetIdentity with
            {
                InstanceKey = "data-store-2",
            },
            CdcProvider.SqlServer,
            "sha256:9caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851",
            CdcProviderSetupMode.ValidateOnly,
            CdcProviderSetupOutcome.Unknown,
            CdcProviderSetupState.Unknown,
            CdcProviderSetupState.Unknown,
            CdcProviderSetupState.Unknown,
            CdcProviderSetupState.Unknown,
            []
        );

        CdcContractValidationResult result = CdcProviderSetupObservationValidator.Validate(
            observation,
            Context
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.OperationMismatch)
            .And.Contain(CdcDiagnosticCategory.InvalidTimestamp)
            .And.Contain(CdcDiagnosticCategory.TargetMismatch)
            .And.Contain(CdcDiagnosticCategory.ProviderMismatch)
            .And.Contain(CdcDiagnosticCategory.SourceMismatch);
    }
}
