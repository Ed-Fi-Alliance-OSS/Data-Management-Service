// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

[TestFixture]
internal class SecurityHeadersMiddlewareTests
{
    // DefaultHttpContext's response feature ignores OnStarting, so a fake captures the callback
    // and the response start is simulated by invoking it after the middleware runs.
    private static async Task<IHeaderDictionary> RunAndStartResponseAsync(
        Action<IHeaderDictionary>? seedUpstreamHeaders = null,
        int statusCode = StatusCodes.Status200OK,
        string scheme = "http",
        string host = "cms.example",
        string environmentName = "Production"
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

        A.CallTo(() => responseFeature.StatusCode).Returns(statusCode);

        var requestFeature = new HttpRequestFeature { Scheme = scheme };
        requestFeature.Headers["Host"] = host;

        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(requestFeature);
        features.Set<IHttpResponseFeature>(responseFeature);
        var context = new DefaultHttpContext(features);

        seedUpstreamHeaders?.Invoke(context.Response.Headers);

        IHostEnvironment environment = A.Fake<IHostEnvironment>();
        A.CallTo(() => environment.EnvironmentName).Returns(environmentName);

        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask, environment);
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

    [Test]
    public async Task It_adds_x_frame_options_deny()
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync();
        headers["X-Frame-Options"].ToString().Should().Be("DENY");
    }

    [Test]
    public async Task It_adds_the_content_security_policy()
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync();
        headers["Content-Security-Policy"]
            .ToString()
            .Should()
            .Be("default-src 'none'; frame-ancestors 'none'");
    }

    [Test]
    public async Task It_adds_hsts_over_https_on_a_non_loopback_host()
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync(scheme: "https", host: "cms.example");
        headers["Strict-Transport-Security"].ToString().Should().Be("max-age=31536000");
    }

    [Test]
    public async Task It_does_not_add_hsts_over_http()
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync(scheme: "http", host: "cms.example");
        headers.ContainsKey("Strict-Transport-Security").Should().BeFalse();
    }

    [Test]
    public async Task It_does_not_add_hsts_over_https_for_a_non_loopback_host_in_development()
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync(
            scheme: "https",
            host: "cms.example",
            environmentName: Environments.Development
        );
        headers.ContainsKey("Strict-Transport-Security").Should().BeFalse();
    }

    [TestCase("localhost")]
    [TestCase("LOCALHOST")]
    [TestCase("127.0.0.1")]
    [TestCase("[::1]")]
    [TestCase("localhost:5126")]
    public async Task It_does_not_add_hsts_for_a_loopback_host(string host)
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync(scheme: "https", host: host);
        headers.ContainsKey("Strict-Transport-Security").Should().BeFalse();
    }

    [TestCase("X-Frame-Options", "SAMEORIGIN")]
    [TestCase("Content-Security-Policy", "default-src 'self'")]
    [TestCase("Strict-Transport-Security", "max-age=60")]
    public async Task It_does_not_overwrite_a_preset_header(string name, string value)
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync(
            h => h[name] = value,
            scheme: "https",
            host: "cms.example"
        );
        headers[name].ToString().Should().Be(value);
    }

    [Test]
    public async Task It_does_not_add_cache_control_to_a_200()
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync(statusCode: 200);
        headers.ContainsKey("Cache-Control").Should().BeFalse();
    }

    [TestCase(400)]
    [TestCase(401)]
    [TestCase(404)]
    [TestCase(500)]
    public async Task It_adds_no_store_to_a_failure_response(int statusCode)
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync(statusCode: statusCode);
        headers["Cache-Control"].ToString().Should().Be("no-store");
    }

    [Test]
    public async Task It_does_not_add_cache_control_to_a_304()
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync(statusCode: 304);
        headers.ContainsKey("Cache-Control").Should().BeFalse();
    }

    [Test]
    public async Task It_does_not_overwrite_a_preset_cache_control_on_a_404()
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync(
            h => h["Cache-Control"] = "no-cache",
            statusCode: 404
        );
        headers["Cache-Control"].ToString().Should().Be("no-cache");
    }
}
