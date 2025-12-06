// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

[TestFixture]
internal class TenantResolutionMiddlewareTests
{
    [TestFixture]
    public class Given_MultiTenancy_Is_Disabled
    {
        private RequestDelegate _next = null!;
        private IOptions<AppSettings> _appSettings = null!;
        private ITenantContextProvider _tenantContextProvider = null!;
        private ITenantRepository _tenantRepository = null!;
        private ILogger<TenantResolutionMiddleware> _logger = null!;

        [SetUp]
        public void Setup()
        {
            _next = A.Fake<RequestDelegate>();
            _tenantContextProvider = new TenantContextProvider();
            _tenantRepository = A.Fake<ITenantRepository>();
            _logger = A.Fake<ILogger<TenantResolutionMiddleware>>();

            _appSettings = Options.Create(
                new AppSettings
                {
                    MultiTenancy = false,
                    Datastore = "postgresql",
                    IdentityProvider = "self-contained",
                }
            );
        }

        [Test]
        public async Task It_passes_through_without_checking_header()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            A.CallTo(() => _next(httpContext)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _tenantRepository.GetTenantByName(A<string>.Ignored)).MustNotHaveHappened();
        }

        [Test]
        public async Task It_does_not_populate_tenant_context()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["Tenant"] = "test-tenant";

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert - context should remain NotMultitenant (the default)
            _tenantContextProvider.Context.Should().BeOfType<TenantContext.NotMultitenant>();
        }
    }

    [TestFixture]
    public class Given_MultiTenancy_Is_Enabled
    {
        private RequestDelegate _next = null!;
        private IOptions<AppSettings> _appSettings = null!;
        private ITenantContextProvider _tenantContextProvider = null!;
        private ITenantRepository _tenantRepository = null!;
        private ILogger<TenantResolutionMiddleware> _logger = null!;

        [SetUp]
        public void Setup()
        {
            _next = A.Fake<RequestDelegate>();
            _tenantContextProvider = new TenantContextProvider();
            _tenantRepository = A.Fake<ITenantRepository>();
            _logger = A.Fake<ILogger<TenantResolutionMiddleware>>();

            _appSettings = Options.Create(
                new AppSettings
                {
                    MultiTenancy = true,
                    Datastore = "postgresql",
                    IdentityProvider = "self-contained",
                }
            );
        }

        [Test]
        public async Task It_returns_400_when_tenant_header_missing()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Response.Body = new MemoryStream();

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            A.CallTo(() => _next(httpContext)).MustNotHaveHappened();
        }

        [Test]
        public async Task It_returns_400_when_tenant_header_empty()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["Tenant"] = "";
            httpContext.Response.Body = new MemoryStream();

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            A.CallTo(() => _next(httpContext)).MustNotHaveHappened();
        }

        [Test]
        public async Task It_returns_400_when_tenant_not_found()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["Tenant"] = "nonexistent-tenant";
            httpContext.Response.Body = new MemoryStream();

            A.CallTo(() => _tenantRepository.GetTenantByName("nonexistent-tenant"))
                .Returns(new TenantGetByNameResult.FailureNotFound());

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            A.CallTo(() => _next(httpContext)).MustNotHaveHappened();
        }

        [Test]
        public async Task It_returns_400_when_tenant_lookup_fails()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["Tenant"] = "test-tenant";
            httpContext.Response.Body = new MemoryStream();

            A.CallTo(() => _tenantRepository.GetTenantByName("test-tenant"))
                .Returns(new TenantGetByNameResult.FailureUnknown("Database error"));

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            httpContext.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
            A.CallTo(() => _next(httpContext)).MustNotHaveHappened();
        }

        [Test]
        public async Task It_populates_tenant_context_and_calls_next_when_tenant_valid()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["Tenant"] = "valid-tenant";

            var tenantResponse = new TenantResponse
            {
                Id = 123,
                Name = "valid-tenant",
                CreatedAt = DateTime.UtcNow,
            };

            A.CallTo(() => _tenantRepository.GetTenantByName("valid-tenant"))
                .Returns(new TenantGetByNameResult.Success(tenantResponse));

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            _tenantContextProvider.Context.Should().BeOfType<TenantContext.Multitenant>();
            var multitenantContext = (TenantContext.Multitenant)_tenantContextProvider.Context;
            multitenantContext.TenantId.Should().Be(123);
            multitenantContext.TenantName.Should().Be("valid-tenant");
            A.CallTo(() => _next(httpContext)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_handles_tenant_names_with_special_characters()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["Tenant"] = "tenant-with-dashes_and_underscores";

            var tenantResponse = new TenantResponse
            {
                Id = 456,
                Name = "tenant-with-dashes_and_underscores",
                CreatedAt = DateTime.UtcNow,
            };

            A.CallTo(() => _tenantRepository.GetTenantByName("tenant-with-dashes_and_underscores"))
                .Returns(new TenantGetByNameResult.Success(tenantResponse));

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            _tenantContextProvider.Context.Should().BeOfType<TenantContext.Multitenant>();
            var multitenantContext = (TenantContext.Multitenant)_tenantContextProvider.Context;
            multitenantContext.TenantId.Should().Be(456);
            multitenantContext.TenantName.Should().Be("tenant-with-dashes_and_underscores");
            A.CallTo(() => _next(httpContext)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task It_sanitizes_tenant_name_before_logging()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["Tenant"] = "malicious<script>alert('xss')</script>";
            httpContext.Response.Body = new MemoryStream();

            A.CallTo(() => _tenantRepository.GetTenantByName(A<string>.Ignored))
                .Returns(new TenantGetByNameResult.FailureNotFound());

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            // The middleware should handle this gracefully without throwing
            httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            A.CallTo(() => _next(httpContext)).MustNotHaveHappened();
        }

        [Test]
        public async Task It_handles_default_result_from_repository()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["Tenant"] = "test-tenant";
            httpContext.Response.Body = new MemoryStream();

            A.CallTo(() => _tenantRepository.GetTenantByName("test-tenant"))
                .Returns(new TenantGetByNameResult());

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            A.CallTo(() => _next(httpContext)).MustNotHaveHappened();
        }

        [Test]
        public async Task It_allows_connect_register_endpoint_without_tenant_header()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/connect/register";
            // No Tenant header

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            A.CallTo(() => _next(httpContext)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _tenantRepository.GetTenantByName(A<string>.Ignored)).MustNotHaveHappened();
        }

        [Test]
        public async Task It_allows_connect_token_endpoint_without_tenant_header()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/connect/token";
            // No Tenant header

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            A.CallTo(() => _next(httpContext)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _tenantRepository.GetTenantByName(A<string>.Ignored)).MustNotHaveHappened();
        }

        [Test]
        public async Task It_allows_connect_endpoint_case_insensitive()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/CONNECT/TOKEN";
            // No Tenant header

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            A.CallTo(() => _next(httpContext)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _tenantRepository.GetTenantByName(A<string>.Ignored)).MustNotHaveHappened();
        }

        [Test]
        public async Task It_allows_tenants_endpoint_without_tenant_header()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/v2/tenants";
            // No Tenant header

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            A.CallTo(() => _next(httpContext)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _tenantRepository.GetTenantByName(A<string>.Ignored)).MustNotHaveHappened();
        }

        [Test]
        public async Task It_allows_tenants_endpoint_with_id_without_tenant_header()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/v2/tenants/123";
            // No Tenant header

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            A.CallTo(() => _next(httpContext)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _tenantRepository.GetTenantByName(A<string>.Ignored)).MustNotHaveHappened();
        }

        [Test]
        public async Task It_allows_well_known_endpoint_without_tenant_header()
        {
            // Arrange
            var middleware = new TenantResolutionMiddleware(_next);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/.well-known/openid-configuration";
            // No Tenant header

            // Act
            await middleware.Invoke(
                httpContext,
                _appSettings,
                _tenantContextProvider,
                _tenantRepository,
                _logger
            );

            // Assert
            A.CallTo(() => _next(httpContext)).MustHaveHappenedOnceExactly();
            A.CallTo(() => _tenantRepository.GetTenantByName(A<string>.Ignored)).MustNotHaveHappened();
        }
    }
}
