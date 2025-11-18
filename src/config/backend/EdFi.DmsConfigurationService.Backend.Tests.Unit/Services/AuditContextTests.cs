// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DmsConfigurationService.Backend.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Services;

[TestFixture]
public class AuditContextTests
{
    private IHttpContextAccessor _httpContextAccessor = null!;
    private AuditContext _auditContext = null!;
    private HttpContext _httpContext = null!;

    [SetUp]
    public void Setup()
    {
        _httpContextAccessor = A.Fake<IHttpContextAccessor>();
        _httpContext = new DefaultHttpContext();
        A.CallTo(() => _httpContextAccessor.HttpContext).Returns(_httpContext);
        _auditContext = new AuditContext(_httpContextAccessor);
    }

    [TestFixture]
    public class GetCurrentUser : AuditContextTests
    {
        [Test]
        public void When_sub_claim_exists_returns_sub_value()
        {
            // Arrange
            var claims = new List<Claim>
            {
                new("sub", "test-user-123"),
                new("client_id", "client-456"),
                new("name", "Test User")
            };
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

            // Act
            var result = _auditContext.GetCurrentUser();

            // Assert
            result.Should().Be("test-user-123");
        }

        [Test]
        public void When_sub_missing_but_client_id_exists_returns_client_id()
        {
            // Arrange
            var claims = new List<Claim>
            {
                new("client_id", "client-456"),
                new("name", "Test User")
            };
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

            // Act
            var result = _auditContext.GetCurrentUser();

            // Assert
            result.Should().Be("client-456");
        }

        [Test]
        public void When_sub_and_client_id_missing_but_name_exists_returns_name()
        {
            // Arrange
            var claims = new List<Claim>
            {
                new("name", "Test User")
            };
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

            // Act
            var result = _auditContext.GetCurrentUser();

            // Assert
            result.Should().Be("Test User");
        }

        [Test]
        public void When_no_relevant_claims_exist_returns_system()
        {
            // Arrange
            var claims = new List<Claim>
            {
                new("some_other_claim", "value")
            };
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

            // Act
            var result = _auditContext.GetCurrentUser();

            // Assert
            result.Should().Be("system");
        }

        [Test]
        public void When_user_not_authenticated_returns_system()
        {
            // Arrange
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity()); // No authentication

            // Act
            var result = _auditContext.GetCurrentUser();

            // Assert
            result.Should().Be("system");
        }

        [Test]
        public void When_http_context_is_null_returns_system()
        {
            // Arrange
            A.CallTo(() => _httpContextAccessor.HttpContext).Returns(null);

            // Act
            var result = _auditContext.GetCurrentUser();

            // Assert
            result.Should().Be("system");
        }

        [Test]
        public void When_sub_claim_is_empty_falls_back_to_next_claim()
        {
            // Arrange
            var claims = new List<Claim>
            {
                new("sub", ""),
                new("client_id", "client-789")
            };
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

            // Act
            var result = _auditContext.GetCurrentUser();

            // Assert
            result.Should().Be("client-789");
        }

        [Test]
        public void When_sub_claim_is_whitespace_falls_back_to_next_claim()
        {
            // Arrange
            var claims = new List<Claim>
            {
                new("sub", "   "),
                new("name", "John Doe")
            };
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

            // Act
            var result = _auditContext.GetCurrentUser();

            // Assert
            result.Should().Be("John Doe");
        }
    }

    [TestFixture]
    public class GetCurrentTimestamp : AuditContextTests
    {
        [Test]
        public void Returns_utc_datetime()
        {
            // Arrange
            var beforeCall = DateTime.UtcNow;

            // Act
            var result = _auditContext.GetCurrentTimestamp();
            var afterCall = DateTime.UtcNow;

            // Assert
            result.Kind.Should().Be(DateTimeKind.Utc);
            result.Should().BeOnOrAfter(beforeCall);
            result.Should().BeOnOrBefore(afterCall);
        }

        [Test]
        public async Task Returns_different_timestamps_on_subsequent_calls()
        {
            // Act
            var timestamp1 = _auditContext.GetCurrentTimestamp();
            await Task.Delay(10); // Small delay to ensure time progresses
            var timestamp2 = _auditContext.GetCurrentTimestamp();

            // Assert
            timestamp2.Should().BeAfter(timestamp1);
        }

        [Test]
        public void Returns_timestamp_close_to_system_time()
        {
            // Act
            var systemTime = DateTime.UtcNow;
            var auditTime = _auditContext.GetCurrentTimestamp();

            // Assert
            var difference = (auditTime - systemTime).TotalSeconds;
            Math.Abs(difference).Should().BeLessThan(1.0, "timestamp should be within 1 second of system time");
        }
    }
}
