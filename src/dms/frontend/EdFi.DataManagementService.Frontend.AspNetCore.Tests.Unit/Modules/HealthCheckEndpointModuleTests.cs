// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
[NonParallelizable]
public class Given_HealthCheckEndpointModule
{
    private const string ValidRequiredRole = "dms-document-cache-operator";

    private static WebApplicationFactory<Program> CreateFactory(
        ScriptedDocumentCacheStatusService documentCacheStatusService,
        string? requiredRole
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    if (requiredRole is not null)
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["DataManagement:DocumentCache:Status:RequiredRole"] = requiredRole,
                            }
                        );
                    }
                }
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);
                services.Replace(
                    ServiceDescriptor.Singleton<IDocumentCacheStatusService>(documentCacheStatusService)
                );
            });
        });
    }

    private static ScriptedDocumentCacheStatusService EmptyStatusService() =>
        new(new DocumentCacheStatusResponse(new DateTimeOffset(2026, 8, 17, 12, 34, 56, TimeSpan.Zero), []));

    [Test]
    public async Task It_omits_document_cache_status_when_required_role_is_missing()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            requiredRole: null
        );
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_omits_document_cache_status_when_required_role_is_invalid()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            requiredRole: "dms document cache operator"
        );
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_document_cache_status_when_required_role_is_valid()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            ValidRequiredRole
        );
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/document-cache");
        string content = await response.Content.ReadAsStringAsync();
        JsonNode json = JsonNode.Parse(content)!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json["contractVersion"]!.GetValue<int>().Should().Be(1);
        json["observedAt"]!.GetValue<string>().Should().Be("2026-08-17T12:34:56Z");
        json["targets"]!.AsArray().Should().BeEmpty();
        documentCacheStatusService.CallCount.Should().Be(1);
    }

    [Test]
    public async Task It_keeps_the_existing_health_endpoint_independent_from_document_cache_status()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        await using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            ValidRequiredRole
        );
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health");
        string content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("\"Name\": \"ApplicationHealthCheck\"");
        content.Should().NotContain("document-cache");
        documentCacheStatusService.CallCount.Should().Be(0);
    }

    [Test]
    public void It_excludes_document_cache_status_from_OpenApi_description_metadata()
    {
        ScriptedDocumentCacheStatusService documentCacheStatusService = EmptyStatusService();
        using WebApplicationFactory<Program> factory = CreateFactory(
            documentCacheStatusService,
            ValidRequiredRole
        );
        using HttpClient client = factory.CreateClient();

        RouteEndpoint endpoint = factory
            .Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/health/document-cache");

        endpoint
            .Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()!
            .ExcludeFromDescription.Should()
            .BeTrue();
    }

    private sealed class ScriptedDocumentCacheStatusService(DocumentCacheStatusResponse response)
        : IDocumentCacheStatusService
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<DocumentCacheStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(response);
        }
    }
}
