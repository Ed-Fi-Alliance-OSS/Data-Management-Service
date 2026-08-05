// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// TokenCleanupService loops on a PeriodicTimer whose period is the configured
/// (clamped-to-at-least-one-minute) interval; there is no injectable clock/timer seam.
/// Waiting for a real timer tick would make these tests take a minute or more, so the
/// "sweep runs" and "resilience" fixtures below invoke the service's private RunSweepAsync
/// method directly via reflection -- the same method the timer loop calls on every tick --
/// rather than waiting on the real PeriodicTimer. The PeriodicTimer scheduling/cadence
/// itself is therefore not covered by a fast unit test; the disabled-flag and
/// interval-clamping fixtures do exercise the real ExecuteAsync/StartAsync/StopAsync path,
/// since those branches complete (or fault) before any timer tick is required.
/// </summary>
[TestFixture]
public class TokenCleanupServiceTests
{
    private static readonly MethodInfo RunSweepAsyncMethod =
        typeof(TokenCleanupService).GetMethod("RunSweepAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "TokenCleanupService.RunSweepAsync was not found; has it been renamed?"
        );

    private static Task InvokeRunSweepAsync(TokenCleanupService service) =>
        (Task)RunSweepAsyncMethod.Invoke(service, null)!;

    [TestFixture]
    public class Given_TokenCleanupIsDisabled
    {
        private IOpenIddictTokenRepository _tokenRepository = null!;

        [SetUp]
        public async Task Act()
        {
            _tokenRepository = A.Fake<IOpenIddictTokenRepository>();

            var service = new TokenCleanupService(
                Options.Create(new IdentityOptions { TokenCleanupEnabled = false }),
                NullLogger<TokenCleanupService>.Instance,
                _tokenRepository
            );

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
    public class Given_TokenCleanupIsEnabled_AndASweepRuns
    {
        private IOpenIddictTokenRepository _tokenRepository = null!;
        private DateTimeOffset _capturedExpiredBefore;

        [SetUp]
        public async Task Act()
        {
            _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
            A.CallTo(() => _tokenRepository.DeleteExpiredTokensAsync(A<DateTimeOffset>._))
                .Invokes((DateTimeOffset expiredBefore) => _capturedExpiredBefore = expiredBefore)
                .Returns(3);

            var service = new TokenCleanupService(
                Options.Create(
                    new IdentityOptions { TokenCleanupEnabled = true, TokenCleanupIntervalMinutes = 1 }
                ),
                NullLogger<TokenCleanupService>.Instance,
                _tokenRepository
            );

            await InvokeRunSweepAsync(service);
        }

        [Test]
        public void It_calls_the_repository_exactly_once()
        {
            A.CallTo(() => _tokenRepository.DeleteExpiredTokensAsync(A<DateTimeOffset>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_passes_a_timestamp_within_a_sane_window_of_utc_now()
        {
            _capturedExpiredBefore.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        }
    }

    [TestFixture]
    public class Given_TheRepositoryThrowsOnTheFirstSweep
    {
        private IOpenIddictTokenRepository _tokenRepository = null!;
        private TokenCleanupService _service = null!;
        private int _callCount;
        private Exception? _caughtException;

        [SetUp]
        public async Task Act()
        {
            _tokenRepository = A.Fake<IOpenIddictTokenRepository>();
            A.CallTo(() => _tokenRepository.DeleteExpiredTokensAsync(A<DateTimeOffset>._))
                .ReturnsLazily(
                    (DateTimeOffset _) =>
                    {
                        _callCount++;
                        if (_callCount == 1)
                        {
                            throw new InvalidOperationException("Simulated repository failure.");
                        }
                        return Task.FromResult(1);
                    }
                );

            _service = new TokenCleanupService(
                Options.Create(
                    new IdentityOptions { TokenCleanupEnabled = true, TokenCleanupIntervalMinutes = 1 }
                ),
                NullLogger<TokenCleanupService>.Instance,
                _tokenRepository
            );

            try
            {
                // First sweep: the repository throws. RunSweepAsync must swallow it.
                await InvokeRunSweepAsync(_service);
                // Second sweep on the same instance proves the failure did not leave the
                // service unable to sweep again.
                await InvokeRunSweepAsync(_service);
            }
            catch (Exception ex)
            {
                _caughtException = ex;
            }
        }

        [TearDown]
        public void TearDown() => _service.Dispose();

        [Test]
        public void It_does_not_let_the_repository_exception_propagate()
        {
            _caughtException.Should().BeNull();
        }

        [Test]
        public void It_still_attempts_a_second_sweep()
        {
            A.CallTo(() => _tokenRepository.DeleteExpiredTokensAsync(A<DateTimeOffset>._))
                .MustHaveHappened(2, Times.Exactly);
        }
    }

    [TestFixture]
    public class Given_TheConfiguredIntervalIsBelowOneMinute
    {
        private Exception? _startException;

        [SetUp]
        public async Task Act()
        {
            var tokenRepository = A.Fake<IOpenIddictTokenRepository>();

            var service = new TokenCleanupService(
                Options.Create(
                    new IdentityOptions { TokenCleanupEnabled = true, TokenCleanupIntervalMinutes = 0 }
                ),
                NullLogger<TokenCleanupService>.Instance,
                tokenRepository
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
}
