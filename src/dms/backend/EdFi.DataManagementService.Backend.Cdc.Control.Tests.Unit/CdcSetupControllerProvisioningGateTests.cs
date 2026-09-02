// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// The fences the provisioning steps carry. The shared Connect offset store and the binding's own
/// Kafka artifacts are validated before a connector is registered rather than only reported in the
/// admission afterwards, and the instance-database connection every provider pass runs over is
/// established under the same step budget the passes themselves run under.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcSetupControllerProvisioningGate")]
public class Given_CdcSetupControllerProvisioningGate
{
    /// <summary>
    /// The store is cluster-scoped state a registered connector immediately commits its source
    /// positions through, so a nonconforming one ends the sequence where it is observed. An admission
    /// that carried the nonconformance to the end would be reporting it about a connector the same
    /// sequence had already started.
    /// </summary>
    [TestCase(CdcConnectOffsetStorePolicyState.Invalid)]
    [TestCase(CdcConnectOffsetStorePolicyState.Unknown)]
    public async Task It_registers_no_connector_against_a_nonconforming_shared_offset_store(
        CdcConnectOffsetStorePolicyState policyState
    )
    {
        CdcSetupControllerHarness harness = new() { OffsetStoreState = policyState };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        Refusal(admission, "enableConnectOffsetStoreNotSatisfied")
            .Category.Should()
            .Be(CdcDiagnosticCategory.ConnectOffsetStoreInvalid);
        NoConnectorWasRegistered(harness);

        // The binding's own artifacts are not provisioned either: the sequence stops at the store.
        A.CallTo(() =>
                harness.Kafka.EnsureBindingKafkaPolicyAsync(
                    A<CdcObservationContext>._,
                    A<CdcArtifactInventory>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    /// <summary>
    /// The worker-only grants are part of the same evidence: a store whose access is not the one the
    /// deployment governs is nonconforming however conforming its topic configuration is.
    /// </summary>
    [TestCase(CdcConnectOffsetStoreItemState.Invalid)]
    [TestCase(CdcConnectOffsetStoreItemState.Unknown)]
    public async Task It_registers_no_connector_against_an_offset_store_whose_grants_are_not_governed(
        CdcConnectOffsetStoreItemState aclState
    )
    {
        CdcSetupControllerHarness harness = new() { OffsetStoreAclState = aclState };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        Refusal(admission, "enableConnectOffsetStoreNotSatisfied").Should().NotBeNull();
        NoConnectorWasRegistered(harness);
    }

    /// <summary>
    /// The binding's topics, grants, and record-size budget are what the connector publishes through,
    /// and the composed policy is refused before registration for the same reason the shared store is.
    /// </summary>
    [TestCase(CdcKafkaPolicyState.Invalid)]
    [TestCase(CdcKafkaPolicyState.Unknown)]
    public async Task It_registers_no_connector_against_a_nonconforming_binding_kafka_policy(
        CdcKafkaPolicyState policyState
    )
    {
        CdcSetupControllerHarness harness = new() { KafkaPolicyState = policyState };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        Refusal(admission, "enableKafkaPolicyNotSatisfied")
            .Category.Should()
            .Be(CdcDiagnosticCategory.KafkaPolicyInvalid);
        NoConnectorWasRegistered(harness);
    }

    /// <summary>
    /// One nonconforming item is enough. The record-size budget drives the producer overrides the
    /// connector is rendered with, so a budget the broker limits do not admit must not reach a worker.
    /// </summary>
    [TestCase(CdcKafkaPolicyItemState.Invalid)]
    [TestCase(CdcKafkaPolicyItemState.Unknown)]
    public async Task It_registers_no_connector_against_an_unproven_record_size_budget(
        CdcKafkaPolicyItemState recordSizeState
    )
    {
        CdcSetupControllerHarness harness = new() { KafkaRecordSizeState = recordSizeState };

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        Refusal(admission, "enableKafkaPolicyNotSatisfied").Should().NotBeNull();
        NoConnectorWasRegistered(harness);
    }

    /// <summary>
    /// Establishing the connection is provider work and is budgeted as provider work. A database that
    /// never answers ends the step when the budget is spent rather than holding the verb open, and the
    /// enablement still produces the admission contract it owes.
    /// </summary>
    [Test]
    public async Task It_ends_the_enablement_when_the_database_connection_outlasts_the_provider_budget()
    {
        CdcSetupControllerHarness harness = new();
        harness.Timeouts.ProviderSetup = TimeSpan.FromMilliseconds(50);
        A.CallTo(() => harness.Connection.OpenAsync(A<CancellationToken>._))
            .ReturnsLazily((CancellationToken token) => Task.Delay(Timeout.Infinite, token));

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        CdcDiagnostic refusal = Refusal(admission, "enableProviderConnectionUnavailable");
        refusal.Category.Should().Be(CdcDiagnosticCategory.ProviderSetupInvalid);
        refusal.Observed.Should().Be("timedOut");
        NoProviderPassRan(harness);
    }

    /// <summary>
    /// A provider that refuses the connection outright is reported the same way: as the failed step it
    /// is, with only the rejection's type, because a provider message quotes connection settings.
    /// </summary>
    [Test]
    public async Task It_reports_a_refused_instance_database_connection_as_a_failed_step()
    {
        CdcSetupControllerHarness harness = new();
        A.CallTo(() => harness.Connection.OpenAsync(A<CancellationToken>._))
            .Throws(new InvalidOperationException("host=dms-postgresql;password=secret"));

        CdcAdmission admission = await harness.EnableAsync();

        using var _ = new AssertionScope();
        admission.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        CdcDiagnostic refusal = Refusal(admission, "enableProviderConnectionUnavailable");
        refusal.Observed.Should().Be(nameof(InvalidOperationException));
        admission
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Message.Contains("password", StringComparison.Ordinal));
        NoProviderPassRan(harness);
    }

    /// <summary>
    /// The status read reaches the same database through the same budgeted open, so an unreachable
    /// provider is a status the operator can read rather than an exception out of the verb.
    /// </summary>
    [Test]
    public async Task It_reports_an_unreachable_instance_database_as_a_status_rather_than_throwing()
    {
        CdcSetupControllerHarness harness = new()
        {
            BindingRead = CdcSetupControllerHarness.Present(CdcSetupControllerHarness.Binding()),
        };
        A.CallTo(() => harness.Connection.OpenAsync(A<CancellationToken>._))
            .Throws(new InvalidOperationException("unreachable"));

        CdcStatus status = await harness.StatusAsync();

        using var _ = new AssertionScope();
        status
            .Targets.Should()
            .ContainSingle()
            .Subject.Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "statusProviderConnectionUnavailable");
        NoProviderPassRan(harness);
    }

    private static void NoConnectorWasRegistered(CdcSetupControllerHarness harness)
    {
        A.CallTo(() =>
                harness.Connect.ValidateConnectorPluginConfigAsync(
                    A<string>._,
                    A<IReadOnlyDictionary<string, string>>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        A.CallTo(() =>
                harness.Connect.PutConnectorConfigAsync(
                    A<string>._,
                    A<IReadOnlyDictionary<string, string>>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    private static void NoProviderPassRan(CdcSetupControllerHarness harness)
    {
        A.CallTo(() => harness.ProviderSetup.SetupAsync(A<CdcProviderSetupRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    private static CdcDiagnostic Refusal(CdcAdmission admission, string code) =>
        admission.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == code).Subject;
}
