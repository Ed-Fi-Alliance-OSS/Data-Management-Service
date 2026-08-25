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
[Category("InitialCdcEligibility")]
public class Given_InitialCdcEligibility
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 8, 17, 13, 9, 55, TimeSpan.Zero);
    private static readonly DateTimeOffset DurableObservedAt = new(2026, 8, 17, 13, 10, 10, TimeSpan.Zero);
    private static readonly DateTimeOffset ObservedAt = DurableObservedAt.AddSeconds(1);
    private static readonly DateTimeOffset Now = ObservedAt.AddMinutes(1);

    private const string OperationId = "operation-1";
    private const string ProofId = "proof-1";
    private const string SetupControllerRunId = "setup-run-1";
    private const string SourceFingerprint =
        "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851";

    private static CdcTargetIdentity TargetIdentity =>
        new("dms-local", "default", "1", "data-store-1", 1, CdcProvider.Postgresql);

    private static CdcObservationValidationContext Context =>
        new(OperationId, TargetIdentity, SourceFingerprint, Now);

    [Test]
    public void It_accepts_trusted_provisioning_proof_and_single_transaction_empty_source_eligibility()
    {
        InitialCdcProvisioningProof proof = ValidProof();
        InitialCdcEligibilityObservation observation = ValidEligibility();

        string proofJson = CdcJsonContract.Serialize(proof);
        string observationJson = CdcJsonContract.Serialize(observation);
        JsonObject proofRoot = JsonNode.Parse(proofJson)!.AsObject();
        JsonObject observationRoot = JsonNode.Parse(observationJson)!.AsObject();

        proofRoot["databaseCreationMode"]!.GetValue<string>().Should().Be("createdForInitialCdcProvisioning");
        proofRoot["writeAdmissionState"]!.GetValue<string>().Should().Be("closedNeverOpened");
        observationRoot["consistencyScope"]!.GetValue<string>().Should().Be("singleProviderTransaction");
        observationRoot["cacheAheadState"]!.GetValue<string>().Should().Be("clear");
        observationRoot["canonicalRowsPresent"]!.GetValue<bool>().Should().BeFalse();
        observationJson.Should().Contain("physicalSourceFingerprint");
        observationJson.Should().NotContain("connectionString");
        observationJson.Should().NotContain("databaseName");

        CdcContractReadResult<InitialCdcProvisioningProof> proofRead =
            CdcJsonContract.Deserialize<InitialCdcProvisioningProof>(proofJson);
        CdcContractReadResult<InitialCdcEligibilityObservation> observationRead =
            CdcJsonContract.Deserialize<InitialCdcEligibilityObservation>(observationJson);

        InitialCdcProvisioningProofValidator
            .Validate(proofRead.Contract!, Context)
            .Succeeded.Should()
            .BeTrue();
        InitialCdcEligibilityObservationValidator
            .Validate(observationRead.Contract!, proofRead.Contract!, Context)
            .Succeeded.Should()
            .BeTrue();
    }

    [Test]
    public void It_rejects_provisioning_proof_that_is_not_current_closed_new_database_evidence()
    {
        InitialCdcProvisioningProof proof = ValidProof() with
        {
            ProofId = "bad/proof",
            OperationId = "operation-2",
            TargetIdentity = TargetIdentity with
            {
                InstanceKey = "data-store-2",
                Provider = CdcProvider.SqlServer,
            },
            Provider = CdcProvider.SqlServer,
            DatabaseCreationMode = (CdcDatabaseCreationMode)999,
            WriteAdmissionState = (CdcWriteAdmissionState)999,
            IssuedAt = Now.AddSeconds(1),
        };

        CdcContractValidationResult result = InitialCdcProvisioningProofValidator.Validate(proof, Context);

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidObservation)
            .And.Contain(CdcDiagnosticCategory.OperationMismatch)
            .And.Contain(CdcDiagnosticCategory.TargetMismatch)
            .And.Contain(CdcDiagnosticCategory.ProviderMismatch)
            .And.Contain(CdcDiagnosticCategory.InvalidTimestamp);
    }

    [Test]
    public void It_rejects_eligibility_that_is_not_correlated_authoritative_empty_source_evidence()
    {
        InitialCdcEligibilityObservation observation = ValidEligibility() with
        {
            DurableObservedAt = ObservedAt.AddSeconds(1),
            SetupControllerRunId = "setup-run-2",
            WriteAdmissionProofId = "proof-2",
            ConsistencyScope = (CdcConsistencyScope)999,
            LifecycleState = CdcLifecycleState.Unknown,
            CacheAheadState = CdcCacheAheadState.Unknown,
            CanonicalRowsPresent = true,
            CacheRowsPresent = true,
            WorkRowsPresent = true,
            ProviderConsistencyToken = "{{{",
        };

        CdcContractValidationResult result = InitialCdcEligibilityObservationValidator.Validate(
            observation,
            ValidProof(),
            Context
        );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.OperationMismatch)
            .And.Contain(CdcDiagnosticCategory.InvalidObservation)
            .And.Contain(CdcDiagnosticCategory.InvalidOrdering)
            .And.Contain(CdcDiagnosticCategory.UnsafeEvidence);
    }

    private static InitialCdcProvisioningProof ValidProof() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            ProofId,
            OperationId,
            TargetIdentity,
            CdcProvider.Postgresql,
            SetupControllerRunId,
            CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning,
            CdcWriteAdmissionState.ClosedNeverOpened,
            IssuedAt
        );

    private static InitialCdcEligibilityObservation ValidEligibility() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            OperationId,
            ObservedAt,
            DurableObservedAt,
            TargetIdentity,
            CdcProvider.Postgresql,
            SourceFingerprint,
            SetupControllerRunId,
            ProofId,
            CdcConsistencyScope.SingleProviderTransaction,
            CdcLifecycleState.Disabled,
            CdcCacheAheadState.Clear,
            false,
            false,
            false,
            "single transaction snapshot visible",
            []
        );
}
