// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Jobs;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

[TestFixture("self-contained", true, false, "", "", "", true)]
[TestFixture("SELF-CONTAINED", true, false, "", "", "", true)]
[TestFixture("self-contained", true, true, "", "", "", true)]
[TestFixture("self-contained", true, false, "172.30.148.2", "", "", true)]
[TestFixture("self-contained", false, false, "", "", "", false)]
[TestFixture("keycloak", true, false, "", "", "", false)]
[TestFixture("self-contained", true, true, "172.30.148.2", "", "", false)]
[TestFixture("self-contained", true, true, "", "172.30.148.0/29", "", false)]
[TestFixture("self-contained", true, true, "172.30.148.2", "", "172.30.148.2", false)]
public class Given_connect_rate_limiting_at_startup(
    string provider,
    bool enabled,
    bool forwarding,
    string knownProxies,
    string knownNetworks,
    string requiredProxy,
    bool expectWarning
)
{
    private CapturingLoggerProvider _logs = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private List<CapturedLog> _warnings = [];

    [SetUp]
    public void Setup()
    {
        _logs = new CapturingLoggerProvider();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            // These settings must be visible before Program configures the provider and forwarding.
            builder.UseSetting("AppSettings:IdentityProvider", provider);
            builder.UseSetting("AppSettings:ReverseProxy:UseForwardedHeaders", forwarding.ToString());
            builder.UseSetting("AppSettings:ReverseProxy:KnownProxies", knownProxies);
            builder.UseSetting("AppSettings:ReverseProxy:KnownNetworks", knownNetworks);
            builder.UseSetting("ConnectRateLimit:Enabled", enabled.ToString());
            builder.UseSetting("ConnectRateLimit:RequiredProxyAddress", requiredProxy);
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(_logs));
        });
        using HttpClient client = _factory.CreateClient();
        _warnings =
        [
            .. _logs.Entries.Where(entry =>
                entry.Level == LogLevel.Warning
                && entry.State.TryGetValue("{OriginalFormat}", out object? template)
                && template is string message
                && message.StartsWith("ConnectRateLimit is enabled", StringComparison.Ordinal)
            ),
        ];
    }

    [TearDown]
    public void TearDown()
    {
        _factory.Dispose();
        _logs.Dispose();
    }

    [Test]
    public void It_warns_only_when_proxy_callers_could_silently_share_the_default_source_budget()
    {
        if (expectWarning)
        {
            CapturedLog warning = _warnings.Should().ContainSingle().Which;
            string message = (string)warning.State["{OriginalFormat}"]!;
            message.Should().Contain("system-token refresh");
            message.Should().Contain("share one source budget");
            message.Should().Contain("KnownProxies");
            message.Should().Contain("raise or disable");
        }
        else
        {
            _warnings.Should().BeEmpty();
        }
    }
}
