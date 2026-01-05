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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

/// <summary>
/// Tests verifying that HybridCache provides stampede protection.
/// Under concurrent load, only one request should execute the factory function
/// while others wait for the result.
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
            A.CallTo(() => _mockProvider.GetAllClaimSets(A<string?>.Ignored))
                .ReturnsLazily(_ => FetchClaimSetsAsync());

            _cachedProvider = new CachedClaimSetProvider(
                _mockProvider,
                CreateHybridCache(),
                CreateCacheSettings(),
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

            A.CallTo(() => _mockProvider.GetApplicationByClientIdAsync(A<string>.Ignored))
                .ReturnsLazily(_ => FetchApplicationContextAsync());

            _cachedProvider = new CachedApplicationContextProvider(
                _mockProvider,
                CreateHybridCache(),
                CreateCacheSettings(),
                NullLogger<CachedApplicationContextProvider>.Instance
            );
        }

        private async Task<ApplicationContext?> FetchApplicationContextAsync()
        {
            Interlocked.Increment(ref _factoryExecutionCount);
            await Task.Delay(100);
            return new ApplicationContext(1, 100, "testClient", Guid.NewGuid(), [1, 2, 3]);
        }

        [Test]
        public async Task It_Should_Execute_Factory_Only_Once_For_Same_ClientId()
        {
            // Arrange
            const string clientId = "test-client";
            var tasks = Enumerable
                .Range(0, 10)
                .Select(_ => _cachedProvider.GetApplicationByClientIdAsync(clientId))
                .ToList();

            // Act
            var results = await Task.WhenAll(tasks);

            // Assert: Factory should execute only once for same clientId
            _factoryExecutionCount.Should().Be(1);

            // All results should be non-null
            results.Should().AllSatisfy(r => r.Should().NotBeNull());
        }

        [Test]
        public async Task It_Should_Execute_Factory_Separately_For_Different_ClientIds()
        {
            // Arrange - Reset factory count and reconfigure mock
            _factoryExecutionCount = 0;
            A.CallTo(() => _mockProvider.GetApplicationByClientIdAsync(A<string>.Ignored))
                .ReturnsLazily(call =>
                {
                    var clientId = call.GetArgument<string>(0) ?? "unknown";
                    return FetchApplicationContextForClientAsync(clientId);
                });

            var tasks = new[]
            {
                _cachedProvider.GetApplicationByClientIdAsync("client1"),
                _cachedProvider.GetApplicationByClientIdAsync("client2"),
                _cachedProvider.GetApplicationByClientIdAsync("client3"),
            };

            // Act
            await Task.WhenAll(tasks);

            // Assert: Each unique client should trigger factory once
            _factoryExecutionCount.Should().Be(3);
        }

        private async Task<ApplicationContext?> FetchApplicationContextForClientAsync(string clientId)
        {
            Interlocked.Increment(ref _factoryExecutionCount);
            await Task.Delay(50);
            return new ApplicationContext(1, 100, clientId, Guid.NewGuid(), [1, 2, 3]);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Concurrent_Token_Requests : StampedeProtectionTests
    {
        private ConfigurationServiceTokenHandler _tokenHandler = null!;
        private int _httpCallCount;

        [SetUp]
        public void Setup()
        {
            _httpCallCount = 0;

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            handler.SetResponse(
                "https://api.example.com/connect/token",
                new { Access_Token = "test-token", Expires_in = 1800 }
            );
            handler.OnRequest = () => Interlocked.Increment(ref _httpCallCount);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
            var mockApiClient = new ConfigurationServiceApiClient(httpClient);

            _tokenHandler = new ConfigurationServiceTokenHandler(
                CreateHybridCache(),
                CreateCacheSettings(),
                mockApiClient,
                NullLogger<ConfigurationServiceTokenHandler>.Instance
            );
        }

        [Test]
        public async Task It_Should_Fetch_Token_Only_Once_Under_Concurrent_Load()
        {
            // Arrange: Create 10 concurrent requests
            var tasks = Enumerable
                .Range(0, 10)
                .Select(_ => _tokenHandler.GetTokenAsync("clientId", "secret", "scope"))
                .ToList();

            // Act
            var tokens = await Task.WhenAll(tasks);

            // Assert: HTTP should only be called once
            _httpCallCount.Should().Be(1);

            // All tokens should be the same
            tokens.Should().AllBe("test-token");
        }
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

            A.CallTo(() => _mockProvider.GetAllClaimSets(A<string?>.That.IsEqualTo("tenant1")))
                .ReturnsLazily(_ => FetchTenant1ClaimSetsAsync());

            A.CallTo(() => _mockProvider.GetAllClaimSets(A<string?>.That.IsEqualTo("tenant2")))
                .ReturnsLazily(_ => FetchTenant2ClaimSetsAsync());

            _cachedProvider = new CachedClaimSetProvider(
                _mockProvider,
                CreateHybridCache(),
                CreateCacheSettings(),
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
