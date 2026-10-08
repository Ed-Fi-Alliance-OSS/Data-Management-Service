// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

/// <summary>
/// Shared helpers for CachedApplicationContextProvider tests. Not itself a fixture: every
/// [TestFixture] below is a nested class so NUnit gives each scenario its own instance and each
/// [SetUp] runs exactly once per scenario, arranging and acting into fields the [Test] methods
/// only assert against.
/// </summary>
public class CachedApplicationContextProviderTests
{
    protected const string ClientId = "client-id";

    protected static CachedApplicationContextProvider CreateProvider(
        IConfigurationServiceApplicationProvider configurationServiceApplicationProvider,
        HybridCache hybridCache
    ) =>
        new(
            configurationServiceApplicationProvider,
            hybridCache,
            new CacheSettings { ApplicationContextCacheExpirationSeconds = 123 },
            NullLogger<CachedApplicationContextProvider>.Instance
        );

    protected static HybridCache CreateHybridCache()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    protected static ApplicationContext CreateApplicationContext(string clientId, long applicationId) =>
        new(applicationId, 100, clientId, Guid.NewGuid(), [1, 2, 3], null, []);

    protected static ApplicationContextResult CreateResult(ApplicationContextOutcome outcome) =>
        outcome switch
        {
            ApplicationContextOutcome.Success => new ApplicationContextResult.Success(
                CreateApplicationContext(ClientId, 1)
            ),
            ApplicationContextOutcome.NotFound => new ApplicationContextResult.NotFound(),
            ApplicationContextOutcome.Unavailable => new ApplicationContextResult.Unavailable(),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };

    public enum ApplicationContextOutcome
    {
        Success,
        NotFound,
        Unavailable,
    }

    [TestFixture]
    public class Given_A_Warm_Single_Tenant_Cache_Entry : CachedApplicationContextProviderTests
    {
        private ApplicationContext _expectedContext = null!;
        private IConfigurationServiceApplicationProvider _cmsProvider = null!;
        private ApplicationContextResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            HybridCache hybridCache = CreateHybridCache();
            _expectedContext = CreateApplicationContext(ClientId, 1);
            await hybridCache.SetAsync($"ApplicationContext:single:{ClientId}", _expectedContext);

            _result = await CreateProvider(_cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null);
        }

        [Test]
        public void It_Returns_The_Cached_Context()
        {
            _result.Should().BeEquivalentTo(new ApplicationContextResult.Success(_expectedContext));
        }

        [Test]
        public void It_Never_Calls_The_Cms_Provider()
        {
            A.CallTo(() =>
                    _cmsProvider.GetApplicationByClientIdAsync(
                        A<string>._,
                        A<string?>._,
                        A<CancellationToken>._
                    )
                )
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_A_Warm_Normalized_Tenant_Cache_Entry : CachedApplicationContextProviderTests
    {
        private ApplicationContext _expectedContext = null!;
        private ApplicationContextResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            var cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            HybridCache hybridCache = CreateHybridCache();
            _expectedContext = CreateApplicationContext(ClientId, 2);
            await hybridCache.SetAsync($"ApplicationContext:tenant:districta:{ClientId}", _expectedContext);

            _result = await CreateProvider(cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, "DistrictA");
        }

        [Test]
        public void It_Returns_The_Cached_Context()
        {
            _result.Should().BeEquivalentTo(new ApplicationContextResult.Success(_expectedContext));
        }
    }

    [TestFixture]
    public class Given_The_Same_Client_In_Two_Different_Tenants : CachedApplicationContextProviderTests
    {
        private ApplicationContext _northContext = null!;
        private ApplicationContext _southContext = null!;
        private ApplicationContextResult _north = null!;
        private ApplicationContextResult _south = null!;

        [SetUp]
        public async Task Setup()
        {
            var cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            HybridCache hybridCache = CreateHybridCache();
            _northContext = CreateApplicationContext(ClientId, 1);
            _southContext = CreateApplicationContext(ClientId, 2);
            A.CallTo(() =>
                    cmsProvider.GetApplicationByClientIdAsync(ClientId, "north", A<CancellationToken>._)
                )
                .Returns(new ApplicationContextResult.Success(_northContext));
            A.CallTo(() =>
                    cmsProvider.GetApplicationByClientIdAsync(ClientId, "south", A<CancellationToken>._)
                )
                .Returns(new ApplicationContextResult.Success(_southContext));

            _north = await CreateProvider(cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, "north");
            _south = await CreateProvider(cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, "south");
        }

