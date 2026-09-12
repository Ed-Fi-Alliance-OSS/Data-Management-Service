// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using DdlProvider = EdFi.DataManagementService.Backend.Ddl.CdcProvider;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(DdlProvider.Postgresql)]
[TestFixture(DdlProvider.SqlServer)]
internal class Given_CdcInitialReadinessRuntime(DdlProvider provider)
{
    private CdcDeploymentRequest _request = null!;
    private CdcProjectionRuntime _runtime = null!;
    private ICdcProviderSourcePositionAdapter _positions = null!;
    private IDocumentCachePhysicalSourceFingerprintReader _fingerprints = null!;
    private DocumentCacheTargetExecutionContext _context = null!;

    [SetUp]
    public void Setup()
    {
        _request = CdcDeploymentRequestTestData.Request(provider);
        var target = DocumentCacheTargetKey.Create(
            _request.TargetIdentity.TenantKey,
            long.Parse(_request.TargetIdentity.DataStoreId)
        );
        var token =
            provider == DdlProvider.Postgresql
                ? RelationalProviderToken.Postgresql
                : RelationalProviderToken.SqlServer;
        _context = new(
            target,
            new(1),
            DocumentCacheTargetEffectiveSettings.FromOptions(new()),
            new(target.DataStoreId, token.Value),
            new(token, "private-resolved-connection"),
            new(_request.Binding.PhysicalSourceFingerprint),
            new(DocumentCacheLifecycleState.Tracking, false),
            new(DocumentCacheInventoryStatus.Satisfied, "Satisfied"),
            new(DocumentCacheEnqueueTriggerStatus.Satisfied, "Satisfied"),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );
        var registry = A.Fake<IDocumentCacheTargetRegistry>();
        A.CallTo(() => registry.CurrentRuntimeSnapshot)
            .Returns(new DocumentCacheTargetRuntimeSnapshot([_context], DateTimeOffset.UtcNow));
        _fingerprints = A.Fake<IDocumentCachePhysicalSourceFingerprintReader>();
        A.CallTo(() =>
                _fingerprints.ReadFingerprintAsync(_context.ConnectionInput.Value, A<CancellationToken>._)
            )
            .Returns(
                DocumentCachePhysicalSourceFingerprintReadResult.Success(_context.PhysicalSourceFingerprint)
            );
        _positions = A.Fake<ICdcProviderSourcePositionAdapter>();
        A.CallTo(() => _positions.Provider).Returns(_request.Binding.Provider);
        A.CallTo(() =>
                _positions.CaptureBarrierAsync(A<CdcProviderBarrierCaptureRequest>._, A<CancellationToken>._)
            )
            .Returns(CdcProviderBarrierCaptureResult.PostgresqlSuccess("0/10", DateTimeOffset.UtcNow));
        var services = new ServiceCollection()
            .AddSingleton(registry)
            .AddSingleton(_fingerprints)
            .AddSingleton(A.Fake<IDocumentCacheProjectionSupervisor>());
        _runtime = new(services.BuildServiceProvider(), target, new IdleSupervisor());
    }

    [TearDown]
    public async Task Teardown() => await _runtime.DisposeAsync();

    [Test]
    public async Task It_captures_from_the_selected_runtime_connection_after_refreshing_its_source()
    {
        await _runtime.StartProcessingAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await _runtime.CaptureBarrierAsync(_request, _positions, cancellation.Token);
        A.CallTo(() => _fingerprints.ReadFingerprintAsync("private-resolved-connection", cancellation.Token))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _positions.CaptureBarrierAsync(
                    A<CdcProviderBarrierCaptureRequest>.That.Matches(r =>
                        r.ConnectionString == _context.ConnectionInput.Value
                        && r.Binding == _request.Binding
                        && r.CommandTimeout == _request.Timing.CallTimeout
                        && r.CaptureWaitTimeout == _request.Timing.WaitTimeout
                        && r.PollInterval == _request.Timing.PollInterval
                    ),
                    cancellation.Token
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_cannot_capture_before_the_projection_supervisor_starts()
    {
        Func<Task> act = async () =>
            await _runtime.CaptureBarrierAsync(_request, _positions, CancellationToken.None);
        await act.Should().ThrowAsync<CdcWorkflowStateException>();
        A.CallTo(() =>
                _positions.CaptureBarrierAsync(A<CdcProviderBarrierCaptureRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_does_not_capture_from_a_replaced_physical_source()
    {
        await _runtime.StartProcessingAsync(CancellationToken.None);
        A.CallTo(() => _fingerprints.ReadFingerprintAsync(A<string>._, A<CancellationToken>._))
            .Returns(
                DocumentCachePhysicalSourceFingerprintReadResult.Success(new("sha256:" + new string('f', 64)))
            );
        Func<Task> act = async () =>
            await _runtime.CaptureBarrierAsync(_request, _positions, CancellationToken.None);
        await act.Should().ThrowAsync<CdcWorkflowStateException>();
        A.CallTo(() =>
                _positions.CaptureBarrierAsync(A<CdcProviderBarrierCaptureRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    private sealed class IdleSupervisor : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}
