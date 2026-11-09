// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Security.ConfigurationServiceTokenHandlerTests;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

/// <summary>
/// Tests verifying stampede protection: HybridCache for claim sets and application contexts, and the
/// token handler's single-flight grant for the Configuration Service bearer. Under concurrent load,
/// only one request should do the work while others wait for its result.
/// </summary>
[TestFixture]
public class StampedeProtectionTests
{
    protected static HybridCache CreateHybridCache()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddHybridCache();
        var serviceProvider = services.BuildServiceProvider();
        return serviceProvider.GetRequiredService<HybridCache>();
    }

    protected static IMemoryCache CreateMemoryCache() => new MemoryCache(new MemoryCacheOptions());

    protected static CacheSettings CreateCacheSettings() => new();

    [TestFixture]
    [Parallelizable]
    public class Given_Concurrent_ClaimSet_Requests : StampedeProtectionTests
    {
        private CachedClaimSetProvider _cachedProvider = null!;
        private IConfigurationServiceClaimSetProvider _mockProvider = null!;
        private int _factoryExecutionCount;

        [SetUp]
        public void Setup()
        {
            _factoryExecutionCount = 0;
            _mockProvider = A.Fake<IConfigurationServiceClaimSetProvider>();

            // Configure mock to track calls and add delay to simulate real fetch
            A.CallTo(() => _mockProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._))
                .ReturnsLazily(_ => FetchClaimSetsAsync());

            _cachedProvider = new CachedClaimSetProvider(
                _mockProvider,
                CreateMemoryCache(),
                CreateCacheSettings(),
                TimeProvider.System,
                NullLogger<CachedClaimSetProvider>.Instance
            );
        }

        private async Task<IList<ClaimSet>> FetchClaimSetsAsync()
        {
            Interlocked.Increment(ref _factoryExecutionCount);
            await Task.Delay(100); // Simulate network latency
            return new List<ClaimSet> { new("TestClaimSet", []) };
        }

        [Test]
        public async Task It_Should_Execute_Factory_Only_Once_Under_Concurrent_Load()
        {
            // Arrange: Create 10 concurrent requests
            var tasks = Enumerable.Range(0, 10).Select(_ => _cachedProvider.GetAllClaimSets()).ToList();

            // Act: Execute all concurrently
            var results = await Task.WhenAll(tasks);

            // Assert: Factory should execute exactly once (stampede protection)
            _factoryExecutionCount.Should().Be(1);

            // All results should have one claim set
            results.Should().AllSatisfy(r => r.Count.Should().Be(1));
        }

        [Test]
        public async Task It_Should_Return_Same_Data_To_All_Concurrent_Callers()
        {
            // Arrange
            var tasks = Enumerable.Range(0, 5).Select(_ => _cachedProvider.GetAllClaimSets()).ToList();

            // Act
            var results = await Task.WhenAll(tasks);

            // Assert: All results should be equivalent
            var firstResult = results[0];
            results.Should().AllSatisfy(r => r[0].Name.Should().Be(firstResult[0].Name));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Concurrent_ApplicationContext_Requests : StampedeProtectionTests
    {
        private CachedApplicationContextProvider _cachedProvider = null!;
        private IConfigurationServiceApplicationProvider _mockProvider = null!;
        private int _factoryExecutionCount;

        [SetUp]
        public void Setup()
        {
            _factoryExecutionCount = 0;
            _mockProvider = A.Fake<IConfigurationServiceApplicationProvider>();

            A.CallTo(() =>
                    _mockProvider.GetApplicationByClientIdAsync(
                        A<string>.Ignored,
                        A<string?>.Ignored,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(_ => FetchApplicationContextAsync());

            _cachedProvider = new CachedApplicationContextProvider(
                _mockProvider,
                CreateHybridCache(),
                CreateCacheSettings(),
                NullLogger<CachedApplicationContextProvider>.Instance
            );
        }

        private async Task<ApplicationContextResult> FetchApplicationContextAsync()
        {
            Interlocked.Increment(ref _factoryExecutionCount);
            await Task.Delay(100);
            return new ApplicationContextResult.Success(
                new ApplicationContext(1, 100, "testClient", Guid.NewGuid(), [1, 2, 3], null, [])
            );
        }

        [Test]
        public async Task It_Should_Memoize_Each_Lookup_Task_By_Client_And_Tenant_In_The_Same_Scope()
        {
            var tasks = new[]
            {
                _cachedProvider.GetApplicationByClientIdAsync("first-client", tenant: null),
                _cachedProvider.GetApplicationByClientIdAsync("second-client", "north"),
                _cachedProvider.GetApplicationByClientIdAsync("third-client", "south"),
            };

            var results = await Task.WhenAll(tasks);

            _factoryExecutionCount.Should().Be(3);
            results
                .Should()
                .AllSatisfy(result => result.Should().BeOfType<ApplicationContextResult.Success>());
            A.CallTo(() =>
                    _mockProvider.GetApplicationByClientIdAsync(
                        "first-client",
                        tenant: null,
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(() =>
                    _mockProvider.GetApplicationByClientIdAsync(
                        "second-client",
                        "north",
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(() =>
                    _mockProvider.GetApplicationByClientIdAsync(
                        "third-client",
                        "south",
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_Should_Coalesce_The_Same_Key_Across_Independent_Scopes()
        {
            HybridCache sharedHybridCache = CreateHybridCache();
            var firstScopeProvider = new CachedApplicationContextProvider(
                _mockProvider,
                sharedHybridCache,
                CreateCacheSettings(),
                NullLogger<CachedApplicationContextProvider>.Instance
            );
            var secondScopeProvider = new CachedApplicationContextProvider(
                _mockProvider,
                sharedHybridCache,
                CreateCacheSettings(),
                NullLogger<CachedApplicationContextProvider>.Instance
            );

            var tasks = new[]
            {
                firstScopeProvider.GetApplicationByClientIdAsync("client-id", tenant: null),
                secondScopeProvider.GetApplicationByClientIdAsync("client-id", tenant: null),
            };

            var results = await Task.WhenAll(tasks);

            _factoryExecutionCount.Should().Be(1);
            var applicationContexts = results
                .Select(result =>
                    result.Should().BeOfType<ApplicationContextResult.Success>().Subject.ApplicationContext
                )
                .ToArray();
            applicationContexts.Should().AllBeEquivalentTo(applicationContexts[0]);
        }

        [Test]
        public async Task It_Should_Execute_Factories_Independently_For_Distinct_Keys_Across_Scopes()
        {
            HybridCache sharedHybridCache = CreateHybridCache();
            var providers = Enumerable
                .Range(0, 3)
                .Select(_ => new CachedApplicationContextProvider(
                    _mockProvider,
                    sharedHybridCache,
                    CreateCacheSettings(),
                    NullLogger<CachedApplicationContextProvider>.Instance
                ))
                .ToArray();

            await Task.WhenAll(
                providers[0].GetApplicationByClientIdAsync("client1", tenant: null),
                providers[1].GetApplicationByClientIdAsync("client2", tenant: null),
                providers[2].GetApplicationByClientIdAsync("client3", tenant: null)
            );

            _factoryExecutionCount.Should().Be(3);
        }
    }

    /// <summary>
    /// Two waves of callers against a grant held pending by a deterministic signal. Each wave shares
    /// one grant and one result; no grant is sent before the 540-second cutoff of a 600-second token,
    /// and exactly one at it.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Concurrent_Token_Requests : StampedeProtectionTests
    {
        private const int CallersPerWave = 10;

        private ServiceProvider _provider = null!;
        private GatedTransport _transport = null!;
        private int _attemptsWhileFirstWavePending;
        private string[] _firstWave = [];
        private string _tokenJustBeforeCutoff = null!;
        private int _attemptsJustBeforeCutoff;
        private int _attemptsWhileSecondWavePending;
        private (string? Token, Exception? Failure)[] _secondWave = [];
        private int _attemptsAfterSecondWave;

        [SetUp]
        public async Task Setup()
        {
            var fakeTime = new FakeTimeProvider();
            _transport = new GatedTransport();
            _provider = BuildRegisteredProvider(
                new RecordingLoggerProvider(),
                fakeTime,
                _transport,
                _transport
            );

            Task<string>[] firstWave =
            [
                .. Enumerable.Range(0, CallersPerWave).Select(_ => RequestToken(_provider)),
            ];
            PendingRequest firstGrant = await _transport.NextAsync() ?? throw new TimeoutException();
            _attemptsWhileFirstWavePending = _transport.Count;
            firstGrant.Answer(HttpStatusCode.OK, ValidGrantBody("first-wave-token", 600));
            _firstWave = await Task.WhenAll(firstWave).WaitAsync(Failsafe);

            fakeTime.Advance(TimeSpan.FromSeconds(540) - OneTick);
            _tokenJustBeforeCutoff = await RequestToken(_provider).WaitAsync(Failsafe);
            _attemptsJustBeforeCutoff = _transport.Count;

            fakeTime.Advance(OneTick);
            Task<string>[] secondWave =
            [
                .. Enumerable.Range(0, CallersPerWave).Select(_ => RequestToken(_provider)),
            ];
            PendingRequest? secondGrant = await _transport.NextAsync();
            _attemptsWhileSecondWavePending = _transport.Count;
            secondGrant?.Answer(HttpStatusCode.OK, ValidGrantBody("second-wave-token", 600));
            _secondWave = await Task.WhenAll(secondWave.Select(CaptureResult));
            _attemptsAfterSecondWave = _transport.Count;
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_sends_one_grant_for_the_cold_wave() => _attemptsWhileFirstWavePending.Should().Be(1);

        [Test]
        public void It_shares_the_first_result_with_the_cold_wave() =>
            _firstWave
                .Should()
                .HaveCount(CallersPerWave)
                .And.OnlyContain(token => token == "first-wave-token");

        [Test]
        public void It_sends_no_grant_before_the_cutoff()
        {
            _attemptsJustBeforeCutoff.Should().Be(1);
            _tokenJustBeforeCutoff.Should().Be("first-wave-token");
        }

        [Test]
        public void It_sends_exactly_one_grant_for_the_expired_wave()
        {
            _attemptsWhileSecondWavePending.Should().Be(2);
            _attemptsAfterSecondWave.Should().Be(2);
        }

        [Test]
        public void It_shares_the_replacement_with_the_expired_wave() =>
            _secondWave
                .Select(result => result.Token)
                .Should()
                .HaveCount(CallersPerWave)
                .And.OnlyContain(token => token == "second-wave-token");
    }

    /// <summary>
    /// A caller that cancels its own wait leaves the shared grant intact for the remaining caller.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Canceled_Token_Waiter : StampedeProtectionTests
    {
        private ServiceProvider _provider = null!;
        private GatedTransport _transport = null!;
        private Exception? _canceledFailure;
        private bool _sharedGrantCanceled;
        private string? _remainingToken;

        [SetUp]
        public async Task Setup()
        {
            _transport = new GatedTransport();
            _provider = BuildRegisteredProvider(
                new RecordingLoggerProvider(),
                new FakeTimeProvider(),
                _transport,
                _transport
            );
            using var waiterCancellation = new CancellationTokenSource();

            Task<string> canceled = RequestToken(_provider, waiterCancellation.Token);
            Task<string> remaining = RequestToken(_provider);
            PendingRequest grant = await _transport.NextAsync() ?? throw new TimeoutException();

            await waiterCancellation.CancelAsync();
            _canceledFailure = await CaptureFailure(() => canceled);
            _sharedGrantCanceled = grant.CancellationToken.IsCancellationRequested;

            grant.Answer(HttpStatusCode.OK, ValidGrantBody("shared-token", 600));
            _remainingToken = (await CaptureResult(remaining)).Token;
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_ends_the_canceled_wait() =>
            _canceledFailure.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_keeps_the_shared_grant_running() => _sharedGrantCanceled.Should().BeFalse();

        [Test]
        public void It_delivers_the_result_to_the_remaining_caller() =>
            _remainingToken.Should().Be("shared-token");

        [Test]
        public void It_sends_one_grant() => _transport.Count.Should().Be(1);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Tenant_Specific_ClaimSet_Stampede : StampedeProtectionTests
    {
        private CachedClaimSetProvider _cachedProvider = null!;
        private IConfigurationServiceClaimSetProvider _mockProvider = null!;
        private int _tenant1FactoryCount;
        private int _tenant2FactoryCount;

        [SetUp]
        public void Setup()
        {
            _tenant1FactoryCount = 0;
            _tenant2FactoryCount = 0;
            _mockProvider = A.Fake<IConfigurationServiceClaimSetProvider>();

            A.CallTo(() =>
                    _mockProvider.GetAllClaimSets(
                        A<string?>.That.IsEqualTo("tenant1"),
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(_ => FetchTenant1ClaimSetsAsync());

            A.CallTo(() =>
                    _mockProvider.GetAllClaimSets(
                        A<string?>.That.IsEqualTo("tenant2"),
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(_ => FetchTenant2ClaimSetsAsync());

            _cachedProvider = new CachedClaimSetProvider(
                _mockProvider,
                CreateMemoryCache(),
                CreateCacheSettings(),
                TimeProvider.System,
                NullLogger<CachedClaimSetProvider>.Instance
            );
        }

        private async Task<IList<ClaimSet>> FetchTenant1ClaimSetsAsync()
        {
            Interlocked.Increment(ref _tenant1FactoryCount);
            await Task.Delay(100);
            return new List<ClaimSet> { new("Tenant1ClaimSet", []) };
        }

        private async Task<IList<ClaimSet>> FetchTenant2ClaimSetsAsync()
        {
            Interlocked.Increment(ref _tenant2FactoryCount);
            await Task.Delay(100);
            return new List<ClaimSet> { new("Tenant2ClaimSet", []) };
        }

        [Test]
        public async Task It_Should_Execute_Factory_Once_Per_Tenant_Under_Concurrent_Load()
        {
            // Arrange: Create concurrent requests for both tenants
            var tenant1Tasks = Enumerable
                .Range(0, 5)
                .Select(_ => _cachedProvider.GetAllClaimSets("tenant1"))
                .ToList();

            var tenant2Tasks = Enumerable
                .Range(0, 5)
                .Select(_ => _cachedProvider.GetAllClaimSets("tenant2"))
                .ToList();

            // Act: Execute all concurrently
            var allTasks = tenant1Tasks.Concat(tenant2Tasks);
            await Task.WhenAll(allTasks);

            // Assert: Each tenant's factory should execute only once
            _tenant1FactoryCount.Should().Be(1);
            _tenant2FactoryCount.Should().Be(1);
        }
    }
}
