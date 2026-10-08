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
        Action<IHeaderDictionary>? seedUpstreamHeaders = null,
        int statusCode = StatusCodes.Status200OK
    )
    {
        var headers = new HeaderDictionary();
        Func<object, Task>? onStarting = null;
        object? onStartingState = null;

        var responseFeature = A.Fake<IHttpResponseFeature>();
        A.CallTo(() => responseFeature.Headers).Returns(headers);

        // The status the pipeline settled on by the time the response starts. A fake returns 0 for
        // an unconfigured int, which is not a status any predicate should be reasoning about, so it
        // is set explicitly on every run.
        A.CallTo(() => responseFeature.StatusCode).Returns(statusCode);
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

    [Test]
    public async Task It_does_not_add_cache_control_to_a_success_response()
    {
        // A 200 is served with an etag and is what conditional GET revalidates against, so it must
        // stay storable. Asserting absence of the header, not just a different value, because the
        // middleware's only contribution here would be the no-store.
        IHeaderDictionary headers = await RunAndStartResponseAsync(statusCode: StatusCodes.Status200OK);
        headers.ContainsKey("Cache-Control").Should().BeFalse();
    }

    [Test]
    public async Task It_adds_cache_control_no_store_to_a_not_found_response()
    {
        // The catch-all 404 in Program.cs reflects a client-supplied correlation ID and is one of
        // the statuses RFC 9111 lets a shared cache store heuristically.
        IHeaderDictionary headers = await RunAndStartResponseAsync(statusCode: StatusCodes.Status404NotFound);
        headers["Cache-Control"].ToString().Should().Be("no-store");
    }

    [Test]
    public async Task It_adds_cache_control_no_store_to_a_server_error_response()
    {
        IHeaderDictionary headers = await RunAndStartResponseAsync(
            statusCode: StatusCodes.Status500InternalServerError
        );
        headers["Cache-Control"].ToString().Should().Be("no-store");
    }

    [Test]
    public async Task It_does_not_add_cache_control_to_a_not_modified_response()
    {
        // The regression guard for the 304 exclusion: a 304 tells a cache its stored copy is still
        // good, so no-store here would discard that copy and defeat the If-None-Match handling in
        // GetByIdHandler. This test fails if the predicate is ever simplified to a bare "not 2xx"
        // or ">= 300".
        IHeaderDictionary headers = await RunAndStartResponseAsync(
            statusCode: StatusCodes.Status304NotModified
        );
        headers.ContainsKey("Cache-Control").Should().BeFalse();
    }

    [Test]
    public async Task It_does_not_overwrite_a_cache_control_already_present()
    {
        // TryAdd semantics for Cache-Control specifically: a handler that has chosen its own
        // directive - GetTokenInfoHandler sets no-cache - keeps it. Run on a failing status so the
        // middleware would otherwise have written no-store here.
        IHeaderDictionary headers = await RunAndStartResponseAsync(
            h => h["Cache-Control"] = "no-cache",
            StatusCodes.Status404NotFound
        );
        headers["Cache-Control"].ToString().Should().Be("no-cache");
    }
}
