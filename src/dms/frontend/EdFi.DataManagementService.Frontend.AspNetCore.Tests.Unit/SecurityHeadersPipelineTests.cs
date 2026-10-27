// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Drives the real DMS pipeline to verify the security headers reach real responses, including
/// HSTS gating on the effective scheme and host (after forwarded-header processing).
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_SecurityHeaders_In_The_Dms_Pipeline
{
    private const string UseForwardedHeadersEnv = "AppSettings__ReverseProxy__UseForwardedHeaders";
    private const string KnownProxiesEnv = "AppSettings__ReverseProxy__KnownProxies";
    private const string KnownNetworksEnv = "AppSettings__ReverseProxy__KnownNetworks";

    private EnvironmentVariableScope _reverseProxyEnvironment = null!;

    [SetUp]
    public void SetUp()
    {
        _reverseProxyEnvironment = new EnvironmentVariableScope(
            UseForwardedHeadersEnv,
            KnownProxiesEnv,
            KnownNetworksEnv
        );
    }

    [TearDown]
    public void TearDown()
    {
        _reverseProxyEnvironment.Dispose();
    }

    private static WebApplicationFactory<Program> CreateFactory(string environment)
    {
        var versionProvider = A.Fake<IVersionProvider>();
        A.CallTo(() => versionProvider.Version).Returns("1.0");
        A.CallTo(() => versionProvider.ApplicationName).Returns("Ed-Fi API");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("8.0.0");

        var dataModelInfoProvider = A.Fake<IDataModelInfoProvider>();
        A.CallTo(() => dataModelInfoProvider.GetDataModelInfo()).Returns([]);

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            if (environment != "Test")
            {
                UseTestSettings(builder);
            }
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                collection.AddTransient(_ => versionProvider);
                collection.AddTransient(_ => dataModelInfoProvider);
                collection.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
            });
        });
    }

    /// <summary>
    /// Another environment reads its own appsettings file instead of appsettings.Test.json, so the
    /// Test settings are supplied through UseSetting, which registration-time readers see.
    /// </summary>
    private static void UseTestSettings(IWebHostBuilder builder)
    {
        IConfiguration testSettings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Test.json"))
            .Build();
        foreach ((string key, string? value) in testSettings.AsEnumerable())
        {
            if (value is not null)
            {
                builder.UseSetting(key, value);
            }
        }
    }

    // Headers stay readable after the factory and client are disposed, because the in-memory
    // test server buffers the response.
    private static async Task<HttpResponseMessage> GetAsync(
        string baseAddress,
        string? remoteIp = null,
        string environment = "Test"
    )
    {
        await using var factory = CreateFactory(environment);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress) }
        );
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        if (remoteIp is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-Host", "proxied.example.com");
            request.Headers.Add("X-Test-Remote-Ip", remoteIp);
        }

        return await client.SendAsync(request);
    }

    [Test]
    public async Task It_adds_hsts_frame_and_csp_headers_over_https()
    {
        using var response = await GetAsync("https://dms.example");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Strict-Transport-Security").Should().Equal("max-age=31536000");
        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        response
            .Headers.GetValues("Content-Security-Policy")
            .Should()
            .Equal("default-src 'none'; frame-ancestors 'none'");
    }

    [Test]
    public async Task It_does_not_add_hsts_over_http()
    {
        using var response = await GetAsync("http://dms.example");

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    [Test]
    public async Task It_does_not_add_hsts_for_a_loopback_host_over_https()
    {
        using var response = await GetAsync("https://localhost");

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    [Test]
    public async Task It_does_not_add_hsts_over_https_in_the_development_environment()
    {
        // A non-loopback host, so only the environment keeps HSTS off: a developer browser would
        // otherwise pin this hostname to HTTPS on every port for a year.
        using var response = await GetAsync("https://dms.example", environment: Environments.Development);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
    }

    [Test]
    public async Task It_adds_hsts_when_a_trusted_proxy_forwards_https()
    {
        Environment.SetEnvironmentVariable(UseForwardedHeadersEnv, "true");
        Environment.SetEnvironmentVariable(KnownProxiesEnv, "10.0.0.1");

        using var response = await GetAsync("http://dms.example", "10.0.0.1");

        response.Headers.GetValues("Strict-Transport-Security").Should().Equal("max-age=31536000");
    }

    [Test]
    public async Task It_does_not_add_hsts_when_an_untrusted_peer_forwards_https()
    {
        Environment.SetEnvironmentVariable(UseForwardedHeadersEnv, "true");
        Environment.SetEnvironmentVariable(KnownProxiesEnv, "10.0.0.1");

        using var response = await GetAsync("http://dms.example", "10.0.0.2");

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    [Test]
    public async Task It_does_not_add_cache_control_to_the_discovery_200()
    {
        using var response = await GetAsync("https://dms.example");

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
