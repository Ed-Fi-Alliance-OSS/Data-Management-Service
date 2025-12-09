// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

public class ClaimSetCacheServiceTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_Service_Receives_Expected_Data : ClaimSetCacheServiceTests
    {
        private readonly IMemoryCache _memoryCache = A.Fake<IMemoryCache>();
        private readonly IClaimSetProvider _securityMetadataProvider = A.Fake<IClaimSetProvider>();
        private CachedClaimSetProvider? _service;
        private IList<ClaimSet>? _claims;
        private IList<ClaimSet>? _expectedClaims;

        [SetUp]
        public async Task Setup()
        {
            _expectedClaims = [new("ClaimSet1", []), new("ClaimSet2", [])];
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns(_expectedClaims);

            object? cached = null;
            A.CallTo(() => _memoryCache.TryGetValue(A<object>.Ignored, out cached)).Returns(false);

            var claimSetCache = new ClaimSetsCache(_memoryCache, TimeSpan.FromMinutes(10));

            _service = new CachedClaimSetProvider(_securityMetadataProvider, claimSetCache);
            _claims = await _service.GetAllClaimSets();
        }

        [Test]
        public void Should_Return_Claims_Not_From_Cache()
        {
            _claims.Should().NotBeNull();
            _claims!.Count.Should().Be(2);
            _claims[0].Name.Should().Be("ClaimSet1");
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => _memoryCache.CreateEntry(A<object>.Ignored)).MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Cache_Has_Claims : ClaimSetCacheServiceTests
    {
        private readonly IMemoryCache _memoryCache = A.Fake<IMemoryCache>();
        private readonly IClaimSetProvider _securityMetadataProvider = A.Fake<IClaimSetProvider>();
        private CachedClaimSetProvider? _service;
        private IList<ClaimSet>? _claims;
        private IList<ClaimSet>? _expectedClaims;

        [SetUp]
        public async Task Setup()
        {
            _expectedClaims = [new("ClaimSet1", []), new("ClaimSet2", [])];
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns(_expectedClaims);

            object? cached = _expectedClaims;
            A.CallTo(() => _memoryCache.TryGetValue(A<object>.Ignored, out cached)).Returns(true);

            var claimSetCache = new ClaimSetsCache(_memoryCache, TimeSpan.FromMinutes(10));

            _service = new CachedClaimSetProvider(_securityMetadataProvider, claimSetCache);
            _claims = await _service.GetAllClaimSets();
        }

        [Test]
        public void Should_Return_Claims_From_Cache()
        {
            _claims.Should().NotBeNull();
            _claims!.Count.Should().Be(2);
            _claims[0].Name.Should().Be("ClaimSet1");
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .MustNotHaveHappened();
            A.CallTo(() => _memoryCache.CreateEntry(A<object>.Ignored)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Backend_Throws_Error : ConfigurationServiceTokenHandlerTests
    {
        private readonly IMemoryCache _memoryCache = A.Fake<IMemoryCache>();
        private readonly IClaimSetProvider _securityMetadataProvider = A.Fake<IClaimSetProvider>();
        private CachedClaimSetProvider? _service;

        [Test]
        public void Should_Throw_Exception_For_BadRequest()
        {
            // Arrange
            SetClaimSetCacheService(HttpStatusCode.BadRequest);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetAllClaimSets());
        }

        [Test]
        public void Should_Throw_Exception_For_Unauthorized()
        {
            // Arrange
            SetClaimSetCacheService(HttpStatusCode.Unauthorized);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetAllClaimSets());
        }

        [Test]
        public void Should_Throw_Exception_For_NotFound()
        {
            // Arrange
            SetClaimSetCacheService(HttpStatusCode.NotFound);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetAllClaimSets());
        }

        [Test]
        public void Should_Throw_Exception_For_Forbidden()
        {
            // Arrange
            SetClaimSetCacheService(HttpStatusCode.Forbidden);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetAllClaimSets());
        }

        [Test]
        public void Should_Throw_Exception_For_InternalServerError()
        {
            // Arrange
            SetClaimSetCacheService(HttpStatusCode.InternalServerError);

            // Act & Assert
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

            object? cached = null;
            A.CallTo(() => _memoryCache.TryGetValue(A<object>.Ignored, out cached)).Returns(true);

            var claimSetCache = new ClaimSetsCache(_memoryCache, TimeSpan.FromMinutes(10));

            _service = new CachedClaimSetProvider(_securityMetadataProvider, claimSetCache);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_GetCacheKey_Tests : ClaimSetCacheServiceTests
    {
        [Test]
        public void When_Tenant_Is_Null_Should_Return_Base_Cache_Key()
        {
            var cacheKey = ClaimSetsCache.GetCacheKey(null);
            cacheKey.Should().Be("ClaimSetsCache");
        }

        [Test]
        public void When_Tenant_Is_Empty_Should_Return_Base_Cache_Key()
        {
            var cacheKey = ClaimSetsCache.GetCacheKey("");
            cacheKey.Should().Be("ClaimSetsCache");
        }

        [Test]
        public void When_Tenant_Is_Specified_Should_Return_Tenant_Keyed_Cache_Key()
        {
            var cacheKey = ClaimSetsCache.GetCacheKey("Tenant1");
            cacheKey.Should().Be("ClaimSetsCache:Tenant1");
        }

        [Test]
        public void When_Different_Tenants_Should_Return_Different_Cache_Keys()
        {
            var cacheKey1 = ClaimSetsCache.GetCacheKey("TenantA");
            var cacheKey2 = ClaimSetsCache.GetCacheKey("TenantB");

            cacheKey1.Should().NotBe(cacheKey2);
            cacheKey1.Should().Be("ClaimSetsCache:TenantA");
            cacheKey2.Should().Be("ClaimSetsCache:TenantB");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Tenant_Specific_Caching : ClaimSetCacheServiceTests
    {
        [Test]
        public async Task Should_Fetch_Different_Claims_For_Different_Tenants()
        {
            // Arrange
            var memoryCache = A.Fake<IMemoryCache>();
            var securityMetadataProvider = A.Fake<IClaimSetProvider>();

            var expectedTenant1Claims = new List<ClaimSet> { new("Tenant1ClaimSet", []) };
            var expectedTenant2Claims = new List<ClaimSet> { new("Tenant2ClaimSet", []) };

            // Setup provider to return different claims based on tenant argument
            A.CallTo(() => securityMetadataProvider.GetAllClaimSets(A<string?>.That.IsEqualTo("Tenant1")))
                .Returns(expectedTenant1Claims);
            A.CallTo(() => securityMetadataProvider.GetAllClaimSets(A<string?>.That.IsEqualTo("Tenant2")))
                .Returns(expectedTenant2Claims);

            // No cache hit for either tenant initially
            object? cached = null;
            A.CallTo(() => memoryCache.TryGetValue(A<object>.Ignored, out cached)).Returns(false);

            var claimSetCache = new ClaimSetsCache(memoryCache, TimeSpan.FromMinutes(10));
            var service = new CachedClaimSetProvider(securityMetadataProvider, claimSetCache);

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
        public async Task Should_Call_Provider_For_Each_Tenant()
        {
            // Arrange
            var memoryCache = A.Fake<IMemoryCache>();
            var securityMetadataProvider = A.Fake<IClaimSetProvider>();

            var expectedClaims = new List<ClaimSet> { new("TestClaimSet", []) };

            A.CallTo(() => securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns(expectedClaims);

            object? cached = null;
            A.CallTo(() => memoryCache.TryGetValue(A<object>.Ignored, out cached)).Returns(false);

            var claimSetCache = new ClaimSetsCache(memoryCache, TimeSpan.FromMinutes(10));
            var service = new CachedClaimSetProvider(securityMetadataProvider, claimSetCache);

            // Act
            await service.GetAllClaimSets("Tenant1");
            await service.GetAllClaimSets("Tenant2");

            // Assert - verify provider was called twice (once per tenant)
            A.CallTo(() => securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .MustHaveHappened(2, Times.Exactly);
        }

        [Test]
        public async Task Should_Cache_With_Tenant_Specific_Keys()
        {
            // Arrange
            var memoryCache = A.Fake<IMemoryCache>();
            var securityMetadataProvider = A.Fake<IClaimSetProvider>();

            var expectedClaims = new List<ClaimSet> { new("TestClaimSet", []) };

            A.CallTo(() => securityMetadataProvider.GetAllClaimSets(A<string?>.Ignored))
                .Returns(expectedClaims);

            object? cached = null;
            A.CallTo(() => memoryCache.TryGetValue(A<object>.Ignored, out cached)).Returns(false);

            var claimSetCache = new ClaimSetsCache(memoryCache, TimeSpan.FromMinutes(10));
            var service = new CachedClaimSetProvider(securityMetadataProvider, claimSetCache);

            // Act
            await service.GetAllClaimSets("Tenant1");
            await service.GetAllClaimSets("Tenant2");

            // Assert - verify CreateEntry was called twice (once per tenant)
            A.CallTo(() => memoryCache.CreateEntry(A<object>.Ignored)).MustHaveHappened(2, Times.Exactly);
        }
    }
}
