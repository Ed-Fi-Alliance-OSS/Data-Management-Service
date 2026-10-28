// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://0.0.0.0:8080", "http://0.0.0.0:18987");
string upstreamUri =
    builder.Configuration["TOKEN_PROXY_UPSTREAM_URI"]
    ?? throw new InvalidOperationException("TOKEN_PROXY_UPSTREAM_URI is required.");
if (
    !Uri.TryCreate(upstreamUri, UriKind.Absolute, out var upstream)
    || upstream.Scheme is not ("http" or "https")
)
{
    throw new InvalidOperationException("TOKEN_PROXY_UPSTREAM_URI must be an absolute HTTP URI.");
}

builder
    .Services.AddHttpClient("upstream", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }
    );
WebApplication app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapPost("/token", ForwardOrFixture);
await app.RunAsync();

async Task ForwardOrFixture(HttpContext context, IHttpClientFactory clients)
{
    string fixture = GetFixtureId(context.Request.Headers.Authorization.ToString());
    if (fixture.Length == 0)
    {
        await Forward(context, clients.CreateClient("upstream"), upstream);
        return;
    }

    switch (fixture)
    {
        case "dms1606-401-object":
            await WriteJson(context, 401, "invalid_client", "Fixture rejected credentials");
            return;
        case "dms1606-401-html":
            context.Response.StatusCode = 401;
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html>withheld-provider-sentinel</html>");
            return;
        case "dms1606-400-invalid-client":
            await WriteJson(context, 400, "invalid_client", "Fixture rejected request");
            return;
        case "dms1606-400-invalid-scope":
            await WriteJson(context, 400, "invalid_scope", "Fixture rejected request");
            return;
        case "dms1606-400-unknown":
            await WriteJson(context, 400, "fixture_unknown", "Fixture rejected request");
            return;
        case "dms1606-429-legacy":
            await WriteLimit(context, "urn:ed-fi:api:security:authentication:too-many-tokens");
            return;
        case "dms1606-429-management":
            await WriteLimit(context, "urn:ed-fi:management-api:security:authentication:too-many-tokens");
            return;
        case "dms1606-429-generic":
            await WriteRaw(
                context,
                429,
                "application/json",
                "{\"error\":\"temporarily_unavailable\",\"nested\":{\"sentinel\":\"withheld-provider-sentinel\"}}"
            );
            return;
        case "dms1606-409":
            await WriteRaw(context, 409, "application/json", "{\"sentinel\":\"withheld-provider-sentinel\"}");
            return;
        case "dms1606-500":
            await WriteRaw(context, 500, "application/json", "{\"sentinel\":\"withheld-provider-sentinel\"}");
            return;
        case "dms1606-abort":
            context.Abort();
            return;
        default:
            await Forward(context, clients.CreateClient("upstream"), upstream);
            return;
    }
}

static string GetFixtureId(string authorization)
{
    if (
        !AuthenticationHeaderValue.TryParse(authorization, out var parsed)
        || !parsed.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
        || parsed.Parameter is null
    )
    {
        return string.Empty;
    }

    try
    {
        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter));
        int separator = decoded.IndexOf(':');
        return separator < 0 ? string.Empty : Uri.UnescapeDataString(decoded[..separator]);
    }
    catch (FormatException)
    {
        return string.Empty;
    }
}

static Task WriteLimit(HttpContext context, string code) =>
    WriteRaw(
        context,
        429,
        "application/json",
        JsonSerializer.Serialize(
            new
            {
                type = code,
                status = 429,
                errors = new[]
                {
                    "Too many access tokens have been requested (limit is 7). Access tokens should be reused until they expire.",
                },
            }
        )
    );

static async Task WriteJson(HttpContext context, int status, string code, string description) =>
    await WriteRaw(
        context,
        status,
        "application/json",
        JsonSerializer.Serialize(new { error = code, error_description = description })
    );

static async Task WriteRaw(HttpContext context, int status, string contentType, string body)
{
    context.Response.StatusCode = status;
    context.Response.ContentType = contentType;
    await context.Response.WriteAsync(body);
}

static async Task Forward(HttpContext context, HttpClient client, Uri upstream)
{
    using HttpRequestMessage request = new(HttpMethod.Post, upstream);
    request.Content = new StreamContent(context.Request.Body);
    if (context.Request.ContentType is not null)
    {
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
    }

    if (context.Request.Headers.TryGetValue("Authorization", out var authorization))
    {
        request.Headers.TryAddWithoutValidation("Authorization", authorization.ToArray());
    }

    using HttpResponseMessage response = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        context.RequestAborted
    );
    context.Response.StatusCode = (int)response.StatusCode;
    if (response.Content.Headers.ContentType is not null)
    {
        context.Response.ContentType = response.Content.Headers.ContentType.ToString();
    }

    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
}
