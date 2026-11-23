// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_A_Rate_Limit_Partition_Key_For_An_Address
{
    private static RateLimitPartitionKey KeyFor(string address) =>
        RateLimitPartitionKey.ForAddress(IPAddress.Parse(address));

    [Test]
    public void It_keys_an_ipv4_address_as_itself()
    {
        KeyFor("10.0.0.1").Should().Be(new RateLimitPartitionKey(RateLimitPartitionKind.Address, "10.0.0.1"));
    }

    [Test]
    public void It_keys_an_ipv4_mapped_address_as_its_ipv4_address()
    {
        KeyFor("::ffff:10.0.0.1").Should().Be(KeyFor("10.0.0.1"));
    }

    [Test]
    public void It_keys_a_native_ipv6_address_on_its_64_bit_prefix()
    {
        KeyFor("2001:db8:1:1:aaaa:bbbb:cccc:dddd")
            .Should()
            .Be(new RateLimitPartitionKey(RateLimitPartitionKind.Address, "2001:db8:1:1::"));
        KeyFor("2001:db8:1:2::1").Should().NotBe(KeyFor("2001:db8:1:1::1"));
    }

    [Test]
    public void It_drops_the_ipv6_scope_id()
    {
        KeyFor("fe80::1%3").Should().Be(KeyFor("fe80::1%4"));
        KeyFor("fe80::1%3").Value.Should().Be("fe80::");
    }

    [Test]
    public void It_keys_a_missing_address_as_one_shared_unknown_partition()
    {
        RateLimitPartitionKey unknown = RateLimitPartitionKey.ForAddress(null);

        unknown.Kind.Should().Be(RateLimitPartitionKind.UnknownAddress);
        RateLimitPartitionKey.ForAddress(null).Should().Be(unknown);
    }

    [Test]
    public void It_never_matches_an_unknown_address_with_a_known_one_of_the_same_value()
    {
        RateLimitPartitionKey
            .ForAddress(null)
            .Should()
            .NotBe(new RateLimitPartitionKey(RateLimitPartitionKind.Address, string.Empty));
    }
}

/// <summary>
/// Drives the real host under the TestRateLimit environment, which admits one request per sixty-second
/// window per partition; the wide window keeps every assertion independent of timing. TestServer leaves
/// the connection address null, so a test-only startup filter sets it from X-Test-Remote-Ip ahead of the
/// application's forwarded-headers middleware. Each test uses addresses no other test in its fixture
/// uses, so tests sharing a host never share a partition.
///
/// The reverse-proxy settings are read before the host is built, so they are supplied through
/// environment variables, which is why these fixtures do not run in parallel with others.
/// </summary>
public abstract class RateLimitPartitionHostFixture
{
    private const string RemoteIpHeader = "X-Test-Remote-Ip";

    private static readonly string[] _environmentVariables =
    [
        "AppSettings__ReverseProxy__UseForwardedHeaders",
        "AppSettings__ReverseProxy__KnownProxies",
        "AppSettings__ReverseProxy__KnownNetworks",
        "RateLimit__PermitLimit",
        "RateLimit__Window",
        "RateLimit__QueueLimit",
    ];

    private EnvironmentVariableScope _environment = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    protected abstract IReadOnlyDictionary<string, string> ReverseProxyEnvironment { get; }

    [OneTimeSetUp]
    public void StartHost()
    {
        _environment = new EnvironmentVariableScope(_environmentVariables);
        foreach ((string name, string value) in ReverseProxyEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("TestRateLimit");
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                collection.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
            });
        });
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        _environment.Dispose();
    }

    /// <summary>
    /// Sends GET /health, which is anonymous and answers 200 whenever the limiter admits it, so its
    /// status reports admission alone.
    /// </summary>
    protected async Task<HttpStatusCode> GetHealthAsync(
        string? remoteIp = null,
        string? host = null,
        string? forwardedFor = null,
        string? forwardedHost = null
    )
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/health");
        AddHeader(request, RemoteIpHeader, remoteIp);
        AddHeader(request, "X-Forwarded-For", forwardedFor);
        AddHeader(request, "X-Forwarded-Host", forwardedHost);
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        using HttpResponseMessage response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    private static void AddHeader(HttpRequestMessage request, string name, string? value)
    {
        if (value is not null)
        {
            request.Headers.Add(name, value);
        }
    }

    /// <summary>
    /// Runs before the application's forwarded-headers middleware and sets the connection address from
    /// X-Test-Remote-Ip, so each request can come from a chosen peer.
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
                            context.Request.Headers.TryGetValue(RemoteIpHeader, out var value)
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

