// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Security.Tests.Unit;

public class ConfigurationServiceTokenHandlerTests
{
    [TestFixture]
    public class Given_Valid_Credentials : ConfigurationServiceTokenHandlerTests
    {
        private ConfigurationServiceTokenHandler? _configServiceTokenHandler;
        private readonly IMemoryCache _memoryCache = A.Fake<IMemoryCache>();
        private readonly string? _expectedToken = "valid-token";
        private string? _token;

        [SetUp]
        public async Task Setup()
        {
            var httpClientFactory = A.Fake<IHttpClientFactory>();

            var json = JsonSerializer.Serialize(new { Access_Token = "valid-token", Expires_in = 1800 });
            var httpClient = new HttpClient(new TestHttpMessageHandler(HttpStatusCode.OK, json))
            {
                BaseAddress = new Uri("https://api.example.com"),
            };

            A.CallTo(() => httpClientFactory.CreateClient(A<string>.Ignored)).Returns(httpClient);

            ConfigurationServiceApiClient _configServiceApiClient = new(httpClient);

            object? cached = null;
            A.CallTo(() => _memoryCache.TryGetValue(A<object>.Ignored, out cached)).Returns(false);

            _configServiceTokenHandler = new ConfigurationServiceTokenHandler(
                _memoryCache,
                _configServiceApiClient,
                NullLogger<ConfigurationServiceTokenHandler>.Instance
            );
            _token = await _configServiceTokenHandler.GetTokenAsync("ClientId", "Secret", "Scope");
        }

        [Test]
        public void Should_Return_Valid_Token_Not_From_Cache()
        {
            _token.Should().NotBeNullOrEmpty();
            _token.Should().Be(_expectedToken);
            A.CallTo(() => _memoryCache.CreateEntry(A<string>.Ignored)).MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_Cache_Has_Valid_Token : ConfigurationServiceTokenHandlerTests
    {
        private ConfigurationServiceTokenHandler? _configServiceTokenHandler;
        private readonly IMemoryCache _memoryCache = A.Fake<IMemoryCache>();
        private readonly string? _expectedToken = "valid-token";
        private string? _token;

        [SetUp]
        public async Task Setup()
        {
            var httpClientFactory = A.Fake<IHttpClientFactory>();

            var json = JsonSerializer.Serialize(new { Access_Token = "valid-token", Expires_in = 1800 });
            var httpClient = new HttpClient(new TestHttpMessageHandler(HttpStatusCode.OK, json))
            {
                BaseAddress = new Uri("https://api.example.com"),
            };

            A.CallTo(() => httpClientFactory.CreateClient(A<string>.Ignored)).Returns(httpClient);

            ConfigurationServiceApiClient _configServiceApiClient = new(httpClient);

            object? cached = _expectedToken;
            A.CallTo(() => _memoryCache.TryGetValue(A<object>.Ignored, out cached)).Returns(true);

            _configServiceTokenHandler = new ConfigurationServiceTokenHandler(
                _memoryCache,
                _configServiceApiClient,
                NullLogger<ConfigurationServiceTokenHandler>.Instance
            );
            _token = await _configServiceTokenHandler.GetTokenAsync("ClientId", "Secret", "Scope");
        }

        [Test]
        public void Should_Return_Valid_Token_From_Cache()
        {
            object? cached = _expectedToken;

            _token.Should().NotBeNullOrEmpty();
            _token.Should().Be(_expectedToken);
            A.CallTo(() => _memoryCache.CreateEntry(A<string>.Ignored)).MustNotHaveHappened();
            A.CallTo(() => _memoryCache.TryGetValue(A<object>.Ignored, out cached))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_Api_Throws_Error : ConfigurationServiceTokenHandlerTests
    {
        private ConfigurationServiceTokenHandler? _configServiceTokenHandler;
        private readonly IMemoryCache _memoryCache = A.Fake<IMemoryCache>();
        private TestHttpMessageHandler? _handler = null;

        [Test]
        public void Should_Throw_BadRequest()
        {
            // Arrange
            SetConfigurationServiceTokenHandler(HttpStatusCode.BadRequest);

            // Act & Assert
            Assert
                .ThrowsAsync<ConfigurationServiceException>(
                    async () => await _configServiceTokenHandler!.GetTokenAsync("ClientId", "Secret", "Scope")
                )!
                .StatusCode.Should()
                .Be(HttpStatusCode.BadRequest);
        }

        [Test]
        public void Should_Throw_Unauthorized()
        {
            // Arrange
            SetConfigurationServiceTokenHandler(HttpStatusCode.Unauthorized);

            // Act & Assert
            Assert
                .ThrowsAsync<ConfigurationServiceException>(
                    async () => await _configServiceTokenHandler!.GetTokenAsync("ClientId", "Secret", "Scope")
                )!
                .StatusCode.Should()
                .Be(HttpStatusCode.Unauthorized);
        }

        [Test]
        public void Should_Throw_NotFound()
        {
            // Arrange
            SetConfigurationServiceTokenHandler(HttpStatusCode.NotFound);

            // Act & Assert
            Assert
                .ThrowsAsync<ConfigurationServiceException>(
                    async () => await _configServiceTokenHandler!.GetTokenAsync("ClientId", "Secret", "Scope")
                )!
                .StatusCode.Should()
                .Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void Should_Throw_Forbidden()
        {
            // Arrange
            SetConfigurationServiceTokenHandler(HttpStatusCode.Forbidden);

            // Act & Assert
            Assert
                .ThrowsAsync<ConfigurationServiceException>(
                    async () => await _configServiceTokenHandler!.GetTokenAsync("ClientId", "Secret", "Scope")
                )!
                .StatusCode.Should()
                .Be(HttpStatusCode.Forbidden);
        }

        [Test]
        public void Should_Throw_InternalServerError()
        {
            // Arrange
            SetConfigurationServiceTokenHandler(HttpStatusCode.InternalServerError);

            // Act & Assert
            Assert
                .ThrowsAsync<ConfigurationServiceException>(
                    async () => await _configServiceTokenHandler!.GetTokenAsync("ClientId", "Secret", "Scope")
                )!
                .StatusCode.Should()
                .Be(HttpStatusCode.InternalServerError);
        }

        private void SetConfigurationServiceTokenHandler(HttpStatusCode statusCode)
        {
            _handler = new TestHttpMessageHandler(statusCode);
            var configServiceHandler = new ConfigurationServiceResponseHandler { InnerHandler = _handler };
            var httpClientFactory = A.Fake<IHttpClientFactory>();

            var httpClient = new HttpClient(configServiceHandler!)
            {
                BaseAddress = new Uri("https://api.example.com"),
            };

            A.CallTo(() => httpClientFactory.CreateClient(A<string>.Ignored)).Returns(httpClient);

            ConfigurationServiceApiClient _configServiceApiClient = new(httpClient);

            object? cached = null;
            A.CallTo(() => _memoryCache.TryGetValue(A<object>.Ignored, out cached)).Returns(false);

            _configServiceTokenHandler = new ConfigurationServiceTokenHandler(
                _memoryCache,
                _configServiceApiClient,
                NullLogger<ConfigurationServiceTokenHandler>.Instance
            );
        }
    }
}
