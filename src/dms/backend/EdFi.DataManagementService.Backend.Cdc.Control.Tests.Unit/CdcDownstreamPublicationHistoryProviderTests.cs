// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// The E18 offline read-acceleration commands are admitted only for a projection proven never to
/// have been published downstream, and no CDC record proves that. Each case here is therefore either
/// positive proof of publication, which the gate must keep rejecting on, or an evidence shape that
/// leaves the history unknown, which it must also keep rejecting on. What must never happen is the
/// admitting status being reported from the absence of a record.
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

    /// <summary>
    /// Other targets' bindings are not evidence about this one. A store holding one unrelated binding
    /// and no record naming this target establishes only which records exist now, so the history is
    /// unknown and the destructive commands stay rejected.
    /// </summary>
    [Test]
    public async Task It_reports_unknown_when_the_deployment_binds_only_other_targets()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "default", dataStoreId: "7", CurrentFingerprintValue))
        );

        using AssertionScope assertions = new();
        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
        observation.EvidenceGenerationIdentifier.Should().BeNull();
    }

    /// <summary>
    /// Retirement deletes the binding record it retires, so a target that was published and then
    /// retired reads as a deployment binding only other targets. The retirement record is what keeps
    /// that history, and finding one reports the target historical — positive proof of publication,
    /// rather than the unknown the bare absence of a binding would leave.
    /// </summary>
    [Test]
    public async Task It_reports_historical_when_the_targets_only_binding_was_retired()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "default", dataStoreId: "7", CurrentFingerprintValue)),
            retirementResult: Retired(
                Retirement(tenantKey: "default", dataStoreId: "1", CurrentFingerprintValue, generation: 4)
            )
        );

        using AssertionScope assertions = new();
        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Historical);
        observation.EvidenceGenerationIdentifier.Should().Be("4");
    }

    /// <summary>
    /// A retirement of the same source under a different tenant or data store is another target's
    /// history and says nothing about this one. It leaves this target with no record naming it, which
    /// is unknown rather than proof that it was never published.
    /// </summary>
    [Test]
    public async Task It_reports_unknown_when_only_another_targets_binding_was_retired()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "default", dataStoreId: "7", CurrentFingerprintValue)),
            retirementResult: Retired(
                Retirement(tenantKey: "default", dataStoreId: "9", CurrentFingerprintValue)
            )
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
    }

    /// <summary>
    /// The retirement records answer the question the absent binding raised, so a listing that cannot
    /// be read leaves it unanswered rather than settling it in the permissive direction.
    /// </summary>
    [Test]
    public async Task It_reports_unknown_when_the_retirement_records_cannot_be_listed()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "default", dataStoreId: "7", CurrentFingerprintValue)),
            retirementResult: UnreadableRetirements()
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
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

    /// <summary>
    /// Retiring the deployment's only binding empties the binding tree, so the retirement record is
    /// the sole trace of what this target published. It is evidence in its own right — it names this
    /// deployment and this target — and reading it is what keeps a normal retirement from reporting
    /// the target as one nothing is known about.
    /// </summary>
    [Test]
    public async Task It_reports_historical_when_the_retired_binding_was_the_only_one()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(),
            retirementResult: Retired(
                Retirement(tenantKey: "default", dataStoreId: "1", CurrentFingerprintValue, generation: 4)
            )
        );

        using AssertionScope assertions = new();
        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Historical);
        observation.EvidenceGenerationIdentifier.Should().Be("4");
    }

    /// <summary>
    /// A retirement naming some other target says nothing about this one, and an empty binding listing
    /// still cannot be told apart from an unwritten store. The answer stays unknown rather than
    /// becoming internal-only, which is the status that admits the destructive commands.
    /// </summary>
    [Test]
    public async Task It_reports_unknown_for_an_empty_store_whose_only_retirement_names_another_target()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(),
            retirementResult: Retired(
                Retirement(tenantKey: "default", dataStoreId: "9", CurrentFingerprintValue)
            )
        );

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

    /// <summary>
    /// Only the data-store id differs in the "other targets" case, so without this one the tenant key
    /// could be ignored entirely and every case would still pass: another tenant's binding would be
    /// read as this target's own and report it active.
    /// </summary>
    [Test]
    public async Task It_reports_unknown_when_only_another_tenant_binds_the_same_data_store_id()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "other-tenant", dataStoreId: "1", CurrentFingerprintValue))
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
    }

    /// <summary>
    /// The E18 target key compares tenant keys case-insensitively. A binding recorded under a
    /// different casing is the same binding, and reading it as absent would report the admitting
    /// status for a target that is published.
    /// </summary>
    [Test]
    public async Task It_matches_a_binding_recorded_under_a_different_tenant_key_casing()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "Tenant-A", dataStoreId: "1", CurrentFingerprintValue)),
            targetKey: DocumentCacheTargetKey.Create("tenant-a", 1)
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Active);
    }

    /// <summary>
    /// A latched source-history incident says the binding's stream broke, not that it never existed.
    /// The record still proves the target was bound, so it must disqualify it like any other.
    /// </summary>
    [Test]
    public async Task It_reports_active_for_a_matching_binding_carrying_a_latched_incident()
    {
        CdcBindingStateContract latched = Binding(
            tenantKey: "default",
            dataStoreId: "1",
            CurrentFingerprintValue
        ) with
        {
            State = CdcBindingState.IncidentLatched,
        };

        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(latched)
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Active);
    }

    /// <summary>
    /// With no resolved fingerprint nothing can be the same physical source, so a binding for the
    /// target is history rather than a live publication. The E18 evaluator rejects an unresolved
    /// fingerprint outright, so this only has to avoid claiming more than was observed.
    /// </summary>
    [Test]
    public async Task It_reports_historical_for_a_matching_binding_when_the_source_is_unresolved()
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "default", dataStoreId: "1", CurrentFingerprintValue)),
            currentPhysicalSourceFingerprint: null
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Historical);
        observation.PhysicalSourceFingerprint.Should().BeNull();
    }

    /// <summary>
    /// An unresolved fingerprint must never be admitted even when no binding names the target: the
    /// evaluator needs the fingerprint to prove the observation describes the source in hand.
    /// </summary>
    [Test]
    public async Task It_keeps_the_gate_closed_when_the_source_is_unresolved_and_no_binding_matches()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("default", 1);

        DocumentCacheDownstreamPublicationHistoryProofResult rejected =
            DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
                targetKey,
                currentPhysicalSourceFingerprint: null,
                await ObserveAsync(
                    Listed(Binding(tenantKey: "default", dataStoreId: "7", CurrentFingerprintValue)),
                    currentPhysicalSourceFingerprint: null
                )
            );

        rejected.IsAccepted.Should().BeFalse();
        rejected
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.ExpectedSourceMismatch);
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task It_reports_unknown_when_the_deployment_key_is_blank(string deploymentKey)
    {
        DocumentCacheDownstreamPublicationHistoryObservation observation = await ObserveAsync(
            Listed(Binding(tenantKey: "default", dataStoreId: "1", CurrentFingerprintValue)),
            deploymentKey: deploymentKey
        );

        observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
    }

    [Test]
    public async Task It_rejects_a_missing_target_key()
    {
        CdcDownstreamPublicationHistoryProvider provider = BuildProvider(Listed(), DeploymentKey);

        Func<Task> observe = () => provider.ObserveAsync(null!, null);

        await observe.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task It_observes_nothing_once_cancellation_is_requested()
    {
        CdcDownstreamPublicationHistoryProvider provider = BuildProvider(Listed(), DeploymentKey);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        Func<Task> observe = () =>
            provider.ObserveAsync(
                DocumentCacheTargetKey.Create("default", 1),
                new DocumentCachePhysicalSourceFingerprint(CurrentFingerprintValue),
                cancellation.Token
            );

        await observe.Should().ThrowAsync<OperationCanceledException>();
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

    /// <summary>
    /// The gate is closed by an unknown history exactly as it is by a present one, so a store whose
    /// records all name other targets admits nothing. This is the case the provider must never turn
    /// into an admission: it is the shape a mis-mounted or partially restored state root produces.
    /// </summary>
    [Test]
    public async Task It_keeps_the_administrative_gate_closed_when_no_record_names_the_target()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("default", 1);
        DocumentCachePhysicalSourceFingerprint fingerprint = new(CurrentFingerprintValue);

        DocumentCacheDownstreamPublicationHistoryProofResult rejected =
            DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
                targetKey,
                fingerprint,
                await ObserveAsync(
                    Listed(Binding(tenantKey: "default", dataStoreId: "7", CurrentFingerprintValue))
                )
            );

        using AssertionScope assertions = new();
        rejected.IsAccepted.Should().BeFalse();
        rejected
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown);
        rejected.Diagnostics.Should().NotBeEmpty();
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

    /// <summary>
    /// Passing <c>null</c> for <paramref name="currentPhysicalSourceFingerprint"/> models a target
    /// whose physical source could not be resolved, which the default value never does.
    /// </summary>
    private static async Task<DocumentCacheDownstreamPublicationHistoryObservation> ObserveAsync(
        CdcBindingLifecycleListResult listResult,
        DocumentCacheTargetKey? targetKey = null,
        string? deploymentKey = DeploymentKey,
        string? currentPhysicalSourceFingerprint = CurrentFingerprintValue,
        CdcRetirementListResult? retirementResult = null
    ) =>
        await BuildProvider(listResult, deploymentKey, retirementResult)
            .ObserveAsync(
                targetKey ?? DocumentCacheTargetKey.Create("default", 1),
                currentPhysicalSourceFingerprint is null
                    ? null
                    : new DocumentCachePhysicalSourceFingerprint(currentPhysicalSourceFingerprint)
            );

    private static CdcDownstreamPublicationHistoryProvider BuildProvider(
        CdcBindingLifecycleListResult listResult,
        string? deploymentKey,
        CdcRetirementListResult? retirementResult = null
    )
    {
        ICdcBindingLifecycleService bindingLifecycleService = A.Fake<ICdcBindingLifecycleService>();
        A.CallTo(() => bindingLifecycleService.ListBindingsAsync(A<string>._, A<CancellationToken>._))
            .Returns(listResult);
        A.CallTo(() => bindingLifecycleService.ListRetirementsAsync(A<string>._, A<CancellationToken>._))
            .Returns(retirementResult ?? Retired());

        return new CdcDownstreamPublicationHistoryProvider(
            bindingLifecycleService,
            BuildConfiguration(deploymentKey),
            new FixedTimeProvider(ObservedAt)
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

    private static CdcRetirementListResult Retired(params CdcRetirement[] retirements) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            ObservedAt,
            CdcControlPlaneOperationStatus.Succeeded,
            retirements,
            []
        );

    private static CdcRetirementListResult UnreadableRetirements() =>
        new(
            CdcJsonContract.CurrentContractVersion,
            ObservedAt,
            CdcControlPlaneOperationStatus.StateStoreUnavailable,
            [],
            []
        );

    private static CdcRetirement Retirement(
        string tenantKey,
        string dataStoreId,
        string physicalSourceFingerprint,
        long generation = 1
    ) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            DeploymentKey,
            tenantKey,
            dataStoreId,
            "instance",
            generation,
            physicalSourceFingerprint,
            ObservedAt,
            CdcJsonContract.CurrentContractVersion
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
