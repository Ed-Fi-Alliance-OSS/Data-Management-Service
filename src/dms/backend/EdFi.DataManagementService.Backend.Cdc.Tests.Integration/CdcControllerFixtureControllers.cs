// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

/// <summary>
/// Production controllers with one retained state root and hooks on their actual dependencies.
/// Provider suites supply emitted-schema runtime/provider adapters; broker profiles supply real
/// administration/authorization adapters. This composition never supplies successful evidence.
/// </summary>
internal sealed class CdcControllerFixtureControllers
{
    private readonly CdcControllerFixture _fixture;
    private readonly LocalCdcWorkflowJournalStore _store;
    private readonly ICdcBindingLifecycleService _bindings;
    private readonly ICdcKafkaAdminAdapter _kafka;
    public CdcInitialEnablement Activation { get; }
    public CdcProviderSetupOrchestration ProviderSetup { get; }
    public CdcInitialReadiness Admission { get; }
    public CdcEstablishedValidation Validation { get; }
    public CdcControllerStatus Status { get; }
    public CdcManagedLifecycle Lifecycle { get; }

    public CdcControllerFixtureControllers(
        CdcControllerFixture fixture,
        ICdcProviderSetupService provider,
        ICdcConnectorTemplateService templates,
        ICdcKafkaAdminAdapter kafka,
        ICdcProviderSourcePositionAdapter positions,
        ICdcWorkerMetricsTransport metrics = null!
    )
    {
        _fixture = fixture;
        _store = fixture.CreateJournalStore();
        _bindings = fixture.Bindings;
        var setup = fixture.Hooks.Decorate(provider, _ => CdcControllerBoundary.ProviderProof);
        _kafka = fixture.Hooks.Decorate(kafka, _ => CdcControllerBoundary.Observation);
        Activation = new(_store, _bindings, TimeProvider.System);
        ProviderSetup = new(_store, _bindings, setup, templates, TimeProvider.System);
        Admission = new(
            _store,
            _bindings,
            setup,
            templates,
            _kafka,
            fixture.Connect,
            fixture.Worker,
            metrics ?? fixture.Metrics,
            positions,
            TimeProvider.System
        );
        Validation = new(
            _store,
            _bindings,
            setup,
            templates,
            _kafka,
            fixture.Connect,
            fixture.Worker,
            metrics ?? fixture.Metrics,
            positions,
            TimeProvider.System
        );
        Status = new(
            _store,
            _bindings,
            fixture.Connect,
            providerKind =>
                providerKind == positions.Provider
                    ? Validation
                    : throw new InvalidOperationException("Fixture provider mismatch."),
            TimeProvider.System
        );
        Lifecycle = new(_store, _bindings, fixture.Connect, fixture.Worker, Status, TimeProvider.System);
    }

    public ICdcProjectionRuntime HookRuntime(ICdcProjectionRuntime runtime) =>
        _fixture.Hooks.Decorate(
            runtime,
            name =>
                name switch
                {
                    nameof(ICdcProjectionRuntime.ActivateAsync) => CdcControllerBoundary.Activation,
                    nameof(ICdcProjectionRuntime.CaptureBarrierAsync) => CdcControllerBoundary.Barrier,
                    nameof(ICdcProjectionRuntime.DisposeAsync) => CdcControllerBoundary.WriterHandoff,
                    _ => CdcControllerBoundary.Observation,
                }
        );

    public CdcRecordSizeIncrease RecordSize(ICdcKafkaRecordSizeAdministration sizes) =>
        new(
            _store,
            _bindings,
            _kafka,
            _fixture.Hooks.Decorate(sizes, _ => CdcControllerBoundary.Rollout),
            _fixture.Connect,
            Validation,
            TimeProvider.System
        );

    public CdcBindingRetirement Retirement(
        ICdcKafkaArtifactCleanupAdapter kafka,
        ICdcProviderArtifactCleanupAdapter provider
    ) =>
        new(
            _store,
            _bindings,
            _fixture.Connect,
            _fixture.Hooks.Decorate(kafka, _ => CdcControllerBoundary.Cleanup),
            _fixture.Hooks.Decorate(provider, _ => CdcControllerBoundary.Cleanup),
            TimeProvider.System
        );
}
