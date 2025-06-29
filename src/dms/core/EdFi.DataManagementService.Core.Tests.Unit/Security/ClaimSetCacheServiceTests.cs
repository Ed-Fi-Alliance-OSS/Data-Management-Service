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
        private readonly ISecurityMetadataProvider _securityMetadataProvider =
            A.Fake<ISecurityMetadataProvider>();
        private ClaimSetCacheService? _service;
        private IList<ClaimSet>? _claims;
        private IList<ClaimSet>? _expectedClaims;

        [SetUp]
        public async Task Setup()
        {
            _expectedClaims = [new("ClaimSet1", []), new("ClaimSet2", [])];
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets()).Returns(_expectedClaims);

            object? cached = null;
            A.CallTo(() => _memoryCache.TryGetValue(A<object>.Ignored, out cached)).Returns(false);

            var claimSetCache = new ClaimSetsCache(_memoryCache, TimeSpan.FromMinutes(10));

            _service = new ClaimSetCacheService(_securityMetadataProvider, claimSetCache);
            _claims = await _service.GetClaimSets();
        }

        [Test]
        public void Should_Return_Claims_Not_From_Cache()
        {
            _claims.Should().NotBeNull();
            _claims!.Count.Should().Be(2);
            _claims[0].Name.Should().Be("ClaimSet1");
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets()).MustHaveHappenedOnceExactly();
            A.CallTo(() => _memoryCache.CreateEntry(A<string>.Ignored)).MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Cache_Has_Claims : ClaimSetCacheServiceTests
    {
        private readonly IMemoryCache _memoryCache = A.Fake<IMemoryCache>();
        private readonly ISecurityMetadataProvider _securityMetadataProvider =
            A.Fake<ISecurityMetadataProvider>();
        private ClaimSetCacheService? _service;
        private IList<ClaimSet>? _claims;
        private IList<ClaimSet>? _expectedClaims;

        [SetUp]
        public async Task Setup()
        {
            _expectedClaims = [new("ClaimSet1", []), new("ClaimSet2", [])];
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets()).Returns(_expectedClaims);

            object? cached = _expectedClaims;
            A.CallTo(() => _memoryCache.TryGetValue(A<object>.Ignored, out cached)).Returns(true);

            var claimSetCache = new ClaimSetsCache(_memoryCache, TimeSpan.FromMinutes(10));

            _service = new ClaimSetCacheService(_securityMetadataProvider, claimSetCache);
            _claims = await _service.GetClaimSets();
        }

        [Test]
        public void Should_Return_Claims_From_Cache()
        {
            _claims.Should().NotBeNull();
            _claims!.Count.Should().Be(2);
            _claims[0].Name.Should().Be("ClaimSet1");
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets()).MustNotHaveHappened();
            A.CallTo(() => _memoryCache.CreateEntry(A<string>.Ignored)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Backend_Throws_Error : ConfigurationServiceTokenHandlerTests
    {
        private readonly IMemoryCache _memoryCache = A.Fake<IMemoryCache>();
        private readonly ISecurityMetadataProvider _securityMetadataProvider =
            A.Fake<ISecurityMetadataProvider>();
        private ClaimSetCacheService? _service;

        [Test]
        public void Should_Throw_Exception_For_BadRequest()
        {
            // Arrange
            SetClaimSetCacheService(HttpStatusCode.BadRequest);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetClaimSets());
        }

        [Test]
        public void Should_Throw_Exception_For_Unauthorized()
        {
            // Arrange
            SetClaimSetCacheService(HttpStatusCode.Unauthorized);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetClaimSets());
        }

        [Test]
        public void Should_Throw_Exception_For_NotFound()
        {
            // Arrange
            SetClaimSetCacheService(HttpStatusCode.NotFound);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetClaimSets());
        }

        [Test]
        public void Should_Throw_Exception_For_Forbidden()
        {
            // Arrange
            SetClaimSetCacheService(HttpStatusCode.Forbidden);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetClaimSets());
        }

        [Test]
        public void Should_Throw_Exception_For_InternalServerError()
        {
            // Arrange
            SetClaimSetCacheService(HttpStatusCode.InternalServerError);

            // Act & Assert
            Assert.ThrowsAsync<HttpRequestException>(async () => await _service!.GetClaimSets());
        }

        private void SetClaimSetCacheService(HttpStatusCode statusCode)
        {
            A.CallTo(() => _securityMetadataProvider.GetAllClaimSets())
                .Throws(
                    new HttpRequestException(
                        $"Error response from http://localhost. Error message: error. StatusCode: {statusCode}"
                    )
                );

            object? cached = null;
            A.CallTo(() => _memoryCache.TryGetValue(A<object>.Ignored, out cached)).Returns(true);

            var claimSetCache = new ClaimSetsCache(_memoryCache, TimeSpan.FromMinutes(10));

            _service = new ClaimSetCacheService(_securityMetadataProvider, claimSetCache);
        }
    }
}
