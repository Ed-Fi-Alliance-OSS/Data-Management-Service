// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

/// <summary>
/// The shipped appsettings.json is read from the build output rather than through the host, because the
/// host under test reads appsettings.Test.json too (pattern: ClientSecretHashingIterationsSettingsTests).
/// </summary>
[TestFixture]
public class Given_the_shipped_connect_rate_limit_settings
{
    private IConfigurationSection _section = null!;

    [SetUp]
    public void Setup()
    {
        IConfigurationRoot shippedConfiguration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();
        _section = shippedConfiguration.GetSection(ConnectRateLimitSettings.SectionName);
    }

    [Test]
    public void It_enables_limiting() => bool.Parse(_section["Enabled"]!).Should().BeTrue();

    [Test]
    public void It_ships_a_source_limit_of_120() => _section["SourcePermitLimit"].Should().Be("120");

    [Test]
    public void It_ships_a_client_limit_of_30() => _section["ClientPermitLimit"].Should().Be("30");

    [Test]
    public void It_ships_a_60_second_window() => _section["WindowSeconds"].Should().Be("60");

    [Test]
    public void It_matches_the_model_defaults()
    {
        var defaults = new ConnectRateLimitSettings();
        defaults.Enabled.Should().BeTrue();
        defaults.SourcePermitLimit.Should().Be(120);
        defaults.ClientPermitLimit.Should().Be(30);
        defaults.WindowSeconds.Should().Be(60);
    }
}

/// <summary>
/// Invalid ConnectRateLimit values go through ReportInvalidConfiguration: the host keeps running and every
/// request is answered with the generic Ed-Fi 500, so the limiter is never constructed.
/// </summary>
[TestFixture]
public class Given_an_invalid_connect_rate_limit_setting_at_startup
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpResponseMessage _response = null!;
    private JsonObject _body = null!;

    [SetUp]
    public async Task Setup()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["ConnectRateLimit:ClientPermitLimit"] = "0" }
                    )
            );
        });
        using HttpClient client = _factory.CreateClient();
        _response = await client.PostAsync(
            "/connect/introspect",
            new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>())
        );
        _body = JsonNode.Parse(await _response.Content.ReadAsStringAsync())!.AsObject();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public void It_answers_500() => _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

    [Test]
    public void It_uses_the_internal_server_error_contract() =>
        _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:management-api:internal-server-error");
}
