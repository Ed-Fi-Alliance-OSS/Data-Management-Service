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

    [TestFixture]
    public class Given_A_Cancelled_Caller_Sharing_A_Fill_With_A_Live_Caller
    {
        private int _fetchCount;
        private ApplicationContext _expectedContext = null!;
        private Exception? _cancelledCallerException;
        private bool _liveCallerCompletedWhileCancelledCallerWasLeaving;
        private ApplicationContextResult _liveResult = null!;

        [SetUp]
        public async Task Setup()
        {
            var configurationServiceApplicationProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            var provider = new CachedApplicationContextProvider(
                configurationServiceApplicationProvider,
                CreateHybridCache(),
                new CacheSettings { ApplicationContextCacheExpirationSeconds = 123 },
                NullLogger<CachedApplicationContextProvider>.Instance
            );
            var gate = new TaskCompletionSource();
            var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _expectedContext = CreateApplicationContext("client-id", 1);
            // NUnit reuses one fixture instance across every [Test] method in it, running SetUp again
            // before each; a field this method only increments, rather than fully reassigning, would
            // otherwise carry a count over from whichever test ran before it.
            _fetchCount = 0;

            async Task<ApplicationContextResult> FetchAsync()
            {
                Interlocked.Increment(ref _fetchCount);
                fetchStarted.TrySetResult();
                await gate.Task;
                return new ApplicationContextResult.Success(_expectedContext);
            }

            A.CallTo(() =>
                    configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                        "client-id",
                        tenant: null,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(_ => FetchAsync());

            using var cancelledCallerCts = new CancellationTokenSource();
            Task<ApplicationContextResult> cancelledCallerTask = provider.GetApplicationByClientIdAsync(
                "client-id",
                tenant: null,
                cancelledCallerCts.Token
            );
            await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Joining is decided synchronously inside the call, before its first await, so the live
            // caller is registered on the fill by the time this line returns.
            Task<ApplicationContextResult> liveCallerTask = provider.GetApplicationByClientIdAsync(
                "client-id",
                tenant: null
            );

            await cancelledCallerCts.CancelAsync();

            try
            {
                await cancelledCallerTask;
            }
            catch (Exception ex)
            {
                _cancelledCallerException = ex;
            }

            // The live caller's fill must still be in flight here: the cancelled caller leaving must
            // not have aborted it.
            _liveCallerCompletedWhileCancelledCallerWasLeaving = liveCallerTask.IsCompleted;

            gate.SetResult();

            _liveResult = await liveCallerTask;
        }

        [Test]
        public void It_Throws_Operation_Canceled_For_The_Cancelled_Caller()
        {
            _cancelledCallerException.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_Does_Not_Complete_The_Live_Callers_Fill_When_The_Cancelled_Caller_Leaves()
        {
            _liveCallerCompletedWhileCancelledCallerWasLeaving.Should().BeFalse();
        }

        [Test]
        public void It_Returns_The_Live_Callers_Result()
        {
            _liveResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(_expectedContext));
        }

        [Test]
        public void It_Fetches_From_The_Provider_Exactly_Once()
        {
            _fetchCount.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_A_Cancelled_Caller_And_A_Later_Caller_For_The_Same_Key
    {
        private Exception? _cancelledCallerException;
        private ApplicationContextResult _laterResult = null!;
        private long _abandonedContextId;
        private long _recoveredContextId;
        private int _fetchCount;

        [SetUp]
        public async Task Setup()
        {
            // Measured against the real HybridCache: a caller's cancellation does not cancel the
            // factory's token, and a later call for the same key may either start a fresh factory or
            // join the abandoned stampede that is still in flight. So this test holds the abandoned
            // fetch open only until the later caller has been issued, then releases it, and asserts
            // the property that does not depend on which path HybridCache took: the later caller gets
            // a Success and never the abandoned caller's cancellation. The synchronous join decision
            // itself is proven by
            // Given_A_Later_Caller_Arriving_While_An_Abandoned_Fill_Is_Still_In_Flight.
            var configurationServiceApplicationProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            var provider = new CachedApplicationContextProvider(
                configurationServiceApplicationProvider,
                CreateHybridCache(),
                new CacheSettings { ApplicationContextCacheExpirationSeconds = 123 },
                NullLogger<CachedApplicationContextProvider>.Instance
            );
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstFetchStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var abandonedContext = CreateApplicationContext("client-id", 2);
            var recoveredContext = CreateApplicationContext("client-id", 3);
            _abandonedContextId = abandonedContext.Id;
            _recoveredContextId = recoveredContext.Id;
            // NUnit reuses one fixture instance across every [Test] method in it, running SetUp again
            // before each; a field this method only increments, rather than fully reassigning, would
            // otherwise carry a count over from whichever test ran before it.
            _fetchCount = 0;

            async Task<ApplicationContextResult> FetchAsync()
            {
                int call = Interlocked.Increment(ref _fetchCount);
                if (call == 1)
                {
                    firstFetchStarted.TrySetResult();
                    await gate.Task;
                    return new ApplicationContextResult.Success(abandonedContext);
                }
                return new ApplicationContextResult.Success(recoveredContext);
            }

            A.CallTo(() =>
                    configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                        "client-id",
                        tenant: null,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(_ => FetchAsync());

            using var cancelledCallerCts = new CancellationTokenSource();
            Task<ApplicationContextResult> cancelledCallerTask = provider.GetApplicationByClientIdAsync(
                "client-id",
                tenant: null,
                cancelledCallerCts.Token
            );
            await firstFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancelledCallerCts.CancelAsync();

            try
            {
                await cancelledCallerTask;
            }
            catch (Exception ex)
            {
                _cancelledCallerException = ex;
            }

            Task<ApplicationContextResult> laterCallerTask = provider.GetApplicationByClientIdAsync(
                "client-id",
                tenant: null
            );
            gate.SetResult();

            _laterResult = await laterCallerTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Test]
        public void It_Throws_Operation_Canceled_For_The_Cancelled_Caller()
        {
            _cancelledCallerException.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_Returns_A_Successful_Result_For_The_Later_Caller()
        {
            _laterResult.Should().BeOfType<ApplicationContextResult.Success>();
        }

        [Test]
        public void It_Returns_Either_The_Abandoned_Or_Recovered_Context_For_The_Later_Caller()
        {
            // HybridCache hands back a deserialized copy, so compare on the distinguishing Id, not by
            // reference.
            var success = _laterResult.Should().BeOfType<ApplicationContextResult.Success>().Subject;
            success.ApplicationContext.Id.Should().BeOneOf(_abandonedContextId, _recoveredContextId);
        }

        [Test]
        public void It_Fetches_At_Most_Twice()
        {
            _fetchCount.Should().BeInRange(1, 2);
        }
    }

    [TestFixture]
    public class Given_The_Only_Caller_Of_A_Fill_Cancels_Before_The_Fetch_Completes
    {
        private Exception? _cancelledCallerException;
        private HeldOpenHybridCache _heldOpenCache = null!;
        private CachedApplicationContextProvider _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            // Whether the shared cache keeps a factory result that no live caller is waiting for is
            // the cache's own behavior. What this provider owns is the token it hands the cache: when
            // the fill's only caller leaves, that token must be cancelled, so the cache sees an
            // abandoned request rather than a live one it would complete and store. The held-open
            // cache keeps the fetch in flight so the check does not race the fetch finishing.
            var configurationServiceApplicationProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            A.CallTo(() =>
                    configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                        "client-id",
                        tenant: null,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ApplicationContextResult.Success(CreateApplicationContext("client-id", 4)));

            _heldOpenCache = new HeldOpenHybridCache();
            _provider = new CachedApplicationContextProvider(
                configurationServiceApplicationProvider,
                _heldOpenCache,
                new CacheSettings { ApplicationContextCacheExpirationSeconds = 123 },
                NullLogger<CachedApplicationContextProvider>.Instance
            );

            using var cancelledCallerCts = new CancellationTokenSource();
            Task<ApplicationContextResult> cancelledCallerTask = _provider.GetApplicationByClientIdAsync(
                "client-id",
                tenant: null,
                cancelledCallerCts.Token
            );
            await _heldOpenCache.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancelledCallerCts.CancelAsync();

            try
            {
                await cancelledCallerTask;
            }
            catch (Exception ex)
            {
                _cancelledCallerException = ex;
            }
        }

        [TearDown]
        public void TearDown()
        {
            _heldOpenCache.ReleaseFirstCall();
            _provider.Dispose();
        }

        [Test]
        public void It_Throws_Operation_Canceled_For_The_Cancelled_Caller()
        {
            _cancelledCallerException.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_Cancels_The_Token_Handed_To_The_Shared_Cache()
        {
            _heldOpenCache.FirstCallToken.IsCancellationRequested.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_A_Later_Caller_Arriving_While_An_Abandoned_Fill_Is_Still_In_Flight
    {
        private HeldOpenHybridCache _heldOpenCache = null!;
        private Exception? _cancelledCallerException;
        private bool _abandonedFillTokenCancelledAfterCallerLeaves;
        private ApplicationContextResult _laterResult = null!;
        private ApplicationContext _recoveredContext = null!;
        private bool _evictedFillTokenThrowsObjectDisposedAfterProviderDispose;

        [SetUp]
        public async Task Setup()
        {
            // The real HybridCache completes an abandoned fill promptly, which hides the window
            // between "last waiter left" and "fill observed as finished". This cache keeps the first
            // fill open regardless of its token, so the window is held wide: the later caller must
            // decide not to join at the moment it arrives, not after a continuation on the abandoned
            // task has run.
            var configurationServiceApplicationProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            _heldOpenCache = new HeldOpenHybridCache();
            var provider = new CachedApplicationContextProvider(
                configurationServiceApplicationProvider,
                _heldOpenCache,
                new CacheSettings { ApplicationContextCacheExpirationSeconds = 123 },
                NullLogger<CachedApplicationContextProvider>.Instance
            );
            _recoveredContext = CreateApplicationContext("client-id", 4);
            A.CallTo(() =>
                    configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                        "client-id",
                        tenant: null,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ApplicationContextResult.Success(_recoveredContext));

            using var cancelledCallerCts = new CancellationTokenSource();
            Task<ApplicationContextResult> cancelledCallerTask = provider.GetApplicationByClientIdAsync(
                "client-id",
                tenant: null,
                cancelledCallerCts.Token
            );
            await _heldOpenCache.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancelledCallerCts.CancelAsync();

            try
            {
                await cancelledCallerTask;
            }
            catch (Exception ex)
            {
                _cancelledCallerException = ex;
            }

            // The abandoned fill's token is cancelled but the cache is still holding its task open.
            _abandonedFillTokenCancelledAfterCallerLeaves = _heldOpenCache
                .FirstCallToken
                .IsCancellationRequested;
            CancellationToken evictedFillToken = _heldOpenCache.FirstCallToken;

            _laterResult = await provider
                .GetApplicationByClientIdAsync("client-id", tenant: null)
                .WaitAsync(TimeSpan.FromSeconds(5));

            provider.Dispose();

            try
            {
                // Accessing WaitHandle throws ObjectDisposedException once the token's own
                // CancellationTokenSource has been disposed; it is the cheapest way to observe that
                // disposal from outside the provider.
                _ = evictedFillToken.WaitHandle;
                _evictedFillTokenThrowsObjectDisposedAfterProviderDispose = false;
            }
            catch (ObjectDisposedException)
            {
                _evictedFillTokenThrowsObjectDisposedAfterProviderDispose = true;
            }

            _heldOpenCache.ReleaseFirstCall();
        }

        [Test]
        public void It_Throws_Operation_Canceled_For_The_Cancelled_Caller()
        {
            _cancelledCallerException.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_Cancels_The_Abandoned_Fills_Token()
        {
            _abandonedFillTokenCancelledAfterCallerLeaves.Should().BeTrue();
        }

        [Test]
        public void It_Fetches_Again_For_The_Later_Caller()
        {
            _heldOpenCache.CallCount.Should().Be(2);
        }

        [Test]
        public void It_Returns_The_Later_Callers_Own_Result()
        {
            _laterResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(_recoveredContext));
        }

        [Test]
        public void It_Disposes_The_Evicted_Fills_Cancellation_Token_On_Provider_Dispose()
        {
            _evictedFillTokenThrowsObjectDisposedAfterProviderDispose.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_A_Reload_While_A_Get_Fill_Is_In_Flight_For_The_Same_Key
    {
        private IConfigurationServiceApplicationProvider _configurationServiceApplicationProvider = null!;
        private ApplicationContextResult _reloadResult = null!;
        private ApplicationContext _reloadedContext = null!;

        [SetUp]
        public async Task Setup()
        {
            _configurationServiceApplicationProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            var cache = new RemoveGatedHybridCache();
            var provider = new CachedApplicationContextProvider(
                _configurationServiceApplicationProvider,
                cache,
                new CacheSettings { ApplicationContextCacheExpirationSeconds = 123 },
                NullLogger<CachedApplicationContextProvider>.Instance
            );

            var getGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var getStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var getContext = CreateApplicationContext("client-id", 1);
            _reloadedContext = CreateApplicationContext("client-id", 2);

            A.CallTo(() =>
                    _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                        "client-id",
                        tenant: null,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(async _ =>
                {
                    getStarted.TrySetResult();
                    await getGate.Task;
                    return new ApplicationContextResult.Success(getContext);
                });
            A.CallTo(() =>
                    _configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                        "client-id",
                        tenant: null,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ApplicationContextResult.Success(_reloadedContext));

            // The reload runs first: it drops its request-scoped memo, then blocks inside
            // hybridCache.RemoveAsync (gated below).
            Task<ApplicationContextResult> reloadTask = provider.ReloadApplicationByClientIdAsync(
                "client-id",
                tenant: null
            );
            await cache.RemoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // While the reload is blocked there, a concurrent Get for the same key inserts its own
            // fill into the now-empty request-scoped memo and starts blocking in the provider.
            Task<ApplicationContextResult> getTask = provider.GetApplicationByClientIdAsync(
                "client-id",
                tenant: null
            );
            await getStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Releasing the reload's RemoveAsync lets it resume past the point where, on unfixed
            // code, it would join the Get's fill instead of starting its own; a reload that joined it
            // would hang here, since the Get's fill does not complete until getGate is released below.
            cache.ReleaseRemove();
            _reloadResult = await reloadTask.WaitAsync(TimeSpan.FromSeconds(10));

            getGate.SetResult();
            await getTask;
        }

        [Test]
        public void It_Calls_The_Reload_Provider_Exactly_Once()
        {
            A.CallTo(() =>
                    _configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                        "client-id",
                        tenant: null,
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_Returns_The_Reload_Providers_Result()
        {
            _reloadResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(_reloadedContext));
        }
    }

    [TestFixture]
    public class Given_A_Fill_That_Completes_Successfully_After_Being_Marked_Abandoned
    {
        private int _fetchCount;
        private ApplicationContext _expectedContext = null!;
        private ApplicationContextResult _reusedResult = null!;

        [SetUp]
        public async Task Setup()
        {
            var configurationServiceApplicationProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            var provider = new CachedApplicationContextProvider(
                configurationServiceApplicationProvider,
                new PassthroughHybridCache(),
                new CacheSettings { ApplicationContextCacheExpirationSeconds = 123 },
                NullLogger<CachedApplicationContextProvider>.Instance
            );
            var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Deliberately not RunContinuationsAsynchronously: releasing this synchronously runs
            // every continuation between it and the fill's own Task completing, on this thread,
            // before SetResult returns. That is what turns "the fill has already completed
            // successfully" into a fact this test can rely on immediately afterwards, rather than
            // something it would otherwise have to poll for.
            var releaseFetch = new TaskCompletionSource();
            _expectedContext = CreateApplicationContext("client-id", 7);
            // NUnit reuses one fixture instance across every [Test] method in it, running SetUp again
            // before each; a field this method only increments, rather than fully reassigning, would
            // otherwise carry a count over from whichever test ran before it.
            _fetchCount = 0;

            A.CallTo(() =>
                    configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                        "client-id",
                        tenant: null,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(async _ =>
                {
                    // This fetch does not observe the fill's own cancellation: it models a fetch that
                    // is already past the point where cancelling stops any work, so it completes
                    // successfully even after the only caller waiting on it has already left.
                    Interlocked.Increment(ref _fetchCount);
                    fetchStarted.TrySetResult();
                    await releaseFetch.Task;
                    return new ApplicationContextResult.Success(_expectedContext);
                });

            using var abandoningCallerCts = new CancellationTokenSource();
            Task<ApplicationContextResult> abandoningCallerTask = provider.GetApplicationByClientIdAsync(
                "client-id",
                tenant: null,
                abandoningCallerCts.Token
            );
            await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await abandoningCallerCts.CancelAsync();
            try
            {
                await abandoningCallerTask;
            }
            catch (OperationCanceledException)
            {
                // Expected: this caller left before the fill it started finished.
            }

            releaseFetch.SetResult();

            // By the time SetResult above returns, the fill has already completed successfully (see
            // the comment on releaseFetch), so this second, same-scope caller is guaranteed to
            // observe an abandoned-but-completed fill rather than racing it.
            _reusedResult = await provider.GetApplicationByClientIdAsync("client-id", tenant: null);
        }

        [Test]
        public void It_Reuses_The_Abandoned_Fills_Successful_Result()
        {
            _reusedResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(_expectedContext));
        }

        [Test]
        public void It_Does_Not_Fetch_A_Second_Time()
        {
            _fetchCount.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_A_Fill_Replaced_By_A_Reload_Before_It_Completes
    {
        private bool _replacedFillTokenThrowsObjectDisposedAfterProviderDispose;

        [SetUp]
        public async Task Setup()
        {
            var configurationServiceApplicationProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            var provider = new CachedApplicationContextProvider(
                configurationServiceApplicationProvider,
                new PassthroughHybridCache(),
                new CacheSettings { ApplicationContextCacheExpirationSeconds = 123 },
                NullLogger<CachedApplicationContextProvider>.Instance
            );

            var getGate = new TaskCompletionSource();
            var getStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken replacedFillToken = default;

            async Task<ApplicationContextResult> FetchAsync(CancellationToken token)
            {
                replacedFillToken = token;
                getStarted.TrySetResult();
                await getGate.Task;
                return new ApplicationContextResult.Success(CreateApplicationContext("client-id", 1));
            }

            A.CallTo(() =>
                    configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                        "client-id",
                        tenant: null,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(call => FetchAsync((CancellationToken)call.Arguments[2]!));
            A.CallTo(() =>
                    configurationServiceApplicationProvider.ReloadApplicationByClientIdAsync(
                        "client-id",
                        tenant: null,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ApplicationContextResult.Success(CreateApplicationContext("client-id", 2)));

            // The Get's own task is deliberately never captured or awaited: nothing below needs its
            // result, and its fetch never observes cancellation, so it would otherwise never finish.
            _ = provider.GetApplicationByClientIdAsync("client-id", tenant: null);
            await getStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The reload overwrites the Get's request-scoped memo directly, never evicting or
            // cancelling it through the normal waiter path, so only the provider's own
            // fill-creation tracking - not its dictionary of current memos - keeps this orphaned
            // fill reachable for disposal.
            await provider.ReloadApplicationByClientIdAsync("client-id", tenant: null);

            provider.Dispose();

            try
            {
                _ = replacedFillToken.WaitHandle;
                _replacedFillTokenThrowsObjectDisposedAfterProviderDispose = false;
            }
            catch (ObjectDisposedException)
            {
                _replacedFillTokenThrowsObjectDisposedAfterProviderDispose = true;
            }
        }

        [Test]
        public void It_Disposes_The_Replaced_Fills_Cancellation_Token()
        {
            _replacedFillTokenThrowsObjectDisposedAfterProviderDispose.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_A_Pre_Cancelled_Caller_Token
    {
        private IConfigurationServiceApplicationProvider _configurationServiceApplicationProvider = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _configurationServiceApplicationProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            var provider = new CachedApplicationContextProvider(
                _configurationServiceApplicationProvider,
                CreateHybridCache(),
                new CacheSettings { ApplicationContextCacheExpirationSeconds = 123 },
                NullLogger<CachedApplicationContextProvider>.Instance
            );
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            try
            {
                await provider.GetApplicationByClientIdAsync("client-id", tenant: null, cts.Token);
            }
            catch (Exception ex)
            {
                _exception = ex;
            }
        }

        [Test]
        public void It_Throws_Operation_Canceled()
        {
            _exception.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_Never_Calls_The_Cms_Provider()
        {
            A.CallTo(() =>
                    _configurationServiceApplicationProvider.GetApplicationByClientIdAsync(
                        A<string>._,
                        A<string?>._,
                        A<CancellationToken>._
                    )
                )
                .MustNotHaveHappened();
        }
    }

    /// <summary>
    /// A HybridCache whose GetOrCreateAsync always calls the factory directly, and whose SetAsync,
    /// RemoveAsync, and RemoveByTagAsync are no-ops. Used wherever a test needs direct control over a
    /// fill's fetch and the CancellationToken it runs on, without the real HybridCache's own caching,
    /// linked-token, or scheduling behavior in the way.
    /// </summary>
    private class PassthroughHybridCache : HybridCache
    {
        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        ) => factory(state, cancellationToken);

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

    /// <summary>
    /// A PassthroughHybridCache whose first GetOrCreateAsync never completes until released, ignoring
    /// its token, and whose later calls run the factory immediately. It exists only to hold the
    /// abandoned-fill window open deterministically.
    /// </summary>
    private sealed class HeldOpenHybridCache : PassthroughHybridCache
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
    }

    /// <summary>
    /// A PassthroughHybridCache whose RemoveAsync blocks until released. It exists only to hold open
    /// the window between a reload dropping its request-scoped memo and installing its own fill, so a
    /// concurrent Get for the same key can be driven to land in between deterministically.
    /// </summary>
    private sealed class RemoveGatedHybridCache : PassthroughHybridCache
    {
        private readonly TaskCompletionSource _releaseRemove = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource RemoveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseRemove() => _releaseRemove.TrySetResult();

        public override async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            RemoveStarted.TrySetResult();
            await _releaseRemove.Task;
        }
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
