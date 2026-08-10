// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Token;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// TokenCleanupService takes a TimeProvider, so these tests drive the real
/// ExecuteAsync/PeriodicTimer loop deterministically with a FakeTimeProvider:
/// advancing fake time fires the timer, and the sweep bound is exact fake-now minus
/// the validation clock skew, with no wall-clock waits anywhere.
/// </summary>
[TestFixture]
public class TokenCleanupServiceTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private static TokenCleanupService CreateService(
        IOpenIddictTokenRepository tokenRepository,
        FakeTimeProvider timeProvider,
        bool enabled = true,
        int intervalMinutes = 30
    ) =>
        new(
            Options.Create(
                new IdentityOptions
                {
                    TokenCleanupEnabled = enabled,
                    TokenCleanupIntervalMinutes = intervalMinutes,
                }
            ),
            NullLogger<TokenCleanupService>.Instance,
            tokenRepository,
            timeProvider
        );

    [TestFixture]
    public class Given_TokenCleanupIsDisabled
    {
        private IOpenIddictTokenRepository _tokenRepository = null!;

        [SetUp]
        public async Task Act()
        {
            _tokenRepository = A.Fake<IOpenIddictTokenRepository>();

            var service = CreateService(_tokenRepository, new FakeTimeProvider(StartTime), enabled: false);

            // Disabled ExecuteAsync returns without ever awaiting the timer, so it
            // completes synchronously; StartAsync's returned task reflects that.
            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);
        }

        [Test]
        public void It_never_calls_the_repository_to_delete_expired_tokens()
        {
            A.CallTo(() => _tokenRepository.DeleteExpiredTokensAsync(A<DateTimeOffset>._))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_TokenCleanupIsEnabled_WhenTheServiceStartsAndIntervalsElapse
    {
        private IOpenIddictTokenRepository _tokenRepository = null!;
        private TokenCleanupService _service = null!;
        private FakeTimeProvider _timeProvider = null!;
        private readonly List<DateTimeOffset> _capturedBounds = [];
        private int _callCountAfterPartialAdvance;
        private TaskCompletionSource _nextSweepObserved = null!;

        [SetUp]
        public async Task Act()
        {
            _capturedBounds.Clear();
            _timeProvider = new FakeTimeProvider(StartTime);
            _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
            A.CallTo(() => _tokenRepository.DeleteExpiredTokensAsync(A<DateTimeOffset>._))
                .Invokes(
                    (DateTimeOffset expiredBefore) =>
                    {
                        lock (_capturedBounds)
                        {
                            _capturedBounds.Add(expiredBefore);
                        }
                        _nextSweepObserved.TrySetResult();
                    }
                )
                .Returns(0);

            _nextSweepObserved = NewSignal();
            _service = CreateService(_tokenRepository, _timeProvider, intervalMinutes: 30);

            // Startup sweep: happens before any timer tick.
            await _service.StartAsync(CancellationToken.None);
            await _nextSweepObserved.Task.WaitAsync(SignalTimeout);

            // A partial interval must NOT produce a sweep: no timer tick fires, so no
            // new repository call can originate.
            _timeProvider.Advance(TimeSpan.FromMinutes(29));
            lock (_capturedBounds)
            {
                _callCountAfterPartialAdvance = _capturedBounds.Count;
            }

            // Completing the interval fires the tick and produces the second sweep.
            _nextSweepObserved = NewSignal();
            _timeProvider.Advance(TimeSpan.FromMinutes(1));
            await _nextSweepObserved.Task.WaitAsync(SignalTimeout);

            // And a further full interval produces the third.
            _nextSweepObserved = NewSignal();
            _timeProvider.Advance(TimeSpan.FromMinutes(30));
            await _nextSweepObserved.Task.WaitAsync(SignalTimeout);

            await _service.StopAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _service.Dispose();

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Test]
        public void It_sweeps_once_at_startup_with_the_skew_adjusted_bound()
        {
            _capturedBounds[0].Should().Be(StartTime - JwtTokenValidator.TokenValidationClockSkew);
        }

        [Test]
        public void It_does_not_sweep_before_the_interval_elapses()
        {
            _callCountAfterPartialAdvance.Should().Be(1);
        }

        [Test]
        public void It_sweeps_again_when_each_configured_interval_elapses()
        {
            _capturedBounds.Should().HaveCount(3);
            _capturedBounds[1]
                .Should()
                .Be(StartTime + TimeSpan.FromMinutes(30) - JwtTokenValidator.TokenValidationClockSkew);
            _capturedBounds[2]
                .Should()
                .Be(StartTime + TimeSpan.FromMinutes(60) - JwtTokenValidator.TokenValidationClockSkew);
        }
    }

    [TestFixture]
    public class Given_TheRepositoryThrowsOnTheStartupSweep
    {
        private IOpenIddictTokenRepository _tokenRepository = null!;
        private TokenCleanupService _service = null!;
        private FakeTimeProvider _timeProvider = null!;
        private int _callCount;
        private readonly TaskCompletionSource _secondSweepObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        [SetUp]
        public async Task Act()
        {
            // NUnit reuses the fixture instance across its tests; reset so every test's
            // Act starts from the throw-then-recover scenario.
            _callCount = 0;

            _timeProvider = new FakeTimeProvider(StartTime);
            _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
            A.CallTo(() => _tokenRepository.DeleteExpiredTokensAsync(A<DateTimeOffset>._))
                .ReturnsLazily(
                    (DateTimeOffset _) =>
                    {
                        int call = Interlocked.Increment(ref _callCount);
                        if (call == 1)
                        {
                            throw new InvalidOperationException("Simulated repository failure.");
                        }
                        _secondSweepObserved.TrySetResult();
                        return Task.FromResult(1);
                    }
                );

            _service = CreateService(_tokenRepository, _timeProvider);

            // The startup sweep throws inside the service; the loop must survive it and
            // keep ticking, or StartAsync would fault and the next advance would produce
            // no second call.
            await _service.StartAsync(CancellationToken.None);

            _timeProvider.Advance(TimeSpan.FromMinutes(30));
            await _secondSweepObserved.Task.WaitAsync(SignalTimeout);

            await _service.StopAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _service.Dispose();

        [Test]
        public void It_swallows_the_failure_and_sweeps_again_on_the_next_interval()
        {
            _callCount.Should().Be(2);
        }
    }

    [TestFixture]
    public class Given_TheConfiguredIntervalIsBelowOneMinute
    {
        private Exception? _startException;

        [SetUp]
        public async Task Act()
        {
            var service = CreateService(
                A.Fake<IOpenIddictTokenRepository>(),
                new FakeTimeProvider(StartTime),
                intervalMinutes: 0
            );

            try
            {
                // A PeriodicTimer constructed with a non-positive period throws
                // synchronously, which would surface here if the invalid interval were
                // not clamped to the documented default before the timer is created.
                await service.StartAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _startException = ex;
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }

        [Test]
        public void It_starts_without_throwing_by_falling_back_to_the_default_interval()
        {
            _startException.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_TheConfiguredIntervalIsAboveTheMaximum
    {
        private Exception? _startException;

        [SetUp]
        public async Task Act()
        {
            var service = CreateService(
                A.Fake<IOpenIddictTokenRepository>(),
                new FakeTimeProvider(StartTime),
                intervalMinutes: int.MaxValue
            );

            try
            {
                // A PeriodicTimer constructed with a period above uint.MaxValue - 1
                // milliseconds throws synchronously, which would fault StartAsync and stop
                // the whole host if the oversized interval were not clamped to the default.
                await service.StartAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _startException = ex;
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }

        [Test]
        public void It_starts_without_throwing_by_falling_back_to_the_default_interval()
        {
            _startException.Should().BeNull();
        }
    }
}
