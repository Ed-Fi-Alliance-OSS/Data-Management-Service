// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[NonParallelizable]
public class Given_The_MapFallback_Correlation_Id
{
    private static WebApplicationFactory<Program> CreateFactory(
        string correlationIdHeader,
        int correlationIdMaxLength,
        string injectedCorrelationIdHeader,
        string injectedCorrelationId,
        string traceIdentifier = "server-trace-id"
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:CorrelationIdHeader"] = correlationIdHeader,
                            ["AppSettings:CorrelationIdMaxLength"] = correlationIdMaxLength.ToString(),
                        }
                    );
                }
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);
                services.AddSingleton<IStartupFilter>(
                    new CorrelationIdHeaderInjectionStartupFilter(
                        injectedCorrelationIdHeader,
                        injectedCorrelationId,
                        traceIdentifier
                    )
                );
            });
        });
    }

    [Test]
    public async Task It_returns_a_normalized_correlation_id_for_unmatched_routes()
    {
        const string hostileCorrelationId = "12\r{34}\t567890";
        await using WebApplicationFactory<Program> factory = CreateFactory(
            "correlationid",
            8,
            "correlationid",
            hostileCorrelationId
        );
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/missing-route");
        JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body["correlationId"]!.GetValue<string>().Should().Be("12{34}");
    }

    [Test]
    public async Task It_ignores_the_correlationid_header_when_client_supplied_correlation_ids_are_disabled()
    {
        const string hostileCorrelationId = "12\r{34}\t567890";
        const string hostileTraceIdentifier = "host\r{34}\t567890";
        await using WebApplicationFactory<Program> factory = CreateFactory(
            string.Empty,
            8,
            "correlationid",
            hostileCorrelationId,
            hostileTraceIdentifier
        );
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/missing-route");
        JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        string correlationId = body["correlationId"]!.GetValue<string>();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        correlationId.Should().Be("host{34");
        correlationId.Should().NotContain("12");
    }

    private sealed class CorrelationIdHeaderInjectionStartupFilter(
        string headerName,
        string headerValue,
        string traceIdentifier
    ) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(
                    async (context, nextMiddleware) =>
                    {
                        context.TraceIdentifier = traceIdentifier;
                        context.Request.Headers[headerName] = headerValue;
                        await nextMiddleware();
                    }
                );
                next(app);
            };
        }
    }
}
