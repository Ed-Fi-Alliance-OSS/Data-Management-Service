// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Verifies that X-Forwarded-* headers are honored only from trusted reverse-proxy sources,
/// asserting on the discovery endpoint URLs which are built from the request scheme/host.
/// A test-only startup filter sets the connection remote IP (TestServer leaves it null) so
/// trusted vs untrusted peers can be simulated deterministically.
///
/// The ReverseProxy:Enabled flag and the trusted sources are read from configuration before the
/// host is built, so they are supplied via environment variables (visible to CreateBuilder) rather
/// than ConfigureAppConfiguration (applied later, at build time).
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_A_Reverse_Proxy_Configuration
{
    private const string ForwardedHost = "proxied.example.com";
    private const string ReverseProxyEnabledEnv = "AppSettings__ReverseProxy__Enabled";
    private const string KnownProxiesEnv = "AppSettings__ReverseProxy__KnownProxies";
    private const string KnownNetworksEnv = "AppSettings__ReverseProxy__KnownNetworks";

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(ReverseProxyEnabledEnv, null);
        Environment.SetEnvironmentVariable(KnownProxiesEnv, null);
        Environment.SetEnvironmentVariable(KnownNetworksEnv, null);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var versionProvider = A.Fake<IVersionProvider>();
        A.CallTo(() => versionProvider.Version).Returns("1.0");
        A.CallTo(() => versionProvider.ApplicationName).Returns("Ed-Fi API");
        A.CallTo(() => versionProvider.InformationalVersion).Returns("8.0.0");

        var dataModelInfoProvider = A.Fake<IDataModelInfoProvider>();
        A.CallTo(() => dataModelInfoProvider.GetDataModelInfo()).Returns([]);

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                collection.AddTransient(_ => versionProvider);
                collection.AddTransient(_ => dataModelInfoProvider);
                collection.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
            });
        });
    }

    private static async Task<string> GetDataManagementApiUrl(HttpClient client, string? remoteIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-Host", ForwardedHost);
        request.Headers.Add("X-Forwarded-Proto", "https");
        if (remoteIp is not null)
        {
            request.Headers.Add("X-Test-Remote-Ip", remoteIp);
        }

        var response = await client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        var apiDetails = JsonNode.Parse(content);
        return apiDetails?["urls"]?["dataManagementApi"]?.GetValue<string>() ?? string.Empty;
    }

    [Test]
    public async Task It_ignores_forwarded_headers_when_reverse_proxy_is_disabled()
    {
        Environment.SetEnvironmentVariable(ReverseProxyEnabledEnv, "false");

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var url = await GetDataManagementApiUrl(client, remoteIp: "10.0.0.5");

        url.Should().Be("http://localhost/data");
    }

    [Test]
    public async Task It_ignores_forwarded_headers_from_an_untrusted_source()
    {
        Environment.SetEnvironmentVariable(ReverseProxyEnabledEnv, "true");
        Environment.SetEnvironmentVariable(KnownProxiesEnv, "10.0.0.5");

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var url = await GetDataManagementApiUrl(client, remoteIp: "203.0.113.99");

        url.Should().Be("http://localhost/data");
    }

    [Test]
    public async Task It_honors_forwarded_headers_from_a_trusted_proxy_ip()
    {
        Environment.SetEnvironmentVariable(ReverseProxyEnabledEnv, "true");
        Environment.SetEnvironmentVariable(KnownProxiesEnv, "10.0.0.5");

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var url = await GetDataManagementApiUrl(client, remoteIp: "10.0.0.5");

        url.Should().Be($"https://{ForwardedHost}/data");
    }

    [Test]
    public async Task It_honors_forwarded_headers_from_a_trusted_network()
    {
        Environment.SetEnvironmentVariable(ReverseProxyEnabledEnv, "true");
        Environment.SetEnvironmentVariable(KnownNetworksEnv, "10.10.0.0/16");

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var url = await GetDataManagementApiUrl(client, remoteIp: "10.10.5.5");

        url.Should().Be($"https://{ForwardedHost}/data");
    }

    [Test]
    public async Task It_fails_startup_when_a_trusted_proxy_ip_is_malformed()
    {
        // Drives the real application startup: the host forces IOptions<ReverseProxySettings>
        // validation, which fails and short-circuits every request via the invalid-configuration
        // middleware (HTTP 500) rather than serving the endpoint.
        Environment.SetEnvironmentVariable(ReverseProxyEnabledEnv, "true");
        Environment.SetEnvironmentVariable(KnownProxiesEnv, "not-an-ip");

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task It_fails_startup_when_a_trusted_network_cidr_is_malformed()
    {
        Environment.SetEnvironmentVariable(ReverseProxyEnabledEnv, "true");
        Environment.SetEnvironmentVariable(KnownNetworksEnv, "10.0.0.0/99");

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
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
