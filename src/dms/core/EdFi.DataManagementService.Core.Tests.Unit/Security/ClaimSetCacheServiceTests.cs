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

public class ClaimSetCacheServiceTests
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
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns(_expectedClaims);

            _service = new CachedClaimSetProvider(
                _securityMetadataProvider,
                CreateHybridCache(),
                CreateCacheSettings(),
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
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
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
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns(_expectedClaims);

            // Use the same HybridCache for both calls
            var hybridCache = CreateHybridCache();

            _service = new CachedClaimSetProvider(
                _securityMetadataProvider,
                hybridCache,
                CreateCacheSettings(),
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
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
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
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .Throws(
                    new HttpRequestException(
                        $"Error response from http://localhost. Error message: error. StatusCode: {statusCode}"
                    )
                );

            _service = new CachedClaimSetProvider(
                _securityMetadataProvider,
                CreateHybridCache(),
                CreateCacheSettings(),
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

            A.CallTo(() => securityMetadataProvider.GetAllClaimSets(A<string?>.That.IsEqualTo("Tenant1")))
                .Returns(expectedTenant1Claims);
            A.CallTo(() => securityMetadataProvider.GetAllClaimSets(A<string?>.That.IsEqualTo("Tenant2")))
                .Returns(expectedTenant2Claims);

            var service = new CachedClaimSetProvider(
                securityMetadataProvider,
                CreateHybridCache(),
                CreateCacheSettings(),
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

            A.CallTo(() => securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns(expectedClaims);

            var service = new CachedClaimSetProvider(
                securityMetadataProvider,
                CreateHybridCache(),
                CreateCacheSettings(),
                NullLogger<CachedClaimSetProvider>.Instance
            );

            // Act
            await service.GetAllClaimSets("Tenant1");
            await service.GetAllClaimSets("Tenant2");

            // Assert - verify provider was called twice (once per tenant)
            A.CallTo(() => securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .MustHaveHappened(2, Times.Exactly);
        }

        [Test]
        public async Task It_Should_Cache_Separately_For_Each_Tenant()
        {
            // Arrange
            var securityMetadataProvider = A.Fake<IConfigurationServiceClaimSetProvider>();

            var expectedClaims = new List<ClaimSet> { new("TestClaimSet", []) };

            A.CallTo(() => securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns(expectedClaims);

            var hybridCache = CreateHybridCache();
            var service = new CachedClaimSetProvider(
                securityMetadataProvider,
                hybridCache,
                CreateCacheSettings(),
                NullLogger<CachedClaimSetProvider>.Instance
            );

            // Act - call each tenant twice
            await service.GetAllClaimSets("Tenant1");
            await service.GetAllClaimSets("Tenant1"); // Should come from cache
            await service.GetAllClaimSets("Tenant2");
            await service.GetAllClaimSets("Tenant2"); // Should come from cache

            // Assert - provider should be called only once per tenant (2 times total)
            A.CallTo(() => securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .MustHaveHappened(2, Times.Exactly);
        }
    }
}