        [Test]
        public void It_Resolves_The_North_Tenant_Context()
        {
            _north.Should().BeEquivalentTo(new ApplicationContextResult.Success(_northContext));
        }

        [Test]
        public void It_Resolves_The_South_Tenant_Context()
        {
            _south.Should().BeEquivalentTo(new ApplicationContextResult.Success(_southContext));
        }
    }

    [TestFixture]
    public class Given_A_Tenant_Looked_Up_With_Different_Casing_Across_Scopes
        : CachedApplicationContextProviderTests
    {
        private IConfigurationServiceApplicationProvider _cmsProvider = null!;
        private ApplicationContextResult _result = null!;
        private ApplicationContext _expectedContext = null!;

        [SetUp]
        public async Task Setup()
        {
            _cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            HybridCache hybridCache = CreateHybridCache();
            _expectedContext = CreateApplicationContext(ClientId, 1);
            A.CallTo(() =>
                    _cmsProvider.GetApplicationByClientIdAsync(ClientId, "DistrictA", A<CancellationToken>._)
                )
                .Returns(new ApplicationContextResult.Success(_expectedContext));

            await CreateProvider(_cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, "DistrictA");
            _result = await CreateProvider(_cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, "districta");
        }

        [Test]
        public void It_Returns_The_Same_Context_For_The_Normalized_Tenant()
        {
            _result.Should().BeEquivalentTo(new ApplicationContextResult.Success(_expectedContext));
        }

        [Test]
        public void It_Calls_The_Cms_Provider_Exactly_Once()
        {
            A.CallTo(() =>
                    _cmsProvider.GetApplicationByClientIdAsync(ClientId, A<string?>._, A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_A_Cold_NotFound_Lookup : CachedApplicationContextProviderTests
    {
        private IConfigurationServiceApplicationProvider _cmsProvider = null!;
        private ApplicationContextResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            A.CallTo(() => _cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .Returns(new ApplicationContextResult.NotFound());

            _result = await CreateProvider(_cmsProvider, CreateHybridCache())
                .GetApplicationByClientIdAsync(ClientId, tenant: null);
        }

        [Test]
        public void It_Returns_NotFound()
        {
            _result.Should().BeOfType<ApplicationContextResult.NotFound>();
        }

        [Test]
        public void It_Never_Calls_Reload()
        {
            A.CallTo(() =>
                    _cmsProvider.ReloadApplicationByClientIdAsync(
                        A<string>._,
                        A<string?>._,
                        A<CancellationToken>._
                    )
                )
                .MustNotHaveHappened();
        }
    }

    [TestFixture(ApplicationContextOutcome.Success)]
    [TestFixture(ApplicationContextOutcome.NotFound)]
    [TestFixture(ApplicationContextOutcome.Unavailable)]
    public class Given_The_First_Outcome_For_A_Request(ApplicationContextOutcome outcome)
        : CachedApplicationContextProviderTests
    {
        private IConfigurationServiceApplicationProvider _cmsProvider = null!;
        private ApplicationContextResult _first = null!;
        private ApplicationContextResult _second = null!;

        [SetUp]
        public async Task Setup()
        {
            ApplicationContextResult expectedResult = CreateResult(outcome);
            _cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            A.CallTo(() => _cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .Returns(expectedResult);

            CachedApplicationContextProvider provider = CreateProvider(_cmsProvider, CreateHybridCache());
            _first = await provider.GetApplicationByClientIdAsync(ClientId, tenant: null);
            _second = await provider.GetApplicationByClientIdAsync(ClientId, tenant: null);
        }

        [Test]
        public void It_Memoizes_The_Same_Result_For_The_Second_Call_In_The_Request()
        {
            _second.Should().BeSameAs(_first);
        }

        [Test]
        public void It_Calls_The_Cms_Provider_Exactly_Once()
        {
            A.CallTo(() => _cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture(ApplicationContextOutcome.NotFound)]
    [TestFixture(ApplicationContextOutcome.Unavailable)]
    public class Given_A_Failed_Result_Followed_By_Recovery_In_A_New_Scope(ApplicationContextOutcome outcome)
        : CachedApplicationContextProviderTests
    {
        private IConfigurationServiceApplicationProvider _cmsProvider = null!;
        private ApplicationContextResult _recovered = null!;
        private ApplicationContext _expectedContext = null!;
        private int _lookupCount;

        [SetUp]
        public async Task Setup()
        {
            _cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            HybridCache hybridCache = CreateHybridCache();
            _expectedContext = CreateApplicationContext(ClientId, 1);
            _lookupCount = 0;

            A.CallTo(() => _cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .ReturnsLazily(_ =>
                {
                    _lookupCount++;
                    return Task.FromResult<ApplicationContextResult>(
                        _lookupCount == 1
                            ? CreateResult(outcome)
                            : new ApplicationContextResult.Success(_expectedContext)
                    );
                });

            await CreateProvider(_cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null);
            _recovered = await CreateProvider(_cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null);
        }

        [Test]
        public void It_Recovers_With_A_Success_In_The_New_Scope()
        {
            _recovered.Should().BeEquivalentTo(new ApplicationContextResult.Success(_expectedContext));
        }

        [Test]
        public void It_Looks_Up_The_Cms_Twice()
        {
            _lookupCount.Should().Be(2);
        }
    }

    [TestFixture]
    public class Given_A_Warm_Success_Cache_During_A_Cms_Outage : CachedApplicationContextProviderTests
    {
        private IConfigurationServiceApplicationProvider _cmsProvider = null!;
        private ApplicationContextResult _result = null!;
        private ApplicationContext _expectedContext = null!;

        [SetUp]
        public async Task Setup()
        {
            _cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            HybridCache hybridCache = CreateHybridCache();
            _expectedContext = CreateApplicationContext(ClientId, 1);
            A.CallTo(() => _cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .Returns(new ApplicationContextResult.Success(_expectedContext));

            await CreateProvider(_cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null);

            A.CallTo(() => _cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .Returns(new ApplicationContextResult.Unavailable());

            _result = await CreateProvider(_cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null);
        }

        [Test]
        public void It_Serves_The_Warm_Success_Instead_Of_The_Outage()
        {
            _result.Should().BeEquivalentTo(new ApplicationContextResult.Success(_expectedContext));
        }
    }

    [TestFixture]
    public class Given_A_Reload_For_One_Tenant_Among_Several : CachedApplicationContextProviderTests
    {
        private ApplicationContextResult _reloadResult = null!;
        private ApplicationContextResult _north = null!;
        private ApplicationContextResult _south = null!;
        private ApplicationContext _refreshedNorthContext = null!;
        private ApplicationContext _southContext = null!;
        private IConfigurationServiceApplicationProvider _cmsProvider = null!;

        [SetUp]
        public async Task Setup()
        {
            _cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            HybridCache hybridCache = CreateHybridCache();
            var staleNorthContext = CreateApplicationContext(ClientId, 1);
            _southContext = CreateApplicationContext(ClientId, 2);
            _refreshedNorthContext = CreateApplicationContext(ClientId, 3);
            await hybridCache.SetAsync($"ApplicationContext:tenant:north:{ClientId}", staleNorthContext);
            await hybridCache.SetAsync($"ApplicationContext:tenant:south:{ClientId}", _southContext);
            A.CallTo(() =>
                    _cmsProvider.ReloadApplicationByClientIdAsync(ClientId, "North", A<CancellationToken>._)
                )
                .Returns(new ApplicationContextResult.Success(_refreshedNorthContext));

            _reloadResult = await CreateProvider(_cmsProvider, hybridCache)
                .ReloadApplicationByClientIdAsync(ClientId, "North");
            _north = await CreateProvider(_cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, "north");
            _south = await CreateProvider(_cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, "south");
        }

        [Test]
        public void It_Returns_The_Refreshed_Context_From_The_Reload()
        {
            _reloadResult
                .Should()
                .BeEquivalentTo(new ApplicationContextResult.Success(_refreshedNorthContext));
        }

        [Test]
        public void It_Reflects_The_Refreshed_Context_For_The_Reloaded_Tenant()
        {
            _north.Should().BeEquivalentTo(new ApplicationContextResult.Success(_refreshedNorthContext));
        }

        [Test]
        public void It_Leaves_The_Other_Tenant_Untouched()
        {
            _south.Should().BeEquivalentTo(new ApplicationContextResult.Success(_southContext));
        }

        [Test]
        public void It_Never_Calls_Get_On_The_Cms_Provider()
        {
            A.CallTo(() =>
                    _cmsProvider.GetApplicationByClientIdAsync(
                        A<string>._,
                        A<string?>._,
                        A<CancellationToken>._
                    )
                )
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_A_Blank_Client_Id : CachedApplicationContextProviderTests
    {
        private IConfigurationServiceApplicationProvider _cmsProvider = null!;
        private ApplicationContextResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            _result = await CreateProvider(_cmsProvider, CreateHybridCache())
                .GetApplicationByClientIdAsync(" ", tenant: null);
        }

        [Test]
        public void It_Returns_NotFound()
        {
            _result.Should().BeOfType<ApplicationContextResult.NotFound>();
        }

        [Test]
        public void It_Never_Calls_The_Cms_Provider()
        {
            A.CallTo(() =>
                    _cmsProvider.GetApplicationByClientIdAsync(
                        A<string>._,
                        A<string?>._,
                        A<CancellationToken>._
                    )
                )
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_A_Reload_That_Invalidates_The_Request_Scoped_Memo
        : CachedApplicationContextProviderTests
    {
        private ApplicationContextResult _beforeReload = null!;
        private ApplicationContextResult _reloadResult = null!;
        private ApplicationContextResult _afterReload = null!;
        private ApplicationContext _reloadedContext = null!;

        [SetUp]
        public async Task Setup()
        {
            var cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            _reloadedContext = CreateApplicationContext(ClientId, 9);
            A.CallTo(() => cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .Returns(new ApplicationContextResult.NotFound());
            A.CallTo(() =>
                    cmsProvider.ReloadApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._)
                )
                .Returns(new ApplicationContextResult.Success(_reloadedContext));

            CachedApplicationContextProvider provider = CreateProvider(cmsProvider, CreateHybridCache());
            _beforeReload = await provider.GetApplicationByClientIdAsync(ClientId, tenant: null);
            _reloadResult = await provider.ReloadApplicationByClientIdAsync(ClientId, tenant: null);
            _afterReload = await provider.GetApplicationByClientIdAsync(ClientId, tenant: null);
        }

        [Test]
        public void It_Was_NotFound_Before_The_Reload()
        {
            _beforeReload.Should().BeOfType<ApplicationContextResult.NotFound>();
        }

        [Test]
        public void It_Returns_The_Reloaded_Context_From_The_Reload_Call()
        {
            _reloadResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(_reloadedContext));
        }

        [Test]
        public void It_Returns_The_Reloaded_Context_For_A_Subsequent_Get_In_The_Same_Scope()
        {
            _afterReload.Should().BeEquivalentTo(new ApplicationContextResult.Success(_reloadedContext));
        }
    }

    [TestFixture]
    public class Given_A_Reload_Whose_Request_Is_Cancelled_After_The_Cms_Answers
        : CachedApplicationContextProviderTests
    {
        private ApplicationContextResult _laterLookup = null!;
        private ApplicationContext _refreshedContext = null!;

        [SetUp]
        public async Task Setup()
        {
            var cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            HybridCache hybridCache = new TokenHonoringHybridCache(CreateHybridCache());
            var staleContext = CreateApplicationContext(ClientId, 1);
            _refreshedContext = CreateApplicationContext(ClientId, 2);
            using var reloadCts = new CancellationTokenSource();

            A.CallTo(() => cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .ReturnsNextFromSequence(
                    new ApplicationContextResult.Success(staleContext),
                    new ApplicationContextResult.Success(_refreshedContext)
                );
            A.CallTo(() =>
                    cmsProvider.ReloadApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._)
                )
                .ReturnsLazily(async _ =>
                {
                    await reloadCts.CancelAsync();
                    return new ApplicationContextResult.Success(_refreshedContext);
                });

            await CreateProvider(cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null);

            try
            {
                await CreateProvider(cmsProvider, hybridCache)
                    .ReloadApplicationByClientIdAsync(ClientId, tenant: null, reloadCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Whether the cancelled caller sees the result is not what this fixture asserts.
            }

            _laterLookup = await CreateProvider(cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null);
        }

        [Test]
        public void It_Does_Not_Leave_The_Pre_Reload_Entry_In_The_Shared_Cache()
        {
            _laterLookup.Should().BeEquivalentTo(new ApplicationContextResult.Success(_refreshedContext));
        }
    }

    [TestFixture]
    public class Given_A_Reload_Whose_Cms_Call_Is_Cancelled : CachedApplicationContextProviderTests
    {
        private Exception? _reloadException;
        private ApplicationContextResult _laterLookup = null!;
        private ApplicationContext _refreshedContext = null!;

        [SetUp]
        public async Task Setup()
        {
            var cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            HybridCache hybridCache = CreateHybridCache();
            var staleContext = CreateApplicationContext(ClientId, 1);
            _refreshedContext = CreateApplicationContext(ClientId, 2);
            using var reloadCts = new CancellationTokenSource();

            A.CallTo(() => cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .ReturnsNextFromSequence(
                    new ApplicationContextResult.Success(staleContext),
                    new ApplicationContextResult.Success(_refreshedContext)
                );
            A.CallTo(() =>
                    cmsProvider.ReloadApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._)
                )
                .ReturnsLazily(async _ =>
                {
                    await reloadCts.CancelAsync();
                    reloadCts.Token.ThrowIfCancellationRequested();
                    return new ApplicationContextResult.Success(_refreshedContext);
                });

            await CreateProvider(cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null);

            try
            {
                await CreateProvider(cmsProvider, hybridCache)
                    .ReloadApplicationByClientIdAsync(ClientId, tenant: null, reloadCts.Token);
            }
            catch (Exception ex)
            {
                _reloadException = ex;
            }

            _laterLookup = await CreateProvider(cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null);
        }

        [Test]
        public void It_Throws_Operation_Canceled_For_The_Reload()
        {
            _reloadException.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_Has_Already_Removed_The_Pre_Reload_Entry_From_The_Shared_Cache()
        {
            _laterLookup.Should().BeEquivalentTo(new ApplicationContextResult.Success(_refreshedContext));
        }
    }

    [TestFixture]
    public class Given_Ownership_Tokens_Round_Tripped_Through_The_Cache
        : CachedApplicationContextProviderTests
    {
        private ApplicationContextResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            var cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            HybridCache hybridCache = CreateHybridCache();
            ApplicationContext expectedContext = CreateApplicationContext(ClientId, 1) with
            {
                CreatorOwnershipTokenId = 303,
                OwnershipTokenIds = [202, 404],
            };
            await hybridCache.SetAsync($"ApplicationContext:single:{ClientId}", expectedContext);

            _result = await CreateProvider(cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null);
        }

        [Test]
        public void It_Preserves_The_Creator_Ownership_Token()
        {
            var success = _result.Should().BeOfType<ApplicationContextResult.Success>().Subject;
            success.ApplicationContext.CreatorOwnershipTokenId.Should().Be(303);
        }

        [Test]
        public void It_Preserves_The_Ownership_Token_List()
        {
            var success = _result.Should().BeOfType<ApplicationContextResult.Success>().Subject;
            success.ApplicationContext.OwnershipTokenIds.Should().Equal((short)202, (short)404);
        }
    }

    [TestFixture]
    public class Given_Two_Scopes_Where_One_Callers_Token_Is_Cancelled_Mid_Fetch
        : CachedApplicationContextProviderTests
    {
        private Exception? _cancelledCallerException;
        private ApplicationContextResult _liveResult = null!;
        private int _fetchCount;
        private ApplicationContext _expectedContext = null!;

        [SetUp]
        public async Task Setup()
        {
            var cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            HybridCache hybridCache = CreateHybridCache();
            var gate = new TaskCompletionSource();
            var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _expectedContext = CreateApplicationContext(ClientId, 1);
            _fetchCount = 0;

            A.CallTo(() => cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .ReturnsLazily(async _ =>
                {
                    Interlocked.Increment(ref _fetchCount);
                    fetchStarted.TrySetResult();
                    await gate.Task;
                    return new ApplicationContextResult.Success(_expectedContext);
                });

            using var cancelledCallerCts = new CancellationTokenSource();
            Task<ApplicationContextResult> cancelledCallerTask = CreateProvider(cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null, cancelledCallerCts.Token);
            await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Task<ApplicationContextResult> liveCallerTask = CreateProvider(cmsProvider, hybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null);

            await cancelledCallerCts.CancelAsync();

            try
            {
                await cancelledCallerTask;
            }
            catch (Exception ex)
            {
                _cancelledCallerException = ex;
            }

            gate.SetResult();
            _liveResult = await liveCallerTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Test]
        public void It_Throws_Operation_Canceled_For_The_Cancelled_Caller()
        {
            _cancelledCallerException.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_Returns_The_Live_Callers_Result_From_The_Other_Scope()
        {
            _liveResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(_expectedContext));
        }

        [Test]
        public void It_Fetches_From_The_Cms_Exactly_Once()
        {
            _fetchCount.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_Two_Callers_In_The_Same_Scope_Where_One_Is_Cancelled_Mid_Fetch
        : CachedApplicationContextProviderTests
    {
        private Exception? _cancelledCallerException;
        private ApplicationContextResult _liveResult = null!;
        private int _fetchCount;
        private ApplicationContext _expectedContext = null!;

        [SetUp]
        public async Task Setup()
        {
            var cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            CachedApplicationContextProvider provider = CreateProvider(cmsProvider, CreateHybridCache());
            var gate = new TaskCompletionSource();
            var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _expectedContext = CreateApplicationContext(ClientId, 1);
            _fetchCount = 0;

            A.CallTo(() => cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .ReturnsLazily(async _ =>
                {
                    Interlocked.Increment(ref _fetchCount);
                    fetchStarted.TrySetResult();
                    await gate.Task;
                    return new ApplicationContextResult.Success(_expectedContext);
                });

            using var cancelledCallerCts = new CancellationTokenSource();
            Task<ApplicationContextResult> cancelledCallerTask = provider.GetApplicationByClientIdAsync(
                ClientId,
                tenant: null,
                cancelledCallerCts.Token
            );
            await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Same provider instance - the same request scope - as the cancelled caller above.
            Task<ApplicationContextResult> liveCallerTask = provider.GetApplicationByClientIdAsync(
                ClientId,
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

            gate.SetResult();
            _liveResult = await liveCallerTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Test]
        public void It_Throws_Operation_Canceled_For_The_Cancelled_Caller()
        {
            _cancelledCallerException.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_Returns_The_Live_Callers_Result_In_The_Same_Scope()
        {
            _liveResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(_expectedContext));
        }

        [Test]
        public void It_Fetches_From_The_Cms_Exactly_Once()
        {
            _fetchCount.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_A_Cancelled_Caller_Is_Followed_By_Another_Caller_In_The_Same_Scope
        : CachedApplicationContextProviderTests
    {
        private Exception? _cancelledCallerException;
        private ApplicationContextResult _laterResult = null!;
        private ApplicationContext _expectedContext = null!;

        [SetUp]
        public async Task Setup()
        {
            var cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            CachedApplicationContextProvider provider = CreateProvider(cmsProvider, CreateHybridCache());
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _expectedContext = CreateApplicationContext(ClientId, 1);

            A.CallTo(() => cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .ReturnsLazily(async _ =>
                {
                    fetchStarted.TrySetResult();
                    await gate.Task;
                    return new ApplicationContextResult.Success(_expectedContext);
                });

            using var cancelledCallerCts = new CancellationTokenSource();
            Task<ApplicationContextResult> cancelledCallerTask = provider.GetApplicationByClientIdAsync(
                ClientId,
                tenant: null,
                cancelledCallerCts.Token
            );
            await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancelledCallerCts.CancelAsync();

            try
            {
                await cancelledCallerTask;
            }
            catch (Exception ex)
            {
                _cancelledCallerException = ex;
            }

            gate.SetResult();

            // Started only after the cancelled caller's own exception has already been observed, so
            // this is a fresh call into the same scope rather than a second waiter on the same fetch.
            _laterResult = await provider
                .GetApplicationByClientIdAsync(ClientId, tenant: null)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Test]
        public void It_Throws_Operation_Canceled_For_The_Cancelled_Caller()
        {
            _cancelledCallerException.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_Returns_A_Success_For_The_Later_Caller_In_The_Same_Scope()
        {
            _laterResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(_expectedContext));
        }
    }

    [TestFixture]
    public class Given_A_Reload_While_A_Get_Fetch_Is_Still_Pending_For_The_Same_Key
        : CachedApplicationContextProviderTests
    {
        private IConfigurationServiceApplicationProvider _cmsProvider = null!;
        private ApplicationContextResult _reloadResult = null!;
        private ApplicationContext _reloadedContext = null!;
        private ApplicationContext _getContext = null!;
        private ApplicationContextResult _freshScopeResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            HybridCache sharedHybridCache = CreateHybridCache();
            var getGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var getStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _getContext = CreateApplicationContext(ClientId, 1);
            _reloadedContext = CreateApplicationContext(ClientId, 2);

            A.CallTo(() => _cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .ReturnsLazily(async _ =>
                {
                    getStarted.TrySetResult();
                    await getGate.Task;
                    return new ApplicationContextResult.Success(_getContext);
                });
            A.CallTo(() =>
                    _cmsProvider.ReloadApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._)
                )
                .Returns(new ApplicationContextResult.Success(_reloadedContext));

            // The Get and the Reload each run against their own provider instance - their own request
            // scope - sharing only the HybridCache, exactly as two concurrent requests would.
            Task<ApplicationContextResult> getTask = CreateProvider(_cmsProvider, sharedHybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null);
            await getStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The reload must return without waiting on the Get's gate: joining the Get's in-flight
            // HybridCache factory here would hang, since that factory does not resolve until
            // getGate is released below.
            _reloadResult = await CreateProvider(_cmsProvider, sharedHybridCache)
                .ReloadApplicationByClientIdAsync(ClientId, tenant: null)
                .WaitAsync(TimeSpan.FromSeconds(10));

            getGate.SetResult();
            await getTask;

            _freshScopeResult = await CreateProvider(_cmsProvider, sharedHybridCache)
                .GetApplicationByClientIdAsync(ClientId, tenant: null);
        }

        [Test]
        public void It_Calls_The_Reload_Provider_Exactly_Once()
        {
            A.CallTo(() =>
                    _cmsProvider.ReloadApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_Returns_The_Reload_Providers_Result_While_The_Get_Is_Still_Pending()
        {
            _reloadResult.Should().BeEquivalentTo(new ApplicationContextResult.Success(_reloadedContext));
        }

        [Test]
        public void It_Serves_A_Later_Request_The_Reloaded_Context_Not_The_Older_Fetch()
        {
            // The Get's fetch started before the reload and finished after it; its older result must
            // not replace what the reload wrote.
            _freshScopeResult
                .Should()
                .BeEquivalentTo(new ApplicationContextResult.Success(_reloadedContext));
        }
    }

    [TestFixture]
    public class Given_A_Pre_Cancelled_Caller_Token_For_Get : CachedApplicationContextProviderTests
    {
        private IConfigurationServiceApplicationProvider _cmsProvider = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            CachedApplicationContextProvider provider = CreateProvider(_cmsProvider, CreateHybridCache());
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            try
            {
                await provider.GetApplicationByClientIdAsync(ClientId, tenant: null, cts.Token);
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
                    _cmsProvider.GetApplicationByClientIdAsync(
                        A<string>._,
                        A<string?>._,
                        A<CancellationToken>._
                    )
                )
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_A_Pre_Cancelled_Caller_Token_For_Reload : CachedApplicationContextProviderTests
    {
        private IConfigurationServiceApplicationProvider _cmsProvider = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            CachedApplicationContextProvider provider = CreateProvider(_cmsProvider, CreateHybridCache());
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            try
            {
                await provider.ReloadApplicationByClientIdAsync(ClientId, tenant: null, cts.Token);
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
                    _cmsProvider.ReloadApplicationByClientIdAsync(
                        A<string>._,
                        A<string?>._,
                        A<CancellationToken>._
                    )
                )
                .MustNotHaveHappened();
        }
    }

    /// <summary>
    /// Wraps a real HybridCache so its writes observe their cancellation token, as a distributed
    /// second-level cache does. The in-memory cache alone ignores the token on SetAsync and
    /// RemoveAsync, which would hide a write skipped because the caller was cancelled.
    /// </summary>
    private sealed class TokenHonoringHybridCache(HybridCache inner) : HybridCache
    {
        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        ) => inner.GetOrCreateAsync(key, state, factory, options, tags, cancellationToken);

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.SetAsync(key, value, options, tags, cancellationToken);
        }

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.RemoveAsync(key, cancellationToken);
        }

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.RemoveByTagAsync(tag, cancellationToken);
        }
    }

    [TestFixture]
    public class Given_A_Deleted_Client_And_A_Controlled_Clock : CachedApplicationContextProviderTests
    {
        private IConfigurationServiceApplicationProvider _cmsProvider = null!;
        private ApplicationContextResult _justBeforeExpiry = null!;
        private ApplicationContextResult _atExpiry = null!;

        /// <summary>Drives HybridCache's in-process IMemoryCache expiry from a <see cref="FakeTimeProvider" />.</summary>
        private sealed class FakeTimeProviderClock(FakeTimeProvider timeProvider) : ISystemClock
        {
            public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
        }

        [SetUp]
        public async Task Setup()
        {
            var timeProvider = new FakeTimeProvider();
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(timeProvider);
            services.AddMemoryCache(options => options.Clock = new FakeTimeProviderClock(timeProvider));
            services.AddHybridCache();
            HybridCache hybridCache = services.BuildServiceProvider().GetRequiredService<HybridCache>();

            _cmsProvider = A.Fake<IConfigurationServiceApplicationProvider>();
            A.CallTo(() => _cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .Returns(
                    Task.FromResult<ApplicationContextResult>(
                        new ApplicationContextResult.Success(CreateApplicationContext(ClientId, 1))
                    )
                )
                .Once()
                .Then.Returns(
                    Task.FromResult<ApplicationContextResult>(new ApplicationContextResult.NotFound())
                );

            // A request scope per call, matching the scoped provider's production lifetime.
            CachedApplicationContextProvider CreateScopedProvider() =>
                new(
                    _cmsProvider,
                    hybridCache,
                    new CacheSettings { ApplicationContextCacheExpirationSeconds = 15 },
                    NullLogger<CachedApplicationContextProvider>.Instance
                );

            await CreateScopedProvider().GetApplicationByClientIdAsync(ClientId, null);
            timeProvider.Advance(TimeSpan.FromSeconds(14));
            _justBeforeExpiry = await CreateScopedProvider().GetApplicationByClientIdAsync(ClientId, null);
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            _atExpiry = await CreateScopedProvider().GetApplicationByClientIdAsync(ClientId, null);
        }

        [Test]
        public void It_keeps_serving_the_cached_context_until_expiry()
        {
            _justBeforeExpiry.Should().BeOfType<ApplicationContextResult.Success>();
        }

        [Test]
        public void It_reports_the_deleted_client_as_not_found_once_the_entry_expires()
        {
            _atExpiry.Should().BeOfType<ApplicationContextResult.NotFound>();
        }

        [Test]
        public void It_asks_the_Configuration_Service_again_only_after_expiry()
        {
            A.CallTo(() => _cmsProvider.GetApplicationByClientIdAsync(ClientId, null, A<CancellationToken>._))
                .MustHaveHappenedTwiceExactly();
        }
    }
}
