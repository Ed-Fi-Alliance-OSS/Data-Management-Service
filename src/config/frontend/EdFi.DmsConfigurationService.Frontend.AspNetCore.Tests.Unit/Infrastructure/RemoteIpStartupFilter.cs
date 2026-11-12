// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Test-only: TestServer leaves the connection remote IP null, so this sets it from a request header
/// ahead of the application pipeline (and so ahead of UseForwardedHeaders), letting tests simulate
/// distinct, trusted and untrusted peers deterministically.
/// </summary>
internal sealed class RemoteIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(
                async (context, nextMiddleware) =>
                {
                    if (
                        context.Request.Headers.TryGetValue(HeaderName, out var value)
                        && IPAddress.TryParse(value.ToString(), out var ip)
                    )
                    {
                        context.Connection.RemoteIpAddress = ip;
                    }

                    await nextMiddleware();
                }
            );
            next(app);
        };
}
