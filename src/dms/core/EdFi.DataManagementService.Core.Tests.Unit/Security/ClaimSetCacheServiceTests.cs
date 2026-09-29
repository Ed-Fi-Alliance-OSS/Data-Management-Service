// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

public class ClaimSetCacheServiceTests
{
    protected static IMemoryCache CreateMemoryCache() => new MemoryCache(new MemoryCacheOptions());

    protected static CacheSettings CreateCacheSettings() => new();

    [TestFixture]
    [Parallelizable]
    public class Given_Service_Receives_Expected_Data : ClaimSetCacheServiceTests
    {
        private readonly IConfigurationServiceClaimSetProvider _securityMetadataProvider =
            A.Fake<IConfigurationServiceClaimSetProvider>();
        private CachedClaimSetProvider? _service;
        private IList<ClaimSet>? _claims;
        private IList<ClaimSet>? _expectedClaims;

        [SetUp]
        public async Task Setup()
        {
            _expectedClaims = [new("ClaimSet1", []), new("ClaimSet2", [])];
            A.CallTo(() =>
                    _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .Returns(_expectedClaims);

            _service = new CachedClaimSetProvider(
                _securityMetadataProvider,
                CreateMemoryCache(),
                CreateCacheSettings(),
                TimeProvider.System,
                A.Fake<IHostApplicationLifetime>(),
                NullLogger<CachedClaimSetProvider>.Instance
            );
            _claims = await _service.GetAllClaimSets();
        }

        [Test]
        public void It_Should_Return_Claims_From_Provider()
        {
            _claims.Should().NotBeNull();
            _claims!.Count.Should().Be(2);
            _claims[0].Name.Should().Be("ClaimSet1");
            A.CallTo(() =>
                    _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Cache_Has_Claims : ClaimSetCacheServiceTests
    {
        private readonly IConfigurationServiceClaimSetProvider _securityMetadataProvider =
            A.Fake<IConfigurationServiceClaimSetProvider>();
        private CachedClaimSetProvider? _service;
        private IList<ClaimSet>? _firstClaims;
        private IList<ClaimSet>? _secondClaims;
        private IList<ClaimSet>? _expectedClaims;

        [SetUp]
        public async Task Setup()
        {
            _expectedClaims = [new("ClaimSet1", []), new("ClaimSet2", [])];
            A.CallTo(() =>
                    _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .Returns(_expectedClaims);

            // Use the same memory cache for both calls
            var memoryCache = CreateMemoryCache();

            _service = new CachedClaimSetProvider(
                _securityMetadataProvider,
                memoryCache,
                CreateCacheSettings(),
                TimeProvider.System,
                A.Fake<IHostApplicationLifetime>(),
                NullLogger<CachedClaimSetProvider>.Instance
            );

            // First call - should fetch from provider
            _firstClaims = await _service.GetAllClaimSets();

            // Second call - should return from cache
            _secondClaims = await _service.GetAllClaimSets();
        }

        [Test]
        public void It_Should_Return_Same_Claims_For_Both_Calls()
        {
            _firstClaims.Should().NotBeNull();
            _secondClaims.Should().NotBeNull();
            _firstClaims!.Count.Should().Be(2);
            _secondClaims!.Count.Should().Be(2);
        }

        [Test]
        public void It_Should_Call_Provider_Only_Once()
        {
            // Second request should come from cache
            A.CallTo(() =>
                    _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Backend_Throws_Error : ClaimSetCacheServiceTests
    {
        private readonly IConfigurationServiceClaimSetProvider _securityMetadataProvider =
            A.Fake<IConfigurationServiceClaimSetProvider>();
        private CachedClaimSetProvider? _service;

        [Test]
        public void It_Should_Throw_Exception_For_BadRequest()
        {
            SetClaimSetCacheService(HttpStatusCode.BadRequest);
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetAllClaimSets());
        }

        [Test]
        public void It_Should_Throw_Exception_For_Unauthorized()
        {
            SetClaimSetCacheService(HttpStatusCode.Unauthorized);
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetAllClaimSets());
        }

        [Test]
        public void It_Should_Throw_Exception_For_NotFound()
        {
            SetClaimSetCacheService(HttpStatusCode.NotFound);
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetAllClaimSets());
        }

        [Test]
        public void It_Should_Throw_Exception_For_Forbidden()
        {
            SetClaimSetCacheService(HttpStatusCode.Forbidden);
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetAllClaimSets());
        }

        [Test]
        public void It_Should_Throw_Exception_For_InternalServerError()
        {
            SetClaimSetCacheService(HttpStatusCode.InternalServerError);
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetAllClaimSets());
        }

        private void SetClaimSetCacheService(HttpStatusCode statusCode)
        {
            A.CallTo(() =>
                    _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .Throws(
                    new HttpRequestException(
                        $"Error response from http://localhost. Error message: error. StatusCode: {statusCode}"
                    )
                );

            _service = new CachedClaimSetProvider(
                _securityMetadataProvider,
                CreateMemoryCache(),
                CreateCacheSettings(),
                TimeProvider.System,
                A.Fake<IHostApplicationLifetime>(),
                NullLogger<CachedClaimSetProvider>.Instance
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_GetCacheKey_Tests : ClaimSetCacheServiceTests
    {
        [Test]
        public void When_Tenant_Is_Null_It_Should_Return_Base_Cache_Key()
        {
            var cacheKey = CachedClaimSetProvider.GetCacheKey(null);
            cacheKey.Should().Be("ClaimSets");
        }

        [Test]
        public void When_Tenant_Is_Empty_It_Should_Return_Base_Cache_Key()
        {
            var cacheKey = CachedClaimSetProvider.GetCacheKey("");
            cacheKey.Should().Be("ClaimSets");
        }

        [Test]
        public void When_Tenant_Is_Specified_It_Should_Return_Tenant_Keyed_Cache_Key()
        {
            var cacheKey = CachedClaimSetProvider.GetCacheKey("Tenant1");
            cacheKey.Should().Be("ClaimSets:Tenant1");
        }

        [Test]
        public void When_Different_Tenants_It_Should_Return_Different_Cache_Keys()
        {
            var cacheKey1 = CachedClaimSetProvider.GetCacheKey("TenantA");
            var cacheKey2 = CachedClaimSetProvider.GetCacheKey("TenantB");

            cacheKey1.Should().NotBe(cacheKey2);
            cacheKey1.Should().Be("ClaimSets:TenantA");
            cacheKey2.Should().Be("ClaimSets:TenantB");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Tenant_Specific_Caching : ClaimSetCacheServiceTests
    {
        [Test]
        public async Task It_Should_Fetch_Different_Claims_For_Different_Tenants()
        {
            // Arrange
            var securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();

            var expectedTenant1Claims = new List<ClaimSet> { new("Tenant1ClaimSet", []) };
            var expectedTenant2Claims = new List<ClaimSet> { new("Tenant2ClaimSet", []) };

            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(
                        A<string?>.That.IsEqualTo("Tenant1"),
                        A<CancellationToken>._
                    )
                )
                .Returns(expectedTenant1Claims);
            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(
                        A<string?>.That.IsEqualTo("Tenant2"),
                        A<CancellationToken>._
                    )
                )
                .Returns(expectedTenant2Claims);

            var service = new CachedClaimSetProvider(
                securityMetadataProvider,
                CreateMemoryCache(),
                CreateCacheSettings(),
                TimeProvider.System,
                A.Fake<IHostApplicationLifetime>(),
                NullLogger<CachedClaimSetProvider>.Instance
            );

            // Act
            var tenant1Claims = await service.GetAllClaimSets("Tenant1");
            var tenant2Claims = await service.GetAllClaimSets("Tenant2");

            // Assert
            tenant1Claims.Should().NotBeNull();
            tenant1Claims.Count.Should().Be(1);
            tenant1Claims[0].Name.Should().Be("Tenant1ClaimSet");

            tenant2Claims.Should().NotBeNull();
            tenant2Claims.Count.Should().Be(1);
            tenant2Claims[0].Name.Should().Be("Tenant2ClaimSet");
        }

        [Test]
        public async Task It_Should_Call_Provider_For_Each_Tenant()
        {
            // Arrange
            var securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();

            var expectedClaims = new List<ClaimSet> { new("TestClaimSet", []) };

            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .Returns(expectedClaims);

            var service = new CachedClaimSetProvider(
                securityMetadataProvider,
                CreateMemoryCache(),
                CreateCacheSettings(),
                TimeProvider.System,
                A.Fake<IHostApplicationLifetime>(),
                NullLogger<CachedClaimSetProvider>.Instance
            );

            // Act
            await service.GetAllClaimSets("Tenant1");
            await service.GetAllClaimSets("Tenant2");

            // Assert - verify provider was called twice (once per tenant)
            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .MustHaveHappened(2, Times.Exactly);
        }

        [Test]
        public async Task It_Should_Cache_Separately_For_Each_Tenant()
        {
            // Arrange
            var securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();

            var expectedClaims = new List<ClaimSet> { new("TestClaimSet", []) };

            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .Returns(expectedClaims);

            var memoryCache = CreateMemoryCache();
            var service = new CachedClaimSetProvider(
                securityMetadataProvider,
                memoryCache,
                CreateCacheSettings(),
                TimeProvider.System,
                A.Fake<IHostApplicationLifetime>(),
                NullLogger<CachedClaimSetProvider>.Instance
            );

            // Act - call each tenant twice
            await service.GetAllClaimSets("Tenant1");
            await service.GetAllClaimSets("Tenant1"); // Should come from cache
            await service.GetAllClaimSets("Tenant2");
            await service.GetAllClaimSets("Tenant2"); // Should come from cache

            // Assert - provider should be called only once per tenant (2 times total)
            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .MustHaveHappened(2, Times.Exactly);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Cache_Is_Invalidated : ClaimSetCacheServiceTests
    {
        [Test]
        public async Task It_Should_Fetch_From_Provider_After_Invalidation()
        {
            var securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();
            var firstClaims = new List<ClaimSet> { new("FirstClaimSet", []) };
            var secondClaims = new List<ClaimSet> { new("SecondClaimSet", []) };

            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .ReturnsNextFromSequence(firstClaims, secondClaims);

            var service = new CachedClaimSetProvider(
                securityMetadataProvider,
                CreateMemoryCache(),
                CreateCacheSettings(),
                TimeProvider.System,
                A.Fake<IHostApplicationLifetime>(),
                NullLogger<CachedClaimSetProvider>.Instance
            );

            var cachedClaims = await service.GetAllClaimSets();
            await service.InvalidateCacheAsync();
            var reloadedClaims = await service.GetAllClaimSets();

            cachedClaims[0].Name.Should().Be("FirstClaimSet");
            reloadedClaims[0].Name.Should().Be("SecondClaimSet");
            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .MustHaveHappened(2, Times.Exactly);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Provider_Returns_Null : ClaimSetCacheServiceTests
    {
        [Test]
        public async Task It_Should_Not_Cache_Null_ClaimSets()
        {
            var securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();

            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .Returns(Task.FromResult<IList<ClaimSet>>(null!));

            var service = new CachedClaimSetProvider(
                securityMetadataProvider,
                CreateMemoryCache(),
                CreateCacheSettings(),
                TimeProvider.System,
                A.Fake<IHostApplicationLifetime>(),
                NullLogger<CachedClaimSetProvider>.Instance
            );

            var firstClaims = await service.GetAllClaimSets();
            var secondClaims = await service.GetAllClaimSets();

            firstClaims.Should().BeEmpty();
            secondClaims.Should().BeEmpty();
            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .MustHaveHappened(2, Times.Exactly);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Large_ClaimSet_Payload : ClaimSetCacheServiceTests
    {
        [Test]
        public async Task It_Should_Cache_Payloads_Above_HybridCache_Default_Limit()
        {
            var securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();
            var expectedClaims = CreateLargeClaimSetPayload();

            JsonSerializer.SerializeToUtf8Bytes(expectedClaims).Length.Should().BeGreaterThan(1_048_576);
            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .Returns(expectedClaims);

            var service = new CachedClaimSetProvider(
                securityMetadataProvider,
                CreateMemoryCache(),
                CreateCacheSettings(),
                TimeProvider.System,
                A.Fake<IHostApplicationLifetime>(),
                NullLogger<CachedClaimSetProvider>.Instance
            );

            var firstClaims = await service.GetAllClaimSets();
            var secondClaims = await service.GetAllClaimSets();

            firstClaims.Should().HaveCount(expectedClaims.Count);
            secondClaims.Should().HaveCount(expectedClaims.Count);
            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
        }

        private static List<ClaimSet> CreateLargeClaimSetPayload()
        {
            var largeNameSegment = new string('x', 1_024);
            return Enumerable
                .Range(0, 1_200)
                .Select(index => new ClaimSet(
                    $"ClaimSet{index}",
                    [
                        new ResourceClaim(
                            $"http://ed-fi.org/ods/identity/claims/domains/edFiTypes/{largeNameSegment}/{index}",
                            "Create",
                            [new AuthorizationStrategy($"Strategy{largeNameSegment}{index}")]
                        ),
                    ]
                ))
                .ToList();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Aborted_Waiter_On_The_Per_Key_Lock : ClaimSetCacheServiceTests
    {
        [Test]
        public async Task It_Stops_Waiting_On_The_Lock_While_A_Live_Waiter_Still_Completes()
        {
            var securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();
            var gate = new TaskCompletionSource();
            var liveClaims = new List<ClaimSet> { new("LiveClaimSet", []) };

            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .ReturnsLazily(async () =>
                {
                    await gate.Task;
                    return (IList<ClaimSet>)liveClaims;
                });

            var service = new CachedClaimSetProvider(
                securityMetadataProvider,
                CreateMemoryCache(),
                CreateCacheSettings(),
                TimeProvider.System,
                A.Fake<IHostApplicationLifetime>(),
                NullLogger<CachedClaimSetProvider>.Instance
            );

            // Holder: acquires the per-key lock and blocks inside the factory until the gate opens.
            Task<IList<ClaimSet>> holderTask = service.GetAllClaimSets();
            await Task.Delay(50);

            // Aborted waiter: queues behind the lock and never acquires it.
            using var abortedCts = new CancellationTokenSource();
            Task<IList<ClaimSet>> abortedWaiterTask = service.GetAllClaimSets(
                cancellationToken: abortedCts.Token
            );
            await Task.Delay(50);

            // Live waiter: also queues behind the lock, without cancelling.
            Task<IList<ClaimSet>> liveWaiterTask = service.GetAllClaimSets();
            await Task.Delay(50);

            var stopwatch = Stopwatch.StartNew();
            await abortedCts.CancelAsync();

            Func<Task> act = async () => await abortedWaiterTask;
            await act.Should().ThrowAsync<OperationCanceledException>();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));

            // The live waiter is still queued behind the holder - the aborted waiter leaving the
            // queue does not let it skip ahead of the holder still fetching.
            liveWaiterTask.IsCompleted.Should().BeFalse();

            gate.SetResult();

            IList<ClaimSet> liveResult = await liveWaiterTask;
            liveResult.Should().BeEquivalentTo(liveClaims);
            await holderTask;
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Caller_Holding_The_Fill_Is_Aborted : ClaimSetCacheServiceTests
    {
        [Test]
        public async Task It_Keeps_The_Shared_Fill_Running_For_A_Live_Waiter()
        {
            var securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();
            var gate = new TaskCompletionSource();
            var liveClaims = new List<ClaimSet> { new("LiveClaimSet", []) };
            var fillTokens = new List<CancellationToken>();

            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .ReturnsLazily(
                    async (string? _, CancellationToken token) =>
                    {
                        fillTokens.Add(token);
                        await gate.Task.WaitAsync(token);
                        return (IList<ClaimSet>)liveClaims;
                    }
                );

            var service = new CachedClaimSetProvider(
                securityMetadataProvider,
                CreateMemoryCache(),
                CreateCacheSettings(),
                TimeProvider.System,
                A.Fake<IHostApplicationLifetime>(),
                NullLogger<CachedClaimSetProvider>.Instance
            );

            // Holder: starts the fill and blocks inside it until the gate opens.
            using var abortedHolderCts = new CancellationTokenSource();
            Task<IList<ClaimSet>> abortedHolderTask = service.GetAllClaimSets(
                cancellationToken: abortedHolderCts.Token
            );
            await Task.Delay(50);

            // Live waiter: needs the same tenant's claim sets and does not cancel.
            Task<IList<ClaimSet>> liveWaiterTask = service.GetAllClaimSets();
            await Task.Delay(50);

            var stopwatch = Stopwatch.StartNew();
            await abortedHolderCts.CancelAsync();

            Func<Task> act = async () => await abortedHolderTask;
            await act.Should().ThrowAsync<OperationCanceledException>();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));

            // The holder leaving did not cancel the fetch the live waiter still needs.
            fillTokens.Should().ContainSingle().Which.IsCancellationRequested.Should().BeFalse();
            liveWaiterTask.IsCompleted.Should().BeFalse();

            gate.SetResult();

            IList<ClaimSet> liveResult = await liveWaiterTask.WaitAsync(TimeSpan.FromSeconds(2));
            liveResult.Should().BeEquivalentTo(liveClaims);
            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    /// <summary>Drives IMemoryCache expiry from a <see cref="FakeTimeProvider" />.</summary>
    protected sealed class FakeTimeProviderClock(FakeTimeProvider timeProvider) : ISystemClock
    {
        public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    }

    protected static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    protected static CachedClaimSetProvider CreateService(
        IConfigurationServiceClaimSetProvider securityMetadataProvider,
        IMemoryCache memoryCache,
        TimeProvider timeProvider,
        IHostApplicationLifetime? lifetime = null,
        CacheSettings? cacheSettings = null
    ) =>
        new(
            securityMetadataProvider,
            memoryCache,
            cacheSettings ?? CreateCacheSettings(),
            timeProvider,
            lifetime ?? A.Fake<IHostApplicationLifetime>(),
            NullLogger<CachedClaimSetProvider>.Instance
        );

    [TestFixture]
    [Parallelizable]
    public class Given_A_Fill_That_Completes_After_A_Reload_Invalidated_It : ClaimSetCacheServiceTests
    {
        private readonly List<ClaimSet> _preReloadClaims = [new("PreReload", [])];
        private readonly List<ClaimSet> _postReloadClaims = [new("PostReload", [])];
        private IConfigurationServiceClaimSetProvider _securityMetadataProvider = null!;
        private IList<ClaimSet> _afterReload = null!;

        [SetUp]
        public async Task Setup()
        {
            _securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();
            var gate = new TaskCompletionSource();
            A.CallTo(() =>
                    _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .ReturnsLazily(async () =>
                {
                    await gate.Task;
                    return (IList<ClaimSet>)_preReloadClaims;
                })
                .Once()
                .Then.Returns(Task.FromResult<IList<ClaimSet>>(_postReloadClaims));

            var service = CreateService(_securityMetadataProvider, CreateMemoryCache(), TimeProvider.System);

            // A fill starts before the reload and is still fetching when the reload invalidates.
            Task<IList<ClaimSet>> preReloadFill = service.GetAllClaimSets();
            await Task.Delay(50);
            await service.InvalidateCacheAsync();

            // The pre-reload fill finishes after the invalidation, before anything reads again.
            gate.SetResult();
            await preReloadFill;

            _afterReload = await service.GetAllClaimSets();
        }

        [Test]
        public void It_does_not_serve_the_pre_reload_result_after_the_reload()
        {
            _afterReload.Should().BeEquivalentTo(_postReloadClaims);
        }

        [Test]
        public void It_fetches_again_after_the_reload()
        {
            A.CallTo(() =>
                    _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .MustHaveHappenedTwiceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Fill_That_Completes_After_The_Reloads_Own_Fill : ClaimSetCacheServiceTests
    {
        private readonly List<ClaimSet> _preReloadClaims = [new("PreReload", [])];
        private readonly List<ClaimSet> _postReloadClaims = [new("PostReload", [])];
        private IList<ClaimSet> _reloadResult = null!;
        private IList<ClaimSet> _afterBothFills = null!;

        [SetUp]
        public async Task Setup()
        {
            var securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();
            var gate = new TaskCompletionSource();
            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .ReturnsLazily(async () =>
                {
                    await gate.Task;
                    return (IList<ClaimSet>)_preReloadClaims;
                })
                .Once()
                .Then.Returns(Task.FromResult<IList<ClaimSet>>(_postReloadClaims));

            var service = CreateService(securityMetadataProvider, CreateMemoryCache(), TimeProvider.System);

            Task<IList<ClaimSet>> preReloadFill = service.GetAllClaimSets();
            await Task.Delay(50);
            await service.InvalidateCacheAsync();
            _reloadResult = await service.GetAllClaimSets();

            // The pre-reload fill finishes last and must not overwrite the reload's entry.
            gate.SetResult();
            await preReloadFill;

            _afterBothFills = await service.GetAllClaimSets();
        }

        [Test]
        public void It_returns_the_post_reload_result_to_the_reload()
        {
            _reloadResult.Should().BeEquivalentTo(_postReloadClaims);
        }

        [Test]
        public void It_keeps_the_post_reload_result_cached()
        {
            _afterBothFills.Should().BeEquivalentTo(_postReloadClaims);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Fill_That_Exceeds_Its_Budget : ClaimSetCacheServiceTests
    {
        private CancellationToken _fillToken;
        private Exception? _exception;
        private int _fetchesAfterRetry;

        [SetUp]
        public async Task Setup()
        {
            var timeProvider = new FakeTimeProvider();
            var securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();
            var neverCompletes = new TaskCompletionSource<IList<ClaimSet>>();
            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .ReturnsLazily(
                    (string? _, CancellationToken token) =>
                    {
                        _fillToken = token;
                        return neverCompletes.Task;
                    }
                )
                .Once()
                .Then.Returns(Task.FromResult<IList<ClaimSet>>([new("Recovered", [])]));

            var service = CreateService(securityMetadataProvider, CreateMemoryCache(), timeProvider);

            Task<IList<ClaimSet>> caller = service.GetAllClaimSets();
            timeProvider.Advance(TimeSpan.FromSeconds(30));
            _exception = await CaptureExceptionAsync(caller);

            await service.GetAllClaimSets().WaitAsync(TimeSpan.FromSeconds(2));
            _fetchesAfterRetry = Fake.GetCalls(securityMetadataProvider).Count();
        }

        [Test]
        public void It_cancels_the_fetch_token_when_the_budget_elapses()
        {
            _fillToken.IsCancellationRequested.Should().BeTrue();
        }

        [Test]
        public void It_fails_the_waiting_caller_instead_of_hanging()
        {
            _exception.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_starts_a_new_fetch_on_the_next_call()
        {
            _fetchesAfterRetry.Should().Be(2);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Host_Shutdown_During_A_Fill : ClaimSetCacheServiceTests
    {
        private CancellationToken _fillToken;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            using var stoppingCts = new CancellationTokenSource();
            var lifetime = A.Fake<IHostApplicationLifetime>();
            A.CallTo(() => lifetime.ApplicationStopping).Returns(stoppingCts.Token);
            var securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();
            var neverCompletes = new TaskCompletionSource<IList<ClaimSet>>();
            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .ReturnsLazily(
                    (string? _, CancellationToken token) =>
                    {
                        _fillToken = token;
                        return neverCompletes.Task;
                    }
                );

            var service = CreateService(
                securityMetadataProvider,
                CreateMemoryCache(),
                new FakeTimeProvider(),
                lifetime
            );

            Task<IList<ClaimSet>> caller = service.GetAllClaimSets();
            await stoppingCts.CancelAsync();
            _exception = await CaptureExceptionAsync(caller);
        }

        [Test]
        public void It_cancels_the_fetch_token()
        {
            _fillToken.IsCancellationRequested.Should().BeTrue();
        }

        [Test]
        public void It_fails_the_waiting_caller_instead_of_hanging()
        {
            _exception.Should().BeAssignableTo<OperationCanceledException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Revoked_Claim_Set_And_A_Controlled_Clock : ClaimSetCacheServiceTests
    {
        private readonly List<ClaimSet> _grantedClaims = [new("Granted", [])];
        private readonly List<ClaimSet> _revokedClaims = [new("Revoked", [])];
        private IList<ClaimSet> _justBeforeExpiry = null!;
        private IList<ClaimSet> _atExpiry = null!;

        [SetUp]
        public async Task Setup()
        {
            var timeProvider = new FakeTimeProvider();
            var memoryCache = new MemoryCache(
                new MemoryCacheOptions { Clock = new FakeTimeProviderClock(timeProvider) }
            );
            var securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();
            A.CallTo(() =>
                    securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored, A<CancellationToken>._)
                )
                .Returns(Task.FromResult<IList<ClaimSet>>(_grantedClaims))
                .Once()
                .Then.Returns(Task.FromResult<IList<ClaimSet>>(_revokedClaims));

            var service = CreateService(
                securityMetadataProvider,
                memoryCache,
                timeProvider,
                cacheSettings: new CacheSettings { ClaimSetsCacheExpirationSeconds = 15 }
            );

            await service.GetAllClaimSets();
            timeProvider.Advance(TimeSpan.FromSeconds(14));
            _justBeforeExpiry = await service.GetAllClaimSets();
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            _atExpiry = await service.GetAllClaimSets();
        }

        [Test]
        public void It_keeps_serving_the_granted_claim_sets_until_expiry()
        {
            _justBeforeExpiry.Should().BeEquivalentTo(_grantedClaims);
        }

        [Test]
        public void It_serves_the_revoked_claim_sets_once_the_entry_expires()
        {
            _atExpiry.Should().BeEquivalentTo(_revokedClaims);
        }
    }
}
