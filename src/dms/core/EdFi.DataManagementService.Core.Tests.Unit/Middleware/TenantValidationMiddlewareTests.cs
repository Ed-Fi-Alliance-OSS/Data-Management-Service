// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class TenantValidationMiddlewareTests
{
    private static IPipelineStep CreateMiddleware(bool multiTenancyEnabled)
    {
        // Use reflection to create the internal class
        var middlewareType = typeof(ApiService).Assembly.GetType(
            "EdFi.DataManagementService.Core.Middleware.TenantValidationMiddleware"
        );
        return (IPipelineStep)
            Activator.CreateInstance(middlewareType!, multiTenancyEnabled, NullLogger.Instance)!;
    }

    private static RequestInfo CreateRequestInfo(string? tenant)
    {
        return new RequestInfo(
            new FrontendRequest(
                Body: null,
                Headers: [],
                Path: "/ed-fi/schools",
                QueryParameters: [],
                TraceId: new TraceId("test-trace-id"),
                RouteQualifiers: [],
                Tenant: tenant
            ),
            RequestMethod.GET
        );
    }

    [TestFixture]
    public class Given_MultiTenancy_Disabled
    {
        [Test]
        public async Task It_Should_Call_Next_When_Tenant_Is_Null()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: false);
            var requestInfo = CreateRequestInfo(tenant: null);
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeTrue();
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public async Task It_Should_Call_Next_When_Tenant_Is_Provided()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: false);
            var requestInfo = CreateRequestInfo(tenant: "tenant1");
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeTrue();
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_MultiTenancy_Enabled
    {
        private static string GetResponseBodyAsString(IFrontendResponse response)
        {
            return response.Body?.ToJsonString() ?? string.Empty;
        }

        [Test]
        public async Task It_Should_Return_400_When_Tenant_Is_Missing()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: true);
            var requestInfo = CreateRequestInfo(tenant: null);
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeFalse();
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            GetResponseBodyAsString(requestInfo.FrontendResponse)
                .Should()
                .Contain("tenant identifier is required");
        }

        [Test]
        public async Task It_Should_Return_400_When_Tenant_Is_Empty()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: true);
            var requestInfo = CreateRequestInfo(tenant: "");
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeFalse();
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            GetResponseBodyAsString(requestInfo.FrontendResponse)
                .Should()
                .Contain("tenant identifier is required");
        }

        [Test]
        public async Task It_Should_Call_Next_When_Tenant_Is_Valid()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: true);
            var requestInfo = CreateRequestInfo(tenant: "tenant1");
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeTrue();
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public async Task It_Should_Accept_Tenant_With_Hyphens()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: true);
            var requestInfo = CreateRequestInfo(tenant: "tenant-one");
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeTrue();
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public async Task It_Should_Accept_Tenant_With_Underscores()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: true);
            var requestInfo = CreateRequestInfo(tenant: "tenant_one");
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeTrue();
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public async Task It_Should_Accept_Tenant_With_Numbers()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: true);
            var requestInfo = CreateRequestInfo(tenant: "tenant123");
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeTrue();
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public async Task It_Should_Return_400_When_Tenant_Has_Special_Characters()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: true);
            var requestInfo = CreateRequestInfo(tenant: "tenant@one");
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeFalse();
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            GetResponseBodyAsString(requestInfo.FrontendResponse)
                .Should()
                .Contain("alphanumeric characters, hyphens, and underscores");
        }

        [Test]
        public async Task It_Should_Return_400_When_Tenant_Has_Spaces()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: true);
            var requestInfo = CreateRequestInfo(tenant: "tenant one");
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeFalse();
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            GetResponseBodyAsString(requestInfo.FrontendResponse)
                .Should()
                .Contain("alphanumeric characters, hyphens, and underscores");
        }

        [Test]
        public async Task It_Should_Return_400_When_Tenant_Has_Dots()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: true);
            var requestInfo = CreateRequestInfo(tenant: "tenant.one");
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeFalse();
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            GetResponseBodyAsString(requestInfo.FrontendResponse)
                .Should()
                .Contain("alphanumeric characters, hyphens, and underscores");
        }

        [Test]
        public async Task It_Should_Return_400_When_Tenant_Exceeds_Max_Length()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: true);
            var longTenant = new string('a', 101); // 101 characters exceeds the 100 character limit
            var requestInfo = CreateRequestInfo(tenant: longTenant);
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeFalse();
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            GetResponseBodyAsString(requestInfo.FrontendResponse).Should().Contain("exceeds maximum length");
        }

        [Test]
        public async Task It_Should_Accept_Tenant_At_Max_Length()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: true);
            var maxLengthTenant = new string('a', 100); // Exactly 100 characters
            var requestInfo = CreateRequestInfo(tenant: maxLengthTenant);
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeTrue();
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public async Task It_Should_Return_400_When_Tenant_Has_Newlines()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: true);
            var requestInfo = CreateRequestInfo(tenant: "tenant\none");
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeFalse();
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public async Task It_Should_Return_400_When_Tenant_Has_Slashes()
        {
            // Arrange
            var middleware = CreateMiddleware(multiTenancyEnabled: true);
            var requestInfo = CreateRequestInfo(tenant: "tenant/one");
            var nextCalled = false;

            // Act
            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            // Assert
            nextCalled.Should().BeFalse();
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }
    }
}
