// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Adds baseline security response headers to every response. Registered on OnStarting so the
/// headers are written just before the response is sent, which keeps them on short-circuited and
/// error responses produced later in the pipeline. TryAdd leaves any value already set upstream intact.
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
        IHeaderDictionary headers = ((HttpContext)state).Response.Headers;
        foreach ((string name, string value) in _securityHeaders)
        {
            headers.TryAdd(name, value);
        }
        return Task.CompletedTask;
    }
}
