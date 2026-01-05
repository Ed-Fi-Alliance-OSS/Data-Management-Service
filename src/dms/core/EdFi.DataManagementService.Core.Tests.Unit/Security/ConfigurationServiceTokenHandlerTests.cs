// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

public class ConfigurationServiceTokenHandlerTests
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
    public class Given_Valid_Credentials : ConfigurationServiceTokenHandlerTests
    {
        private ConfigurationServiceTokenHandler? _configServiceTokenHandler;
        private readonly string _expectedToken = "valid-token";
        private string? _token;
        private int _httpCallCount;

        [SetUp]
        public async Task Setup()
        {
            _httpCallCount = 0;
            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            handler.SetResponse(
                "https://api.example.com/connect/token",
                new { Access_Token = "valid-token", Expires_in = 1800 }
            );
            handler.OnRequest = () => _httpCallCount++;

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
            var configServiceApiClient = new ConfigurationServiceApiClient(httpClient);

            _configServiceTokenHandler = new ConfigurationServiceTokenHandler(
                CreateHybridCache(),
                CreateCacheSettings(),
                configServiceApiClient,
                NullLogger<ConfigurationServiceTokenHandler>.Instance
            );
            _token = await _configServiceTokenHandler.GetTokenAsync("ClientId", "Secret", "Scope");
        }

        [Test]
        public void It_Should_Return_Valid_Token()
        {
            _token.Should().NotBeNullOrEmpty();
            _token.Should().Be(_expectedToken);
        }

        [Test]
        public void It_Should_Make_Http_Call_On_Cache_Miss()
        {
            _httpCallCount.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Subsequent_Request_After_Cache_Populated : ConfigurationServiceTokenHandlerTests
    {
        private ConfigurationServiceTokenHandler? _configServiceTokenHandler;
        private readonly string _expectedToken = "valid-token";
        private string? _firstToken;
        private string? _secondToken;
        private int _httpCallCount;

        [SetUp]
        public async Task Setup()
        {
            _httpCallCount = 0;
            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            handler.SetResponse(
                "https://api.example.com/connect/token",
                new { Access_Token = "valid-token", Expires_in = 1800 }
            );
            handler.OnRequest = () => _httpCallCount++;

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com") };
            var configServiceApiClient = new ConfigurationServiceApiClient(httpClient);

            // Use the same HybridCache instance for both calls
            var hybridCache = CreateHybridCache();

            _configServiceTokenHandler = new ConfigurationServiceTokenHandler(
                hybridCache,
                CreateCacheSettings(),
                configServiceApiClient,
                NullLogger<ConfigurationServiceTokenHandler>.Instance
            );

            // First call - should fetch from HTTP
            _firstToken = await _configServiceTokenHandler.GetTokenAsync("ClientId", "Secret", "Scope");

            // Second call - should return from cache
            _secondToken = await _configServiceTokenHandler.GetTokenAsync("ClientId", "Secret", "Scope");
        }

        [Test]
        public void It_Should_Return_Same_Token_For_Both_Calls()
        {
            _firstToken.Should().Be(_expectedToken);
            _secondToken.Should().Be(_expectedToken);
        }

        [Test]
        public void It_Should_Make_Only_One_Http_Call()
        {
            // Second request should come from cache
            _httpCallCount.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Api_Throws_Error : ConfigurationServiceTokenHandlerTests
    {
        private ConfigurationServiceTokenHandler? _configServiceTokenHandler;
        private TestHttpMessageHandler? _handler;

        [Test]
        public void It_Should_Throw_Exception_For_BadRequest()
        {
            SetConfigurationServiceTokenHandler(HttpStatusCode.BadRequest);

            Assert.ThrowsAsync<HttpRequestException>(async () =>
                await _configServiceTokenHandler!.GetTokenAsync("ClientId", "Secret", "Scope")
            );
        }

        [Test]
        public void It_Should_Throw_Exception_For_Unauthorized()
        {
            SetConfigurationServiceTokenHandler(HttpStatusCode.Unauthorized);

            Assert.ThrowsAsync<HttpRequestException>(async () =>
                await _configServiceTokenHandler!.GetTokenAsync("ClientId", "Secret", "Scope")
            );
        }

        [Test]
        public void It_Should_Throw_Exception_For_NotFound()
        {
            SetConfigurationServiceTokenHandler(HttpStatusCode.NotFound);

            Assert.ThrowsAsync<HttpRequestException>(async () =>
                await _configServiceTokenHandler!.GetTokenAsync("ClientId", "Secret", "Scope")
            );
        }

        [Test]
        public void It_Should_Throw_Exception_For_Forbidden()
        {
            SetConfigurationServiceTokenHandler(HttpStatusCode.Forbidden);

            Assert.ThrowsAsync<HttpRequestException>(async () =>
                await _configServiceTokenHandler!.GetTokenAsync("ClientId", "Secret", "Scope")
            );
        }

        [Test]
        public void It_Should_Throw_Exception_For_InternalServerError()
        {
            SetConfigurationServiceTokenHandler(HttpStatusCode.InternalServerError);

            Assert.ThrowsAsync<HttpRequestException>(async () =>
                await _configServiceTokenHandler!.GetTokenAsync("ClientId", "Secret", "Scope")
            );
        }

        private void SetConfigurationServiceTokenHandler(HttpStatusCode statusCode)
        {
            _handler = new TestHttpMessageHandler(statusCode);
            var configServiceHandler = new ConfigurationServiceResponseHandler(
                NullLogger<ConfigurationServiceResponseHandler>.Instance
            )
            {
                InnerHandler = _handler,
            };

            var httpClient = new HttpClient(configServiceHandler)
            {
                BaseAddress = new Uri("https://api.example.com"),
            };

            var configServiceApiClient = new ConfigurationServiceApiClient(httpClient);

            _configServiceTokenHandler = new ConfigurationServiceTokenHandler(
                CreateHybridCache(),
                CreateCacheSettings(),
                configServiceApiClient,
                NullLogger<ConfigurationServiceTokenHandler>.Instance
            );
        }
    }
}
