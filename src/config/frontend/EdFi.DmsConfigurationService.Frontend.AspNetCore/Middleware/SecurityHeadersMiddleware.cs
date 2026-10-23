// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

/// <summary>
/// Adds baseline security response headers to every response. Registered on OnStarting so the
/// headers are written just before the response is sent, which keeps them on short-circuited and
/// error responses produced later in the pipeline, and makes the final status code available when
/// the headers are applied. TryAdd preserves values written earlier in this pipeline. A reverse
/// proxy adds its headers after the response leaves the process, so they cannot be seen here; a
/// proxy that wants a different value must remove the application's (see docs/CONFIGURATION.md).
/// </summary>
/// <remarks>
/// Strict-Transport-Security is written only for HTTPS requests (RFC 6797 section 7.2 forbids the
/// header over HTTP) and not for loopback hosts, so developer browsers are not pinned to HTTPS on
/// localhost. It is applied here rather than with UseHsts, which writes before the endpoint runs
/// and is lost when the exception handler clears the response headers.
/// </remarks>
public class SecurityHeadersMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    private static readonly (string Name, string Value)[] _securityHeaders =
    [
        ("X-Content-Type-Options", "nosniff"),
        ("Referrer-Policy", "no-referrer"),
        ("X-Frame-Options", "DENY"),
        ("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'"),
    ];

    public Task Invoke(HttpContext context)
    {
        context.Response.OnStarting(ApplyHeaders, context);
        return _next(context);
    }

    private static Task ApplyHeaders(object state)
    {
        HttpContext context = (HttpContext)state;
        HttpResponse response = context.Response;
        IHeaderDictionary headers = response.Headers;
        foreach ((string name, string value) in _securityHeaders)
        {
            headers.TryAdd(name, value);
        }

        if (context.Request.IsHttps && !IsLoopbackHost(context.Request.Host.Host))
        {
            headers.TryAdd("Strict-Transport-Security", "max-age=31536000");
        }

        if (ShouldPreventSharedCaching(response.StatusCode))
        {
            headers.TryAdd("Cache-Control", "no-store");
        }

        return Task.CompletedTask;
    }

    // Matches the ASP.NET Core HstsOptions.ExcludedHosts defaults.
    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this response must be marked no-store so that a CDN or other shared cache in front
    /// of the Configuration Service cannot hold on to it and replay it to a different caller.
    /// </summary>
    /// <remarks>
    /// CMS failure bodies carry request-derived validation detail, and several statuses it emits
    /// (404, 405 among them) are heuristically cacheable under RFC 9110 section 15.1. CMS emits no
    /// 304 today; the exclusion is kept so this predicate stays identical to the one in the DMS
    /// SecurityHeadersMiddleware (DMS-1545 recorded the drift between the two copies). A 304 must
    /// not carry no-store, because that would make a cache discard the copy it just revalidated.
    /// </remarks>
    private static bool ShouldPreventSharedCaching(int statusCode) =>
        statusCode is not (>= 200 and <= 299) && statusCode != StatusCodes.Status304NotModified;
}
