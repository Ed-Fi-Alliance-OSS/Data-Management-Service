// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Acme.IdentityFixture;

/// <summary>
/// The fixture's plain-HTTP client for the control channel. Every request is relative to
/// <c>ControlBaseAddress</c>; when none is configured the channel is disabled and callers fall back
/// to configuration or do nothing.
/// </summary>
public sealed class FixtureControlChannel(
    IOptions<IdentityFixtureOptions> options,
    IHttpClientFactory httpClients
)
{
    public const string HttpClientName = "Acme.IdentityFixture.Control";

    public bool IsEnabled => !string.IsNullOrEmpty(options.Value.ControlBaseAddress);

    /// <summary>GETs a JSON object, throwing on a non-success status or a body that is not an object.</summary>
    public async Task<JsonObject> GetAsync(string relative, CancellationToken cancellationToken)
    {
        using HttpClient client = httpClients.CreateClient(HttpClientName);
        using HttpResponseMessage response = await client.GetAsync(Compose(relative), cancellationToken);
        response.EnsureSuccessStatusCode();

        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject
            ?? throw new InvalidOperationException("The control channel answered without a JSON object.");
    }

    /// <summary>POSTs a JSON object, throwing on a non-success status.</summary>
    public async Task PostAsync(string relative, JsonObject body, CancellationToken cancellationToken)
    {
        using HttpClient client = httpClients.CreateClient(HttpClientName);
        using StringContent content = new(body.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(
            Compose(relative),
            content,
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Reads a required boolean property of a control-channel answer.</summary>
    public static bool ReadFlag(JsonObject body, string property) =>
        body[property] is JsonValue value && value.TryGetValue(out bool flag)
            ? flag
            : throw new InvalidOperationException("The control channel answered without a boolean flag.");

    private Uri Compose(string relative)
    {
        UriBuilder baseAddress = new(options.Value.ControlBaseAddress!);
        if (!baseAddress.Path.EndsWith('/'))
        {
            baseAddress.Path += "/";
        }

        return new Uri(baseAddress.Uri, relative);
    }
}
