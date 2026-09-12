// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(Ddl.CdcProvider.Postgresql)]
[TestFixture(Ddl.CdcProvider.SqlServer)]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
internal class Given_CdcBindingRetirementLifecycle(Ddl.CdcProvider provider) : CdcReadinessTestBase(provider)
{
    [SetUp]
    public async Task SetupRetirement()
    {
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None
        );
        var journal = await session.ReadAsync(Target, CancellationToken.None);
        await session.RecordRetirementIntentAsync(
            _request.Binding,
            journal.WorkflowId,
            Guid.NewGuid(),
            CancellationToken.None
        );
        _trace.Clear();
        Fake.ClearRecordedCalls(_connect);
        Fake.ClearRecordedCalls(_kafka);
        Fake.ClearRecordedCalls(_runtime);
    }

    [TestCase(CdcEstablishedValidationMode.PreStart)]
    [TestCase(CdcEstablishedValidationMode.RunningPublication)]
    public async Task It_rejects_ordinary_validation_before_live_or_projection_work(
        CdcEstablishedValidationMode mode
    )
    {
        var validation = new CdcEstablishedValidation(
            _root,
            _provider,
            _templates,
            _kafka,
            _connect,
            _worker,
            _metrics,
            _positions
        );
        (await validation.ValidateAsync(_request, _runtime, mode, 1000))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().BeEmpty();
    }

    [TestCase(CdcManagedLifecycleOperation.Start)]
    [TestCase(CdcManagedLifecycleOperation.Restart)]
    [TestCase(CdcManagedLifecycleOperation.Resume)]
    public async Task It_rejects_ordinary_resume_without_connect_mutation(
        CdcManagedLifecycleOperation operation
    )
    {
        var managed = new CdcManagedLifecycle(
            _root,
            _provider,
            _templates,
            _kafka,
            _connect,
            _worker,
            _metrics,
            [_positions]
        );
        var result = await managed.ExecuteAsync(new(_request, _runtime, 1000), operation);
        result.Succeeded.Should().BeFalse();
        result.Ready.Should().BeFalse();
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _connect.RestartAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_rejects_initial_enablement_and_registration_after_retirement_intent()
    {
        (await new CdcInitialEnablement(_root).ActivateAsync(_request, _runtime))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        (await RunAsync()).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _trace.Should().BeEmpty();
        (
            await _services
                .GetRequiredService<ICdcBindingLifecycleService>()
                .ExactMatchBindingAsync(_request.Binding)
        )
            .Status.Should()
            .Be(CdcControlPlaneOperationStatus.Succeeded);
    }
}
