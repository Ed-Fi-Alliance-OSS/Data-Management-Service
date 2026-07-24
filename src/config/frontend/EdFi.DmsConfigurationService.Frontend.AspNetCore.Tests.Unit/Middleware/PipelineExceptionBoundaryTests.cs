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
public class PipelineExceptionBoundaryTests
{
    [TestFixture]
    public class Given_tenant_resolution_throws_an_unexpected_exception
    {
        private const string Sentinel = "SENTINEL_TENANT_REPO_7f3a91_must_not_leak";

        private ITenantRepository _tenantRepository = null!;
        private TestLogger<RequestLoggingMiddleware> _recordingLogger = null!;
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _response = null!;
        private string _content = null!;
        private JsonObject _body = null!;

        [SetUp]
        public async Task Setup()
        {
            _tenantRepository = A.Fake<ITenantRepository>();
            A.CallTo(() => _tenantRepository.GetTenantByName(A<string>._))
                .Throws(new InvalidOperationException(Sentinel));
            _recordingLogger = new TestLogger<RequestLoggingMiddleware>();

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("AppSettings:MultiTenancy", "true");
                builder.ConfigureServices(collection =>
                {
                    collection.Configure<AppSettings>(options => options.MultiTenancy = true);
                    collection.AddTransient<ITenantRepository>(_ => _tenantRepository);
                    collection.AddSingleton<ILogger<RequestLoggingMiddleware>>(_recordingLogger);
                });
            });
            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Add("Tenant", "some-tenant");

            // A non-bypassed route with a valid Tenant header reaches tenant resolution, which throws.
            _response = await _client.GetAsync("/v3/applications");
            _content = await _response.Content.ReadAsStringAsync();
            _body = JsonNode.Parse(_content)!.AsObject();
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _factory?.Dispose();
        }

        [Test]
        public void It_reaches_the_tenant_repository() =>
            A.CallTo(() => _tenantRepository.GetTenantByName(A<string>._)).MustHaveHappened();

        [Test]
        public void It_returns_500() => _response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_uses_the_problem_details_content_type() =>
            _response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        [Test]
        public void It_returns_the_internal_server_error_type() =>
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:internal-server-error");

        [Test]
        public void It_has_the_internal_server_error_title() =>
            _body["title"]!.GetValue<string>().Should().Be("Internal Server Error");

        [Test]
        public void It_has_an_empty_detail() => _body["detail"]!.GetValue<string>().Should().BeEmpty();

        [Test]
        public void It_has_a_body_status_of_500() => _body["status"]!.GetValue<int>().Should().Be(500);

        [Test]
        public void It_includes_a_non_empty_correlation_id() =>
            _body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        [Test]
        public void It_includes_empty_extension_members()
        {
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
            _body["errors"]!.AsArray().Count.Should().Be(0);
        }

        [Test]
        public void It_does_not_leak_the_exception_text() => _content.Should().NotContain(Sentinel);

        [Test]
        public void It_matches_the_trace_id_header_to_the_correlation_id() =>
            _response
                .Headers.GetValues("TraceId")
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(_body["correlationId"]!.GetValue<string>());

        [Test]
        public void It_logs_exactly_one_http_request_failed_event() =>
            _recordingLogger
                .Entries.Count(entry => entry.EventId.Id == RequestLoggingEventIds.HttpRequestFailed.Id)
                .Should()
                .Be(1);
    }
}