[TestFixture]
[NonParallelizable]
public class Given_The_Rate_Limiter_With_Forwarded_Headers_Disabled : RateLimitPartitionHostFixture
{
    protected override IReadOnlyDictionary<string, string> ReverseProxyEnvironment =>
        new Dictionary<string, string> { ["AppSettings__ReverseProxy__UseForwardedHeaders"] = "false" };

    [Test]
    public async Task It_budgets_each_address_independently()
    {
        (await GetHealthAsync(remoteIp: "10.0.0.1")).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: "10.0.0.1")).Should().Be(HttpStatusCode.TooManyRequests);
        (await GetHealthAsync(remoteIp: "10.0.0.2")).Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task It_does_not_split_an_address_budget_by_host()
    {
        (await GetHealthAsync(remoteIp: "10.0.1.1", host: "first.example.com"))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: "10.0.1.1", host: "second.example.com"))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task It_ignores_forwarded_for_headers()
    {
        (await GetHealthAsync(remoteIp: "10.0.2.1", forwardedFor: "203.0.113.1"))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: "10.0.2.1", forwardedFor: "203.0.113.2"))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task It_shares_one_budget_between_an_ipv4_mapped_address_and_its_ipv4_address()
    {
        (await GetHealthAsync(remoteIp: "::ffff:10.0.3.1")).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: "10.0.3.1")).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task It_shares_one_budget_within_an_ipv6_64_bit_prefix()
    {
        (await GetHealthAsync(remoteIp: "2001:db8:1:1::1")).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: "2001:db8:1:1::2")).Should().Be(HttpStatusCode.TooManyRequests);
        (await GetHealthAsync(remoteIp: "2001:db8:1:2::1")).Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task It_shares_one_budget_among_requests_without_an_address()
    {
        (await GetHealthAsync()).Should().Be(HttpStatusCode.OK);
        (await GetHealthAsync()).Should().Be(HttpStatusCode.TooManyRequests);
    }
}

[TestFixture]
[NonParallelizable]
public class Given_The_Rate_Limiter_Behind_Configured_Reverse_Proxies : RateLimitPartitionHostFixture
{
    private const string TrustedProxy = "10.0.0.5";
    private const string TrustedNetworkPeer = "10.10.5.5";
    private const string OtherTrustedNetworkPeer = "10.10.5.6";

    protected override IReadOnlyDictionary<string, string> ReverseProxyEnvironment =>
        new Dictionary<string, string>
        {
            ["AppSettings__ReverseProxy__UseForwardedHeaders"] = "true",
            ["AppSettings__ReverseProxy__KnownProxies"] = TrustedProxy,
            ["AppSettings__ReverseProxy__KnownNetworks"] = "10.10.0.0/16",
        };

    [Test]
    public async Task It_ignores_forwarded_for_headers_from_an_untrusted_peer()
    {
        (await GetHealthAsync(remoteIp: "203.0.113.99", forwardedFor: "198.51.100.1"))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: "203.0.113.99", forwardedFor: "198.51.100.2"))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task It_budgets_forwarded_addresses_from_a_trusted_network_independently()
    {
        (await GetHealthAsync(remoteIp: TrustedNetworkPeer, forwardedFor: "198.51.100.11"))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: TrustedNetworkPeer, forwardedFor: "198.51.100.12"))
            .Should()
            .Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task It_shares_the_budget_of_one_forwarded_address_across_trusted_peers()
    {
        (await GetHealthAsync(remoteIp: TrustedNetworkPeer, forwardedFor: "198.51.100.21"))
            .Should()
            .Be(HttpStatusCode.OK);
        (await GetHealthAsync(remoteIp: OtherTrustedNetworkPeer, forwardedFor: "198.51.100.21"))
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task It_does_not_split_a_forwarded_address_budget_by_forwarded_host()
    {
        (
            await GetHealthAsync(
                remoteIp: TrustedProxy,
                forwardedFor: "198.51.100.31",
                forwardedHost: "first.example.com"
            )
        )
            .Should()
            .Be(HttpStatusCode.OK);
        (
            await GetHealthAsync(
                remoteIp: TrustedProxy,
                forwardedFor: "198.51.100.31",
                forwardedHost: "second.example.com"
            )
        )
            .Should()
            .Be(HttpStatusCode.TooManyRequests);
    }
}
