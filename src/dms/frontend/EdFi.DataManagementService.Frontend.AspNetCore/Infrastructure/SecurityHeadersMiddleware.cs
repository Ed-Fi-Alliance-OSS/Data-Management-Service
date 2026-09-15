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
    /// DMS failure bodies carry per-request content - a correlation ID taken from a client-supplied
    /// header, and validation detail derived from the request - and several of the statuses DMS
    /// emits (404, 405, 501 among them) are on RFC 9110 section 15.1's list of responses a shared
    /// cache may store *heuristically*, with no explicit freshness header required. The catch-all
    /// 404 in Program.cs is the sharpest case: it is unauthenticated, reachable on any unmatched
    /// route, and echoes the caller's correlation ID, so without this header one caller's chosen
    /// value can be stored and replayed to a different caller.
    ///
    /// Success responses are deliberately left alone: 200 bodies are served with an etag and are
    /// exactly what the conditional-GET support in GetByIdHandler exists to let clients revalidate.
    ///
    /// *** 304 must stay excluded - do not simplify this to "not 2xx" or ">= 300". ***
    /// A 304 is the server telling a cache "the copy you already stored is still good". Answering
    /// that with no-store instructs the cache to throw that copy away, which turns every successful
    /// revalidation into a full re-fetch and defeats the If-None-Match handling in GetByIdHandler.
    /// Worse, RFC 9110 section 15.4.5 directs a cache to update its stored response from the 304
    /// (the mechanism is RFC 9111 section 4.3.4), so the no-store would be written onto the stored
    /// 200 as well, poisoning a response that was never meant to carry it. 304 is the only 3xx DMS emits
    /// today (GetByIdHandler.TryCreateNotModified); no redirects are produced anywhere in the
    /// frontend or core, so the rest of the 3xx range is left in scope rather than excluded on
    /// speculation - marking a hypothetical future redirect no-store would merely forgo a caching
    /// opportunity, whereas leaving a future client-reflecting failure status uncovered would not.
    /// </remarks>
    private static bool ShouldPreventSharedCaching(int statusCode) =>
        statusCode is not (>= 200 and <= 299) && statusCode != StatusCodes.Status304NotModified;
}
