// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// The E18 offline read-acceleration commands are admitted only when this provider proves the
/// target was never published downstream, so each case here is either an admission the gate must
/// accept or a rejection it must keep closed.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcDownstreamPublicationHistory")]
public class Given_CdcDownstreamPublicationHistoryProvider
{
    private const string DeploymentKey = "deployment";
    private const string CurrentFingerprintValue =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string OtherFingerprintValue =
        "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task It_reports_internal_only_when_the_deployment_binds_only_other_targets()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "default", dataStoreId: "7", CurrentFingerprintValue))
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.InternalOnly);
        observation.EvidenceGenerationIdentifier.Should().BeNull();
    }

    /// <summary>
    /// A fresh volume, a mis-mounted root, and a root pointed at another deployment all list empty,
    /// so an empty store is never the proof that this target was not published. Admitting it would
    /// unlock the destructive commands on precisely the evidence the deployment failed to supply.
    /// </summary>
    [Test]
    public async Task It_reports_unknown_when_the_deployment_holds_no_bindings()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(Listed());

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
    }

    [Test]
    public async Task It_reports_active_when_a_binding_matches_the_target_and_the_current_source()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "default", dataStoreId: "1", CurrentFingerprintValue, generation: 4))
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Active);
        observation.EvidenceGenerationIdentifier.Should().Be("4");
    }

    [Test]
    public async Task It_reports_historical_when_a_binding_matches_the_target_under_another_source()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "default", dataStoreId: "1", OtherFingerprintValue, generation: 2))
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Historical);
        observation.EvidenceGenerationIdentifier.Should().Be("2");
    }

    /// <summary>
    /// The binding record spells the default tenant as a literal token while the E18 target key
    /// spells it as the empty string. Comparing the two raw would find no binding for every
    /// single-tenant deployment and report the most dangerous answer the gate can be given.
    /// </summary>
    [Test]
    public async Task It_matches_a_default_tenant_target_against_the_binding_tenant_token()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "default", dataStoreId: "1", CurrentFingerprintValue)),
            targetKey: DocumentCacheTargetKey.Create(string.Empty, 1)
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Active);
    }

    [Test]
    public async Task It_prefers_the_current_source_binding_over_an_earlier_generation()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(
                Binding(tenantKey: "default", dataStoreId: "1", OtherFingerprintValue, generation: 1),
                Binding(tenantKey: "default", dataStoreId: "1", CurrentFingerprintValue, generation: 2)
            )
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Active);
        observation.EvidenceGenerationIdentifier.Should().Be("2");
    }

    [Test]
    public async Task It_reports_unknown_when_no_deployment_key_is_configured()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "default", dataStoreId: "1", CurrentFingerprintValue)),
            deploymentKey: null
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
    }

    [TestCase(CdcControlPlaneOperationStatus.StateStoreUnavailable)]
    [TestCase(CdcControlPlaneOperationStatus.InvalidOperation)]
    public async Task It_reports_unknown_when_the_state_store_cannot_be_listed(
        CdcControlPlaneOperationStatus status
    )
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            new CdcBindingLifecycleListResult(
                CdcJsonContract.CurrentContractVersion,
                ObservedAt,
                status,
                [],
                []
            )
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
    }

    /// <summary>
    /// An unreadable record may be the very binding that would disqualify this target, so the
    /// listing is inconclusive even though every other record parsed.
    /// </summary>
    [Test]
    public async Task It_reports_unknown_when_a_record_cannot_be_read_as_a_binding()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(
                new CdcBindingStateContract(
                    CdcJsonContract.CurrentContractVersion,
                    ObservedAt,
                    CdcBindingState.BindingMismatch,
                    null,
                    null
                )
            )
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
    }

    /// <summary>
    /// The evaluator checks the fingerprint before the status. Withholding the resolved fingerprint
    /// on a rejecting observation would classify every rejection as a source mismatch that was
    /// never observed.
    /// </summary>
    [Test]
    public async Task It_reports_the_resolved_fingerprint_on_a_rejecting_observation()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "default", dataStoreId: "1", OtherFingerprintValue))
        );

        observation.PhysicalSourceFingerprint!.Value.Should().Be(CurrentFingerprintValue);
    }

    [Test]
    public async Task It_admits_the_administrative_gate_only_on_proven_internal_only_evidence()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("default", 1);
        DocumentCachePhysicalSourceFingerprint fingerprint = new(CurrentFingerprintValue);

        DocumentCacheDownstreamPublicationHistoryProofResult admitted =
            DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
                targetKey,
                fingerprint,
                await ObserveAsync(
                    Listed(Binding(tenantKey: "default", dataStoreId: "7", CurrentFingerprintValue))
                )
            );

        admitted.IsAccepted.Should().BeTrue();
        admitted.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        admitted.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public async Task It_keeps_the_administrative_gate_closed_on_a_matching_binding()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("default", 1);
        DocumentCachePhysicalSourceFingerprint fingerprint = new(CurrentFingerprintValue);

        DocumentCacheDownstreamPublicationHistoryProofResult rejected =
            DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
                targetKey,
                fingerprint,
                await ObserveAsync(
                    Listed(Binding(tenantKey: "default", dataStoreId: "1", CurrentFingerprintValue))
                )
            );

        rejected.IsAccepted.Should().BeFalse();
        rejected
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown);
        rejected.Diagnostics.Should().NotBeEmpty();
    }

    private static async Task<DocumentCacheDownstreamPublicationHistoryObservation> ObserveAsync(
        CdcBindingLifecycleListResult listResult,
        DocumentCacheTargetKey? targetKey = null,
        string? deploymentKey = DeploymentKey
    )
    {
        ICdcBindingLifecycleService bindingLifecycleService = A.Fake<ICdcBindingLifecycleService>();
        A.CallTo(() => bindingLifecycleService.ListBindingsAsync(A<string>._, A<CancellationToken>._))
            .Returns(listResult);

        CdcDownstreamPublicationHistoryProvider provider = new(
            bindingLifecycleService,
            BuildConfiguration(deploymentKey),
            new FixedTimeProvider(ObservedAt)
        );

        return await provider.ObserveAsync(
            targetKey ?? DocumentCacheTargetKey.Create("default", 1),
            new DocumentCachePhysicalSourceFingerprint(CurrentFingerprintValue)
        );
    }

    private static IConfiguration BuildConfiguration(string? deploymentKey)
    {
        Dictionary<string, string?> settings = new(StringComparer.Ordinal);
        if (deploymentKey is not null)
        {
            settings[CdcDownstreamPublicationHistoryProvider.DeploymentKeyConfigurationPath] = deploymentKey;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static CdcBindingLifecycleListResult Listed(params CdcBindingStateContract[] states) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            ObservedAt,
            CdcControlPlaneOperationStatus.Succeeded,
            states,
            []
        );

    private static CdcBindingStateContract Binding(
        string tenantKey,
        string dataStoreId,
        string physicalSourceFingerprint,
        long generation = 1
    ) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            ObservedAt,
            CdcBindingState.BindingPresent,
            new CdcBinding(
                CdcJsonContract.CurrentContractVersion,
                DeploymentKey,
                tenantKey,
                dataStoreId,
                "instance",
                generation,
                CdcProvider.Postgresql,
                physicalSourceFingerprint,
                "connector",
                "topic",
                3,
                CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
                CdcJsonContract.CurrentContractVersion
            ),
            null
        );

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
