// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.DataModel.Model.Register;
using EdFi.DmsConfigurationService.DataModel.Model.Token;
using FakeItEasy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// An isolated host for the DMS-1482 limiter tests. Every factory gets its own ConnectRateLimiter
/// singleton, so budgets never leak between fixtures. A null limit leaves the value to the shipped or
/// environment configuration. The token and revocation managers default to fakes that always succeed,
/// so an admitted request is answered 200 and a rejected one 429.
/// </summary>
internal static class ConnectRateLimitTestHost
{
    public const string ClientSecret = "test123@Puiu";

    public static WebApplicationFactory<Program> Create(
        int? sourcePermitLimit = null,
        int? clientPermitLimit = null,
        bool? enabled = null,
        ITokenManager? tokenManager = null,
        ITokenRevocationManager? revocationManager = null
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    Dictionary<string, string?> settings = [];
                    if (enabled is not null)
                    {
                        settings["ConnectRateLimit:Enabled"] = enabled.Value.ToString();
                    }
                    if (sourcePermitLimit is not null)
                    {
                        settings["ConnectRateLimit:SourcePermitLimit"] = sourcePermitLimit.Value.ToString(
                            CultureInfo.InvariantCulture
                        );
                    }
                    if (clientPermitLimit is not null)
                    {
                        settings["ConnectRateLimit:ClientPermitLimit"] = clientPermitLimit.Value.ToString(
                            CultureInfo.InvariantCulture
                        );
                    }
                    configuration.AddInMemoryCollection(settings);
                }
            );
            builder.ConfigureServices(collection =>
            {
                collection.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
                collection.AddTransient(_ => new TokenRequest.Validator());
                collection.AddTransient(_ => new RegisterRequest.Validator());
                ITokenManager tokens = tokenManager ?? SucceedingTokenManager();
                collection.AddTransient(_ => tokens);
                collection.RemoveAll<ITokenRevocationManager>();
                collection.AddSingleton(revocationManager ?? CompletingRevocationManager());
            });
        });

    public static ITokenManager SucceedingTokenManager()
    {
        ITokenManager tokenManager = A.Fake<ITokenManager>();
        A.CallTo(() => tokenManager.GetAccessTokenAsync(A<IEnumerable<KeyValuePair<string, string>>>._))
            .Returns(
                new TokenResult.Success(
                    """{"access_token":"input123token","expires_in":900,"token_type":"bearer"}"""
                )
            );
        return tokenManager;
    }

    public static ITokenRevocationManager CompletingRevocationManager()
    {
        ITokenRevocationManager revocationManager = A.Fake<ITokenRevocationManager>();
        A.CallTo(() =>
                revocationManager.RevokeTokenAsync(A<TokenRevocationRequest>._, A<CancellationToken>._)
            )
            .Returns(new TokenRevocationResult.Completed());
        return revocationManager;
    }

    public static FormUrlEncodedContent TokenForm(string clientId, string grantType = "client_credentials") =>
        new([new("client_id", clientId), new("client_secret", ClientSecret), new("grant_type", grantType)]);

    public static FormUrlEncodedContent RevokeForm(string clientId) =>
        new([new("token", "opaque-token"), new("client_id", clientId), new("client_secret", ClientSecret)]);

    public static FormUrlEncodedContent EmptyForm() => new(Array.Empty<KeyValuePair<string, string>>());

    public static HttpRequestMessage Post(string path, HttpContent content, string? remoteIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (remoteIp is not null)
        {
            request.Headers.Add(RemoteIpStartupFilter.HeaderName, remoteIp);
        }
        return request;
    }

    public static AuthenticationHeaderValue Basic(string clientId) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{ClientSecret}")));

    public static async Task<List<int>> SendAll(
        HttpClient client,
        IEnumerable<Func<HttpRequestMessage>> requests
    )
    {
        List<int> statuses = [];
        foreach (Func<HttpRequestMessage> request in requests)
        {
            using HttpResponseMessage response = await client.SendAsync(request());
            statuses.Add((int)response.StatusCode);
        }
        return statuses;
    }
}
