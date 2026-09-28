// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

/// <summary>
/// Covers how ConfigurationServiceClaimSetProvider treats the shape of the authorization metadata
/// response body, independent of the header contract covered by
/// ConfigurationServiceClaimSetProviderHeaderTests.
/// </summary>
public class ConfigurationServiceClaimSetProviderTests
{
    private sealed class StaticResponseHandler(string responseBody) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            CallCount++;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    private static ConfigurationServiceClaimSetProvider CreateProvider(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://cms.example/") };
        var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
        A.CallTo(() =>
                tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._)
            )
            .Returns("cms-token");

        return new ConfigurationServiceClaimSetProvider(
            new ConfigurationServiceApiClient(client),
            tokenHandler,
            new ConfigurationServiceContext("dms-client", "dms-secret", "fullaccess")
        );
    }

    [TestFixture]
    public class Given_A_Response_Body_Of_Null
    {
        private StaticResponseHandler _handler = null!;
        private ConfigurationServiceClaimSetProvider _provider = null!;
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _handler = new StaticResponseHandler("null");
            _provider = CreateProvider(_handler);
            _act = () => _provider.GetAllClaimSets();
        }

        [Test]
        public async Task It_Throws_A_Json_Exception()
        {
            await _act.Should().ThrowAsync<JsonException>();
        }

        [Test]
        public async Task It_Is_Not_Cached_By_The_Cached_Claim_Set_Provider()
        {
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var cachedProvider = new CachedClaimSetProvider(
                _provider,
                memoryCache,
                new CacheSettings(),
                NullLogger<CachedClaimSetProvider>.Instance
            );

            Func<Task> cachedAct = () => cachedProvider.GetAllClaimSets();

            await cachedAct.Should().ThrowAsync<JsonException>();
            memoryCache.TryGetValue(CachedClaimSetProvider.GetCacheKey(null), out _).Should().BeFalse();

            // A second call still reaches the Configuration Service rather than serving a cached failure.
            await cachedAct.Should().ThrowAsync<JsonException>();
            _handler.CallCount.Should().Be(2);
        }
    }

    [TestFixture]
    public class Given_A_Response_Body_Containing_A_Null_Entry
    {
        private ConfigurationServiceClaimSetProvider _provider = null!;
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            _provider = CreateProvider(new StaticResponseHandler("[null]"));
            _act = () => _provider.GetAllClaimSets();
        }

        [Test]
        public async Task It_Throws_A_Json_Exception()
        {
            await _act.Should().ThrowAsync<JsonException>();
        }
    }

    [TestFixture]
    public class Given_A_Response_Body_Of_An_Empty_Array
    {
        private IList<ClaimSet> _result = null!;

        [SetUp]
        public async Task Setup()
        {
            ConfigurationServiceClaimSetProvider provider = CreateProvider(new StaticResponseHandler("[]"));
            _result = await provider.GetAllClaimSets();
        }

        [Test]
        public void It_Returns_An_Empty_List()
        {
            _result.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_A_Normal_Response_Body
    {
        private IList<ClaimSet> _result = null!;

        [SetUp]
        public async Task Setup()
        {
            string body = JsonSerializer.Serialize(
                new List<ClaimSetMetadata> { new("TestClaimSet", [], []) }
            );
            ConfigurationServiceClaimSetProvider provider = CreateProvider(new StaticResponseHandler(body));
            _result = await provider.GetAllClaimSets();
        }

        [Test]
        public void It_Returns_The_Parsed_Claim_Set()
        {
            _result.Should().ContainSingle().Which.Name.Should().Be("TestClaimSet");
        }
    }
}
