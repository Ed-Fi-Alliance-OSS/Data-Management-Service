// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

[TestFixture]
public class CachedApplicationContextProviderTests
{
    private IConfigurationServiceApplicationProvider _configurationServiceApplicationProvider = null!;
    private HybridCache _hybridCache = null!;
    private CachedApplicationContextProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _configurationServiceApplicationProvider = A.Fake<IConfigurationServiceApplicationProvider>();
        _hybridCache = CreateHybridCache();
        _provider = CreateProvider();
    }

    [Test]
    public async Task It_Uses_The_Exact_Single_Tenant_Cache_Key()
    {
        var expectedContext = CreateApplicationContext("client-id", 1);
        await _hybridCache.SetAsync("ApplicationContext:single:client-id", expectedContext);

        ApplicationContextResult result = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeEquivalentTo(new ApplicationContextResult.Success(expectedContext));
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    A<string>.Ignored,
                    A<string?>.Ignored,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_Uses_The_Exact_Normalized_Tenant_Cache_Key()
    {
        var expectedContext = CreateApplicationContext("client-id", 2);
        await _hybridCache.SetAsync("ApplicationContext:tenant:districta:client-id", expectedContext);

        ApplicationContextResult result = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            "DistrictA"
        );

        result.Should().BeEquivalentTo(new ApplicationContextResult.Success(expectedContext));
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    A<string>.Ignored,
                    A<string?>.Ignored,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_Keeps_The_Same_Client_Isolated_Between_Tenants()
    {
        var northContext = CreateApplicationContext("client-id", 1);
        var southContext = CreateApplicationContext("client-id", 2);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    "north",
                    A<CancellationToken>._
                )
            )
            .Returns(new ApplicationContextResult.Success(northContext));
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    "south",
                    A<CancellationToken>._
                )
            )
            .Returns(new ApplicationContextResult.Success(southContext));

        ApplicationContextResult north = await _provider.GetApplicationByClientIdAsync("client-id", "north");
        ApplicationContextResult south = await CreateProvider()
            .GetApplicationByClientIdAsync("client-id", "south");

        north.Should().BeEquivalentTo(new ApplicationContextResult.Success(northContext));
        south.Should().BeEquivalentTo(new ApplicationContextResult.Success(southContext));
    }

    [Test]
    public async Task It_Normalizes_Tenant_Case_While_Preserving_The_Original_Tenant_For_Cms()
    {
        var expectedContext = CreateApplicationContext("client-id", 1);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    "DistrictA",
                    A<CancellationToken>._
                )
            )
            .Returns(new ApplicationContextResult.Success(expectedContext));

        await _provider.GetApplicationByClientIdAsync("client-id", "DistrictA");
        var secondScopeProvider = CreateProvider();
        ApplicationContextResult result = await secondScopeProvider.GetApplicationByClientIdAsync(
            "client-id",
            "districta"
        );

        result.Should().BeEquivalentTo(new ApplicationContextResult.Success(expectedContext));
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    A<string?>.Ignored,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_Performs_One_Normal_Lookup_On_A_Cold_NotFound_Without_Reloading()
    {
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .Returns(new ApplicationContextResult.NotFound());

        ApplicationContextResult result = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeOfType<ApplicationContextResult.NotFound>();
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                    A<string>.Ignored,
                    A<string?>.Ignored,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [TestCase(ApplicationContextOutcome.Success)]
    [TestCase(ApplicationContextOutcome.NotFound)]
    [TestCase(ApplicationContextOutcome.Unavailable)]
    public async Task It_Memoizes_The_First_Outcome_For_The_Request(ApplicationContextOutcome outcome)
    {
        ApplicationContextResult expectedResult = CreateResult(outcome);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .Returns(expectedResult);

        ApplicationContextResult first = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );
        ApplicationContextResult second = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        second.Should().BeSameAs(first);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [TestCase(ApplicationContextOutcome.NotFound)]
    [TestCase(ApplicationContextOutcome.Unavailable)]
    public async Task It_Does_Not_Admit_Failed_Results_To_The_Shared_Cache(ApplicationContextOutcome outcome)
    {
        var expectedContext = CreateApplicationContext("client-id", 1);
        var lookupCount = 0;
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(_ =>
            {
                lookupCount++;
                return Task.FromResult<ApplicationContextResult>(
                    lookupCount == 1
                        ? CreateResult(outcome)
                        : new ApplicationContextResult.Success(expectedContext)
                );
            });

        ApplicationContextResult failed = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );
        ApplicationContextResult recovered = await CreateProvider()
            .GetApplicationByClientIdAsync("client-id", tenant: null);

        failed
            .GetType()
            .Should()
            .Be(
                outcome switch
                {
                    ApplicationContextOutcome.NotFound => typeof(ApplicationContextResult.NotFound),
                    ApplicationContextOutcome.Unavailable => typeof(ApplicationContextResult.Unavailable),
                    _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
                }
            );
        recovered.Should().BeEquivalentTo(new ApplicationContextResult.Success(expectedContext));
        lookupCount.Should().Be(2);
    }

    [Test]
    public async Task It_Serves_A_Warm_Success_Cache_When_Cms_Is_Unavailable()
    {
        var expectedContext = CreateApplicationContext("client-id", 1);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .Returns(new ApplicationContextResult.Success(expectedContext));

        await _provider.GetApplicationByClientIdAsync("client-id", tenant: null);
        var outageScopeProvider = CreateProvider();
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .Returns(new ApplicationContextResult.Unavailable());

        ApplicationContextResult result = await outageScopeProvider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        result.Should().BeEquivalentTo(new ApplicationContextResult.Success(expectedContext));
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_Reloads_Only_The_Matching_Normalized_Tenant_Key()
    {
        var staleNorthContext = CreateApplicationContext("client-id", 1);
        var southContext = CreateApplicationContext("client-id", 2);
        var refreshedNorthContext = CreateApplicationContext("client-id", 3);
        await _hybridCache.SetAsync("ApplicationContext:tenant:north:client-id", staleNorthContext);
        await _hybridCache.SetAsync("ApplicationContext:tenant:south:client-id", southContext);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                    "client-id",
                    "North",
                    A<CancellationToken>._
                )
            )
            .Returns(new ApplicationContextResult.Success(refreshedNorthContext));

        ApplicationContextResult reloadResult = await _provider.ReloadApplicationByClientIdAsync(
            "client-id",
            "North"
        );
        ApplicationContextResult north = await CreateProvider()
            .GetApplicationByClientIdAsync("client-id", "north");
        ApplicationContextResult south = await CreateProvider()
            .GetApplicationByClientIdAsync("client-id", "south");

        reloadResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(refreshedNorthContext));
        north.Should().BeEquivalentTo(new ApplicationContextResult.Success(refreshedNorthContext));
        south.Should().BeEquivalentTo(new ApplicationContextResult.Success(southContext));
        A.CallTo(() =>
                _configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                    "client-id",
                    "North",
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    A<string>.Ignored,
                    A<string?>.Ignored,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_Returns_A_Typed_NotFound_Result_For_A_Blank_Client_Without_Caching()
    {
        ApplicationContextResult result = await _provider.GetApplicationByClientIdAsync(" ", tenant: null);

        result.Should().BeOfType<ApplicationContextResult.NotFound>();
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    A<string>.Ignored,
                    A<string?>.Ignored,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_Invalidates_The_Request_Scoped_Memo_On_Reload()
    {
        var reloadedContext = CreateApplicationContext("client-id", 9);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .Returns(new ApplicationContextResult.NotFound());
        A.CallTo(() =>
                _configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .Returns(new ApplicationContextResult.Success(reloadedContext));

        ApplicationContextResult beforeReload = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );
        ApplicationContextResult reloadResult = await _provider.ReloadApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );
        ApplicationContextResult afterReload = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        beforeReload.Should().BeOfType<ApplicationContextResult.NotFound>();
        reloadResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(reloadedContext));
        afterReload.Should().BeEquivalentTo(new ApplicationContextResult.Success(reloadedContext));
    }

    [Test]
    public async Task It_Preserves_Ownership_Tokens_Through_The_Cache()
    {
        ApplicationContext expectedContext = CreateApplicationContext("client-id", 1) with
        {
            CreatorOwnershipTokenId = 303,
            OwnershipTokenIds = [202, 404],
        };
        await _hybridCache.SetAsync("ApplicationContext:single:client-id", expectedContext);

        ApplicationContextResult result = await _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        var success = result.Should().BeOfType<ApplicationContextResult.Success>().Subject;
        success.ApplicationContext.CreatorOwnershipTokenId.Should().Be(303);
        success.ApplicationContext.OwnershipTokenIds.Should().Equal((short)202, (short)404);
    }

    [Test]
    public async Task It_Does_Not_Cancel_A_Fill_Another_Caller_Still_Needs()
    {
        var gate = new TaskCompletionSource();
        var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedContext = CreateApplicationContext("client-id", 1);
        int fetchCount = 0;

        async Task<ApplicationContextResult> FetchAsync()
        {
            Interlocked.Increment(ref fetchCount);
            fetchStarted.TrySetResult();
            await gate.Task;
            return new ApplicationContextResult.Success(expectedContext);
        }

        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(_ => FetchAsync());

        using var cancelledCallerCts = new CancellationTokenSource();
        Task<ApplicationContextResult> cancelledCallerTask = _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null,
            cancelledCallerCts.Token
        );
        await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Joining is decided synchronously inside the call, before its first await, so the live
        // caller is registered on the fill by the time this line returns.
        Task<ApplicationContextResult> liveCallerTask = _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );

        await cancelledCallerCts.CancelAsync();

        Func<Task> act = async () => await cancelledCallerTask;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // The live caller's fill is still in flight - the cancelled caller leaving did not abort it.
        liveCallerTask.IsCompleted.Should().BeFalse();

        gate.SetResult();

        ApplicationContextResult liveResult = await liveCallerTask;
        liveResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(expectedContext));
        fetchCount.Should().Be(1);
    }

    [Test]
    public async Task It_Does_Not_Memoize_A_Cancelled_Callers_Result_For_A_Later_Caller_In_The_Same_Scope()
    {
        // Measured against the real HybridCache: a caller's cancellation does not cancel the factory's
        // token, and a later call for the same key may either start a fresh factory or join the
        // abandoned stampede that is still in flight. So this test holds the abandoned fetch open only
        // until the later caller has been issued, then releases it, and asserts the property that does
        // not depend on which path HybridCache took: the later caller gets a Success and never the
        // abandoned caller's cancellation. The synchronous join decision itself is proven by
        // It_Never_Hands_A_Later_Caller_An_Abandoned_Fill_That_Is_Still_In_Flight.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandonedContext = CreateApplicationContext("client-id", 2);
        var recoveredContext = CreateApplicationContext("client-id", 3);
        int fetchCount = 0;

        async Task<ApplicationContextResult> FetchAsync()
        {
            int call = Interlocked.Increment(ref fetchCount);
            if (call == 1)
            {
                firstFetchStarted.TrySetResult();
                await gate.Task;
                return new ApplicationContextResult.Success(abandonedContext);
            }
            return new ApplicationContextResult.Success(recoveredContext);
        }

        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(_ => FetchAsync());

        using var cancelledCallerCts = new CancellationTokenSource();
        Task<ApplicationContextResult> cancelledCallerTask = _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null,
            cancelledCallerCts.Token
        );
        await firstFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancelledCallerCts.CancelAsync();

        Func<Task> act = async () => await cancelledCallerTask;
        await act.Should().ThrowAsync<OperationCanceledException>();

        Task<ApplicationContextResult> laterCallerTask = _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null
        );
        gate.SetResult();

        ApplicationContextResult laterResult = await laterCallerTask.WaitAsync(TimeSpan.FromSeconds(10));
        // HybridCache hands back a deserialized copy, so compare on the distinguishing Id, not by reference.
        var success = laterResult.Should().BeOfType<ApplicationContextResult.Success>().Subject;
        success.ApplicationContext.Id.Should().BeOneOf(abandonedContext.Id, recoveredContext.Id);
        fetchCount.Should().BeInRange(1, 2);
    }

    [Test]
    public async Task It_Does_Not_Cache_A_Cancelled_Fetch_In_The_Shared_Cache()
    {
        // A fetch whose only caller cancelled must not be admitted to the durable cache. HybridCache
        // may still let a brand-new scope join that fetch while it is in flight, so the assertion is
        // made once the abandoned fetch has finished: from then on, a fresh scope must miss and fetch
        // again, bounded by a generous retry window rather than a fixed delay.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFetchFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandonedContext = CreateApplicationContext("client-id", 4);
        var recoveredContext = CreateApplicationContext("client-id", 5);
        int fetchCount = 0;

        async Task<ApplicationContextResult> FetchAsync()
        {
            int call = Interlocked.Increment(ref fetchCount);
            if (call == 1)
            {
                firstFetchStarted.TrySetResult();
                await gate.Task;
                firstFetchFinished.TrySetResult();
                return new ApplicationContextResult.Success(abandonedContext);
            }
            return new ApplicationContextResult.Success(recoveredContext);
        }

        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(_ => FetchAsync());

        using var cancelledCallerCts = new CancellationTokenSource();
        Task<ApplicationContextResult> cancelledCallerTask = _provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null,
            cancelledCallerCts.Token
        );
        await firstFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancelledCallerCts.CancelAsync();

        Func<Task> act = async () => await cancelledCallerTask;
        await act.Should().ThrowAsync<OperationCanceledException>();

        gate.SetResult();
        await firstFetchFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Once the abandoned fetch has completed, a fresh scope must not find its value in the shared
        // cache. A fresh call that still lands on the in-flight stampede returns the abandoned
        // context; retry until HybridCache has torn that stampede down and the miss is observable.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        ApplicationContextResult freshScopeResult;
        do
        {
            freshScopeResult = await CreateProvider()
                .GetApplicationByClientIdAsync("client-id", tenant: null);
            if (
                freshScopeResult is ApplicationContextResult.Success { ApplicationContext: var context }
                && context.Id == recoveredContext.Id
            )
            {
                break;
            }
            await Task.Yield();
        } while (DateTime.UtcNow < deadline);

        freshScopeResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(recoveredContext));
    }

    [Test]
    public async Task It_Never_Hands_A_Later_Caller_An_Abandoned_Fill_That_Is_Still_In_Flight()
    {
        // The real HybridCache completes an abandoned fill promptly, which hides the window between
        // "last waiter left" and "fill observed as finished". This cache keeps the first fill open
        // regardless of its token, so the window is held wide: the later caller must decide not to
        // join at the moment it arrives, not after a continuation on the abandoned task has run.
        var heldOpenCache = new HeldOpenHybridCache();
        var provider = new CachedApplicationContextProvider(
            _configurationServiceApplicationProvider,
            heldOpenCache,
            new CacheSettings { ApplicationContextCacheExpirationSeconds = 123 },
            NullLogger<CachedApplicationContextProvider>.Instance
        );
        var recoveredContext = CreateApplicationContext("client-id", 4);
        A.CallTo(() =>
                _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                    "client-id",
                    tenant: null,
                    A<CancellationToken>._
                )
            )
            .Returns(new ApplicationContextResult.Success(recoveredContext));

        using var cancelledCallerCts = new CancellationTokenSource();
        Task<ApplicationContextResult> cancelledCallerTask = provider.GetApplicationByClientIdAsync(
            "client-id",
            tenant: null,
            cancelledCallerCts.Token
        );
        await heldOpenCache.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancelledCallerCts.CancelAsync();

        Func<Task> act = async () => await cancelledCallerTask;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // The abandoned fill's token is cancelled but the cache is still holding its task open.
        heldOpenCache.FirstCallToken.IsCancellationRequested.Should().BeTrue();

        ApplicationContextResult laterResult = await provider
            .GetApplicationByClientIdAsync("client-id", tenant: null)
            .WaitAsync(TimeSpan.FromSeconds(5));

        laterResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(recoveredContext));
        heldOpenCache.CallCount.Should().Be(2);

        heldOpenCache.ReleaseFirstCall();
        provider.Dispose();
    }

    /// <summary>
    /// A HybridCache whose first GetOrCreateAsync never completes until released, ignoring its token,
    /// and whose later calls run the factory immediately. It exists only to hold the abandoned-fill
    /// window open deterministically.
    /// </summary>
    private sealed class HeldOpenHybridCache : HybridCache
    {
        private readonly TaskCompletionSource _firstCallReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _callCount;

        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken FirstCallToken { get; private set; }
        public int CallCount => _callCount;

        public void ReleaseFirstCall() => _firstCallReleased.TrySetResult();

        public override async ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        )
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                FirstCallToken = cancellationToken;
                FirstCallStarted.TrySetResult();
                await _firstCallReleased.Task;
            }

            return await factory(state, cancellationToken);
        }

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(
            string tag,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;
    }

    private CachedApplicationContextProvider CreateProvider() =>
        new(
            _configurationServiceApplicationProvider,
            _hybridCache,
            new CacheSettings { ApplicationContextCacheExpirationSeconds = 123 },
            NullLogger<CachedApplicationContextProvider>.Instance
        );

    private static HybridCache CreateHybridCache()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    private static ApplicationContextResult CreateResult(ApplicationContextOutcome outcome) =>
        outcome switch
        {
            ApplicationContextOutcome.Success => new ApplicationContextResult.Success(
                CreateApplicationContext("client-id", 1)
            ),
            ApplicationContextOutcome.NotFound => new ApplicationContextResult.NotFound(),
            ApplicationContextOutcome.Unavailable => new ApplicationContextResult.Unavailable(),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };

    private static ApplicationContext CreateApplicationContext(string clientId, long applicationId) =>
        new(applicationId, 100, clientId, Guid.NewGuid(), [1, 2, 3], null, []);

    public enum ApplicationContextOutcome
    {
        Success,
        NotFound,
        Unavailable,
    }
}
