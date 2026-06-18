// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

[TestFixture]
[Parallelizable]
public class SecurityHeadersMiddlewareTests
{
    // DefaultHttpContext's response feature ignores OnStarting, so a fake captures the callback
    // and the response start is simulated by invoking it after the middleware runs.
    private static async Task<IHeaderDictionary> RunAndStartResponseAsync(
        Action<IHeaderDictionary>? seedUpstreamHeaders = null
    )
    {
        var headers = new HeaderDictionary();
        Func<object, Task>? onStarting = null;
        object? onStartingState = null;

        var responseFeature = A.Fake<IHttpResponseFeature>();
        A.CallTo(() => responseFeature.Headers).Returns(headers);
        A.CallTo(() => responseFeature.OnStarting(A<Func<object, Task>>._, A<object>._))
            .Invokes(
                (Func<object, Task> callback, object state) =>
                {
                    onStarting = callback;
                    onStartingState = state;
                }
            );

        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature());
        features.Set<IHttpResponseFeature>(responseFeature);
        var context = new DefaultHttpContext(features);

        seedUpstreamHeaders?.Invoke(context.Response.Headers);

        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);
        await middleware.Invoke(context);

        if (onStarting is not null)
        {
            await onStarting(onStartingState!);
        }

        return headers;
    }

    [Test]
    public async Task It_adds_x_content_type_options_nosniff()
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync();
        headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
    }

    [Test]
    public async Task It_adds_referrer_policy_no_referrer()
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync();
        headers["Referrer-Policy"].ToString().Should().Be("no-referrer");
    }

    [Test]
    public async Task It_does_not_overwrite_a_value_already_present()
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync(h =>
            h["X-Content-Type-Options"] = "custom"
        );
        headers["X-Content-Type-Options"].ToString().Should().Be("custom");
    }
}
