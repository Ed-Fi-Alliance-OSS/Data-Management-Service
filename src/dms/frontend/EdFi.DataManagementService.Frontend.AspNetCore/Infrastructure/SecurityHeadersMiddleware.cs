// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Adds baseline security response headers to every response. Registered on OnStarting so the
/// headers are written just before the response is sent, which keeps them on short-circuited and
/// error responses produced later in the pipeline, and makes the final status code available when
/// the headers are applied. TryAdd leaves any value already set upstream intact.
/// </summary>
public class SecurityHeadersMiddleware(RequestDelegate next)
{
    private static readonly (string Name, string Value)[] _securityHeaders =
    [
        ("X-Content-Type-Options", "nosniff"),
        ("Referrer-Policy", "no-referrer"),
    ];

    public Task Invoke(HttpContext context)
    {
        context.Response.OnStarting(ApplyHeaders, context);
        return next(context);
    }

    private static Task ApplyHeaders(object state)
    {
        HttpResponse response = ((HttpContext)state).Response;
        IHeaderDictionary headers = response.Headers;
        foreach ((string name, string value) in _securityHeaders)
        {
            headers.TryAdd(name, value);
        }

        if (ShouldPreventSharedCaching(response.StatusCode))
        {
            headers.TryAdd("Cache-Control", "no-store");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether this response must be marked no-store so that a CDN or other shared cache in front
    /// of DMS cannot hold on to it and replay it to a different caller.
    /// </summary>
    /// <remarks>
    /// DMS failure bodies carry per-request content - a correlation ID taken from a
    /// client-supplied header, and validation detail derived from the request - and several
    /// statuses DMS emits (404, 405, 501 among them) are heuristically cacheable under RFC 9110
    /// section 15.1, with no explicit freshness header required. The catch-all 404 in Program.cs
    /// is the sharpest case: unauthenticated, reachable on any unmatched route, and echoing the
    /// caller's correlation ID. Success responses are deliberately left alone - a 200 carries an
    /// etag and is what the conditional-GET support in GetByIdHandler exists to revalidate.
    ///
    /// *** 304 must stay excluded - do not simplify this to "not 2xx" or ">= 300". ***
    /// A 304 tells a cache its stored copy is still good, so answering with no-store makes the
    /// cache discard that copy, turning every successful revalidation into a full re-fetch. Worse,
    /// RFC 9110 section 15.4.5 directs a cache to update its stored response from the 304 (the
    /// mechanism is RFC 9111 section 4.3.4), so the no-store would be written onto the stored 200
    /// as well. 304 is the only 3xx DMS emits today (GetByIdHandler.TryCreateNotModified); the
    /// rest of the 3xx range is left in scope rather than excluded on speculation.
    /// </remarks>
    private static bool ShouldPreventSharedCaching(int statusCode) =>
        statusCode is not (>= 200 and <= 299) && statusCode != StatusCodes.Status304NotModified;
}
