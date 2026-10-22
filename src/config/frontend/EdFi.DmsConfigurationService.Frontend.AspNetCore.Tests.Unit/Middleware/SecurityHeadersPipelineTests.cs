// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

/// <summary>
/// Drives the real CMS pipeline to verify the security headers reach real responses, including
/// HSTS gating on the effective scheme and host (after forwarded-header processing) and the
/// no-store rule on short-circuited failure responses.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_The_Cms_Pipeline
{
    private const string UseForwardedHeadersEnv = "AppSettings__ReverseProxy__UseForwardedHeaders";
    private const string KnownProxiesEnv = "AppSettings__ReverseProxy__KnownProxies";

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(UseForwardedHeadersEnv, null);
        Environment.SetEnvironmentVariable(KnownProxiesEnv, null);
    }

    private const string Hsts = "Strict-Transport-Security";

    /// <summary>
    /// Boots the real pipeline, sends one request to the given base address and path, and disposes
    /// the factory and client before returning the (already buffered) response.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(
        string baseAddress,
        string path,
        string? forwardedFromIp = null
    )
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
                collection.AddSingleton<IStartupFilter, RemoteIpStartupFilter>()
            );
        });
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress) }
        );
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (forwardedFromIp is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-Host", "proxied.example.com");
            request.Headers.Add("X-Test-Remote-Ip", forwardedFromIp);
        }

        return await client.SendAsync(request);
    }

    private static void TrustProxy(string proxyIp)
    {
        Environment.SetEnvironmentVariable(UseForwardedHeadersEnv, "true");
        Environment.SetEnvironmentVariable(KnownProxiesEnv, proxyIp);
    }

    [Test]
    public async Task It_adds_hsts_frame_and_csp_headers_over_https()
    {
        using var response = await SendAsync("https://cms.example", "/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(Hsts).Should().Equal("max-age=31536000");
        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        response
            .Headers.GetValues("Content-Security-Policy")
            .Should()
            .Equal("default-src 'none'; frame-ancestors 'none'");
    }

    [Test]
    public async Task It_does_not_add_hsts_over_http()
    {
        using var response = await SendAsync("http://cms.example", "/");

        response.Headers.Contains(Hsts).Should().BeFalse();
    }

    [Test]
    public async Task It_does_not_add_hsts_for_a_loopback_host_over_https()
    {
        using var response = await SendAsync("https://localhost", "/");

        response.Headers.Contains(Hsts).Should().BeFalse();
    }

    [Test]
    public async Task It_adds_hsts_when_a_trusted_proxy_forwards_https()
    {
        TrustProxy("10.0.0.1");

        using var response = await SendAsync("http://cms.example", "/", forwardedFromIp: "10.0.0.1");

        response.Headers.GetValues(Hsts).Should().Equal("max-age=31536000");
    }

    [Test]
    public async Task It_does_not_add_hsts_when_an_untrusted_peer_forwards_https()
    {
        TrustProxy("10.0.0.1");

        using var response = await SendAsync("http://cms.example", "/", forwardedFromIp: "10.0.0.2");

        response.Headers.Contains(Hsts).Should().BeFalse();
    }

    [Test]
    public async Task It_marks_an_unauthenticated_401_no_store()
    {
        using var response = await SendAsync("https://cms.example", "/v3/vendors");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.GetValues("Cache-Control").Should().Equal("no-store");
    }

    [Test]
    public async Task It_marks_an_unmapped_route_404_no_store()
    {
        using var response = await SendAsync("https://cms.example", "/no-such-route");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.GetValues("Cache-Control").Should().Equal("no-store");
    }

    [Test]
    public async Task It_does_not_add_cache_control_to_the_health_200()
    {
        using var response = await SendAsync("https://cms.example", "/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Cache-Control").Should().BeFalse();
    }

    /// <summary>
    /// Runs before the application's UseForwardedHeaders middleware and sets the connection
    /// remote IP from the X-Test-Remote-Ip header so trusted/untrusted peers can be simulated.
    /// </summary>
    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    async (context, nextMiddleware) =>
                    {
                        if (
                            context.Request.Headers.TryGetValue("X-Test-Remote-Ip", out var value)
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
}
