// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

internal class ReportInvalidConfigurationMiddlewareTests
{
    [TestFixture]
    public class Given_Configuration_Is_Invalid
    {
        [Test]
        public async Task It_returns_a_compliant_500_and_logs_configuration_errors()
        {
            // Arrange
            var next = A.Fake<RequestDelegate>();
            var logger = A.Fake<ILogger<ReportInvalidConfigurationMiddleware>>();
            var errors = new List<string>
            {
                "AppSettings:Foo is required",
                "IdentitySettings:Bar is invalid",
            };
            var middleware = new ReportInvalidConfigurationMiddleware(next, errors);

            var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

            // Act
            await middleware.Invoke(httpContext, logger);

            // Assert
            httpContext.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
            httpContext.Response.ContentType.Should().Be("application/problem+json");

            httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
            string content = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();

            // The configuration error messages are logged server-side, never exposed in the response body.
            content.Should().NotContain("AppSettings:Foo is required");
            content.Should().NotContain("IdentitySettings:Bar is invalid");

            var actual = JsonNode.Parse(content);
            actual!["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
            var expected = JsonNode.Parse(
                """
                {
                  "detail": "",
                  "type": "urn:ed-fi:api:internal-server-error",
                  "title": "Internal Server Error",
                  "status": 500,
                  "correlationId": "{correlationId}",
                  "validationErrors": {},
                  "errors": []
                }
                """.Replace("{correlationId}", actual!["correlationId"]!.GetValue<string>())
            );
            JsonNode.DeepEquals(actual, expected).Should().BeTrue();

            // The configuration errors are logged (LogCritical -> ILogger.Log), once per error.
            A.CallTo(logger).Where(call => call.Method.Name == "Log").MustHaveHappenedTwiceExactly();
            A.CallTo(() => next(httpContext)).MustNotHaveHappened();
        }
    }
}
