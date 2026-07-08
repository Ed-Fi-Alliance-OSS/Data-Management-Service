// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

[TestFixture]
[Parallelizable]
public class LoggingMiddlewareTests
{
    private static (DefaultHttpContext Context, MemoryStream ResponseBody) CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/schemas";

        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        return (context, responseBody);
    }

    [Test]
    public async Task It_leaves_request_body_too_large_rejections_at_413()
    {
        var (context, responseBody) = CreateContext();
        var middleware = new LoggingMiddleware(_ =>
            throw new Microsoft.AspNetCore.Http.BadHttpRequestException(
                "Request body too large.",
                Microsoft.AspNetCore.Http.StatusCodes.Status413PayloadTooLarge
            )
        );
        var logger = A.Fake<ILogger<LoggingMiddleware>>();

        await FluentActions.Awaiting(() => middleware.Invoke(context, logger)).Should().NotThrowAsync();

        context
            .Response.StatusCode.Should()
            .Be(Microsoft.AspNetCore.Http.StatusCodes.Status413PayloadTooLarge);
        responseBody.ToArray().Should().BeEmpty();
    }

    [Test]
    public async Task It_keeps_normal_exceptions_on_the_existing_500_path()
    {
        var (context, responseBody) = CreateContext();
        var middleware = new LoggingMiddleware(_ => throw new InvalidOperationException("boom"));
        var logger = A.Fake<ILogger<LoggingMiddleware>>();

        Func<Task> invoke = () => middleware.Invoke(context, logger);

        await invoke.Should().ThrowAsync<InvalidOperationException>();

        context
            .Response.StatusCode.Should()
            .Be(Microsoft.AspNetCore.Http.StatusCodes.Status500InternalServerError);
        context.Response.ContentType.Should().Be("application/json");
        responseBody.ToArray().Should().NotBeEmpty();
    }
}
