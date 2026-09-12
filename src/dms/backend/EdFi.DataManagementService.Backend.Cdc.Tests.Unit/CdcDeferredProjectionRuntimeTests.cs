// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
public class Given_CdcDeferredProjectionRuntime
{
    private ICdcProjectionRuntime _inner = null!;
    private CdcDeferredProjectionRuntime _runtime = null!;
    private int _preparations;
    private bool _available;

    [SetUp]
    public void Setup()
    {
        _inner = A.Fake<ICdcProjectionRuntime>();
        _preparations = 0;
        _available = true;
        _runtime = new(_ =>
        {
            _preparations++;
            return Task.FromResult<CdcTransportResult<ICdcProjectionRuntime>>(
                _available
                    ? new CdcTransportResult<ICdcProjectionRuntime>.Observed(_inner)
                    : new CdcTransportResult<ICdcProjectionRuntime>.Unavailable(
                        new(CdcDeploymentComponent.Projection, CdcDeploymentFailure.Unavailable)
                    )
            );
        });
    }

    [TearDown]
    public async Task Cleanup()
    {
        await _runtime.DisposeAsync();
        await _inner.DisposeAsync();
    }

    [Test]
    public async Task It_does_not_prepare_on_disposal_of_an_unused_stop_runtime()
    {
        await _runtime.DisposeAsync();
        _preparations.Should().Be(0);
        Fake.GetCalls(_inner).Should().BeEmpty();
    }

    [Test]
    public async Task It_initializes_once_without_starting_and_disposes_once()
    {
        await _runtime.InitializeAsync(default);
        await _runtime.InitializeAsync(default);
        await _runtime.DisposeAsync();
        await _runtime.DisposeAsync();
        _preparations.Should().Be(1);
        A.CallTo(() => _inner.StartProcessingAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _inner.DisposeAsync()).MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_retries_unavailable_preparation_on_a_later_watch_pass()
    {
        _available = false;
        Func<Task> initialize = () => _runtime.InitializeAsync(default);
        await initialize.Should().ThrowAsync<CdcEstablishedValidation.EvidenceException>();
        _available = true;
        await _runtime.ObserveAsync(default);
        _preparations.Should().Be(2);
        A.CallTo(() => _inner.ObserveAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.StartProcessingAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task It_never_creates_resources_after_disposal()
    {
        await _runtime.DisposeAsync();
        Func<Task> initialize = () => _runtime.InitializeAsync(default);
        await initialize.Should().ThrowAsync<ObjectDisposedException>();
        _preparations.Should().Be(0);
    }

    [Test]
    public async Task It_preserves_cancellation_and_disposes_resources_returned_at_the_boundary()
    {
        using var cancel = new CancellationTokenSource();
        await using var runtime = new CdcDeferredProjectionRuntime(_ =>
        {
            cancel.Cancel();
            return Task.FromResult<CdcTransportResult<ICdcProjectionRuntime>>(
                new CdcTransportResult<ICdcProjectionRuntime>.Observed(_inner)
            );
        });
        Func<Task> initialize = () => runtime.InitializeAsync(cancel.Token);
        await initialize.Should().ThrowAsync<OperationCanceledException>();
        await runtime.DisposeAsync();
        A.CallTo(() => _inner.DisposeAsync()).MustHaveHappenedOnceExactly();
    }
}
