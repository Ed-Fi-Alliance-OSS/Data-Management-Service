// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>One HTTP exchange with the identity API: what came back, read once.</summary>
internal sealed record IdentityHttpOutcome(
    HttpStatusCode Status,
    string? Location,
    string Text,
    JsonNode? Body,
    IReadOnlyDictionary<string, string[]> Headers
)
{
    /// <summary>The values of one response header, empty when the response does not carry it.</summary>
    public string[] HeaderValues(string name) =>
        Headers.TryGetValue(name, out string[]? values) ? values : [];

    /// <summary>The problem type of a problem response, or null when the body has none.</summary>
    public string? ProblemType => Body is JsonObject body ? body["type"]?.GetValue<string>() : null;
}

/// <summary>
/// A running fixture host with a control stub, one granted client and raw HTTP helpers, so a case can
/// send exactly the bytes it wants and read the status, <c>Location</c> and body that came back.
/// </summary>
internal sealed class IdentityHttpRun : IAsyncDisposable
{
    private const string FixturePlugin = "Acme.IdentityFixture";
    public const string Namespace = "ns-a";

    private IdentityHttpRun(
        IdentityFixtureControlStub stub,
        IdentityPluginHost host,
        HttpClient client,
        string district
    )
    {
        Stub = stub;
        Host = host;
        Client = client;
        District = district;
    }

    public IdentityFixtureControlStub Stub { get; }

    public IdentityPluginHost Host { get; }

    public HttpClient Client { get; }

    public string District { get; }

    /// <summary>
    /// Starts the fixture host with client A granted the one namespace its first district maps, a
    /// control stub for invocation counts, and the given number of incomplete polls.
    /// </summary>
    public static async Task<IdentityHttpRun> StartAsync(
        Action<IServiceCollection>? configureServices = null,
        int pollsUntilComplete = 1,
        IReadOnlyDictionary<string, string>? fixtureSettings = null,
        IReadOnlyDictionary<string, string>? settings = null
    )
    {
        string district = IdentityTestClients.DistrictsOf(IdentityTestClients.TenantOne)[0];

        IdentityFixtureControlStub stub = await IdentityFixtureControlStub.StartAsync();
        IdentityFixtureSettings fixture = new IdentityFixtureSettings()
            .WithControlStub(stub)
            .Namespace(Namespace, (IdentityTestClients.TenantOne, district))
            .With("IdentityFixture:PollsUntilComplete", pollsUntilComplete.ToString());

        foreach ((string key, string value) in fixtureSettings ?? new Dictionary<string, string>())
        {
            fixture.With(key, value);
        }

        IdentityPluginHost host = IdentityPluginHost.Create(
            [FixturePlugin],
            allowed: FixturePlugin,
            fixtureSettings: fixture.Settings,
            settings: settings,
            configureServices: configureServices
        );
        stub.Grant(IdentityTestClients.ClientA.ClientId, IdentityTestClients.TenantOne, Namespace);

        return new IdentityHttpRun(
            stub,
            host,
            host.CreateClient(IdentityTestClients.ClientA.Token),
            district
        );
    }

    /// <summary>The route under client A's tenant and first district.</summary>
    public string Route(string path) =>
        IdentityTestClients.Route(IdentityTestClients.TenantOne, District, path);

    public Task<IdentityHttpOutcome> PostAsync(
        string path,
        string body,
        string contentType = "application/json",
        HttpClient? client = null
    ) => SendAsync(HttpMethod.Post, Route(path), body, contentType, client);

    public Task<IdentityHttpOutcome> GetAsync(string pathOrLocation, HttpClient? client = null) =>
        SendAsync(
            HttpMethod.Get,
            pathOrLocation.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? pathOrLocation
                : Route(pathOrLocation),
            body: null,
            contentType: null,
            client
        );

    private async Task<IdentityHttpOutcome> SendAsync(
        HttpMethod method,
        string target,
        string? body,
        string? contentType,
        HttpClient? client = null
    )
    {
        using HttpRequestMessage request = new(method, target);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.Remove("Content-Type");
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        using HttpResponseMessage response = await (client ?? Client).SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        JsonNode? parsed = null;

        if (text.Length > 0)
        {
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                parsed = null;
            }
        }

        string? location = response.Headers.Location?.OriginalString;
        Dictionary<string, string[]> headers = new(StringComparer.OrdinalIgnoreCase);

        foreach (
            (string name, IEnumerable<string> values) in response.Headers.Concat(response.Content.Headers)
        )
        {
            headers[name] = [.. values];
        }

        return new IdentityHttpOutcome(response.StatusCode, location, text, parsed, headers);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.DisposeAsync();
        await Stub.DisposeAsync();
    }
}
