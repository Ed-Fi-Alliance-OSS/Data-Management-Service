// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

/// <summary>
/// Verifies the DMS-1218 INV-36 pipeline ordering: because <c>UseExceptionHandler</c> is registered
/// ahead of <c>TenantResolutionMiddleware</c>, an unexpected exception thrown during tenant
/// resolution is shaped by GlobalExceptionHandler into the Ed-Fi internal-server-error contract
/// (rather than an unshaped framework 500), and — because RequestLoggingMiddleware remains outermost —
/// the handled failure is logged exactly once as HttpRequestFailed.
/// </summary>
[TestFixture]
public class PipelineExceptionBoundaryTests
{
    private const string Sentinel = "SENTINEL_TENANT_REPO_7f3a91_must_not_leak";

    [Test]
    public async Task Tenant_resolution_exception_is_shaped_as_ed_fi_500_and_logged_once()
    {
        // Arrange: multitenancy on, a tenant repository that throws, and a recording logger for
        // RequestLoggingMiddleware so we can count HttpRequestFailed events.
        var tenantRepository = A.Fake<ITenantRepository>();
        A.CallTo(() => tenantRepository.GetTenantByName(A<string>._))
            .Throws(new InvalidOperationException(Sentinel));

        var recordingLogger = new TestLogger<RequestLoggingMiddleware>();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("AppSettings:MultiTenancy", "true");
            builder.ConfigureServices(collection =>
            {
                collection.Configure<AppSettings>(options => options.MultiTenancy = true);
                collection.AddTransient<ITenantRepository>(_ => tenantRepository);
                collection.AddSingleton<ILogger<RequestLoggingMiddleware>>(recordingLogger);
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Tenant", "some-tenant");

        // Act: a non-bypassed route with a valid Tenant header reaches tenant resolution, which throws.
        var response = await client.GetAsync("/v3/applications");
        string content = await response.Content.ReadAsStringAsync();

        // Assert: the repository was actually reached.
        A.CallTo(() => tenantRepository.GetTenantByName(A<string>._)).MustHaveHappened();

        // Assert: the full Ed-Fi internal-server-error contract, with no leaked exception text.
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        content.Should().NotContain(Sentinel);

        JsonNode body = JsonNode.Parse(content)!;
        body["detail"]!.GetValue<string>().Should().BeEmpty();
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:internal-server-error");
        body["title"]!.GetValue<string>().Should().Be("Internal Server Error");
        body["status"]!.GetValue<int>().Should().Be(500);
        string correlationId = body["correlationId"]!.GetValue<string>();
        correlationId.Should().NotBeNullOrEmpty();
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);

        // Assert: the TraceId response header matches the body's correlationId.
        response.Headers.GetValues("TraceId").Should().ContainSingle().Which.Should().Be(correlationId);

        // Assert: exactly one HttpRequestFailed event for the handled tenant exception.
        recordingLogger
            .Entries.Count(entry => entry.EventId.Id == RequestLoggingEventIds.HttpRequestFailed.Id)
            .Should()
            .Be(1);
    }
}
