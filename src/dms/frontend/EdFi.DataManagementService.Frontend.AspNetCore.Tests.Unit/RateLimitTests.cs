// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[NonParallelizable]
public class Given_Requests_That_Exceed_The_Configured_Rate_Limit
{
    // The TestRateLimit environment permits one request per sixty-second window. The wide
    // window keeps every request in this fixture inside a single window, so no assertion
    // depends on timing and nothing waits for a reset.
    private const int ConfiguredWindowSeconds = 60;
    private const string SuppliedCorrelationId = "supplied-correlation-id";

    private WebApplicationFactory<Program> _factory = default!;
    private HttpClient _client = default!;
    private HttpResponseMessage _permittedResponse = default!;
    private HttpResponseMessage _rejectedResponse = default!;
    private HttpResponseMessage _rejectedResponseWithoutCorrelationHeader = default!;
    private JsonNode _rejectedBody = default!;
    private JsonNode _rejectedBodyWithoutCorrelationHeader = default!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // This environment has an extreme rate limit
            builder.UseEnvironment("TestRateLimit");
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                }
            );
        });
        _client = _factory.CreateClient();

        _permittedResponse = await _client.GetAsync("/health");

        using var overLimitRequest = new HttpRequestMessage(HttpMethod.Get, "/health");
        overLimitRequest.Headers.Add("correlationid", SuppliedCorrelationId);
        _rejectedResponse = await _client.SendAsync(overLimitRequest);
        _rejectedBody = JsonNode.Parse(await _rejectedResponse.Content.ReadAsStringAsync())!;

        _rejectedResponseWithoutCorrelationHeader = await _client.GetAsync("/health");
        _rejectedBodyWithoutCorrelationHeader = JsonNode.Parse(
            await _rejectedResponseWithoutCorrelationHeader.Content.ReadAsStringAsync()
        )!;
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        _permittedResponse.Dispose();
        _rejectedResponse.Dispose();
        _rejectedResponseWithoutCorrelationHeader.Dispose();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Test]
    public void It_permits_the_first_request_in_the_window()
    {
        _permittedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_rejects_requests_over_the_limit_with_429()
    {
        _rejectedResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        _rejectedResponseWithoutCorrelationHeader.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public void It_serves_a_retry_after_header_in_whole_seconds_within_the_window()
    {
        // Delta parses only from the integer delta-seconds form, so a non-null value also
        // pins the header format. The range assertion avoids depending on how much of the
        // window has elapsed when the rejected request is issued.
        TimeSpan? delta = _rejectedResponse.Headers.RetryAfter?.Delta;

        delta.Should().NotBeNull();
        delta!.Value.TotalSeconds.Should().BeGreaterThanOrEqualTo(1);
        delta.Value.TotalSeconds.Should().BeLessThanOrEqualTo(ConfiguredWindowSeconds);
    }

    [Test]
    public void It_serves_the_problem_json_content_type()
    {
        _rejectedResponse.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        _rejectedResponse.Content.Headers.ContentType!.CharSet.Should().Be("utf-8");
    }

    [Test]
    public void It_serves_the_too_many_requests_problem_details_envelope()
    {
        _rejectedBody["type"]!.ToString().Should().Be("urn:ed-fi:api:too-many-requests");
        _rejectedBody["title"]!.ToString().Should().Be("Too Many Requests");
        _rejectedBody["status"]!.GetValue<int>().Should().Be(429);
        _rejectedBody["detail"]!
            .ToString()
            .Should()
            .Be("The number of allowed requests has been exceeded. Retry the request later.");
        _rejectedBody["validationErrors"]!.AsObject().Count.Should().Be(0);
        _rejectedBody["errors"]!.AsArray().Count.Should().Be(0);
    }

    [Test]
    public void It_echoes_the_configured_correlation_header_as_the_correlation_id()
    {
        _rejectedBody["correlationId"]!.ToString().Should().Be(SuppliedCorrelationId);
    }

    [Test]
    public void It_falls_back_to_the_trace_identifier_when_no_correlation_header_is_supplied()
    {
        string correlationId = _rejectedBodyWithoutCorrelationHeader["correlationId"]!.ToString();

        correlationId.Should().NotBeNullOrEmpty();
        correlationId.Should().NotBe(SuppliedCorrelationId);
    }

    [Test]
    public void It_preserves_the_security_headers_on_the_rejected_response()
    {
        _rejectedResponse
            .Headers.GetValues("X-Content-Type-Options")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("nosniff");
        _rejectedResponse
            .Headers.GetValues("Referrer-Policy")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("no-referrer");
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Rate_Limit_Rejection_Without_Retry_After_Metadata
{
    private const string SuppliedTraceIdentifier = "test-trace-identifier";

    private DefaultHttpContext _httpContext = default!;
    private JsonNode _body = default!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _httpContext = RateLimitRejectionTestContext.CreateHttpContext();
        _httpContext.TraceIdentifier = SuppliedTraceIdentifier;

        await WebApplicationBuilderExtensions.WriteRateLimitRejectionAsync(
            new OnRejectedContext
            {
                HttpContext = _httpContext,
                Lease = new RateLimitRejectionTestContext.StubLease(retryAfter: null),
            },
            CancellationToken.None
        );

        _body = await RateLimitRejectionTestContext.ReadBody(_httpContext);
    }

    [Test]
    public void It_does_not_set_a_retry_after_header()
    {
        _httpContext.Response.Headers.ContainsKey("Retry-After").Should().BeFalse();
    }

    [Test]
    public void It_sets_the_problem_json_content_type()
    {
        _httpContext.Response.ContentType.Should().Be("application/problem+json; charset=utf-8");
    }

    [Test]
    public void It_still_writes_the_too_many_requests_problem_details_body()
    {
        _body["type"]!.ToString().Should().Be("urn:ed-fi:api:too-many-requests");
        _body["status"]!.GetValue<int>().Should().Be(429);
        _body["correlationId"]!.ToString().Should().Be(SuppliedTraceIdentifier);
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Rate_Limit_Rejection_With_Sub_Second_Retry_After_Metadata
{
    private DefaultHttpContext _httpContext = default!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _httpContext = RateLimitRejectionTestContext.CreateHttpContext();

        await WebApplicationBuilderExtensions.WriteRateLimitRejectionAsync(
            new OnRejectedContext
            {
                HttpContext = _httpContext,
                Lease = new RateLimitRejectionTestContext.StubLease(
                    retryAfter: TimeSpan.FromMilliseconds(200)
                ),
            },
            CancellationToken.None
        );
    }

    [Test]
    public void It_rounds_the_retry_after_header_up_to_one_second()
    {
        _httpContext.Response.Headers.RetryAfter.ToString().Should().Be("1");
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Rate_Limit_Rejection_With_Multi_Window_Retry_After_Metadata
{
    private DefaultHttpContext _httpContext = default!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        // Queued demand can make the limiter recommend a delay spanning several configured
        // windows; the handler must serve that recommendation rather than clamp it to one window.
        _httpContext = RateLimitRejectionTestContext.CreateHttpContext();

        await WebApplicationBuilderExtensions.WriteRateLimitRejectionAsync(
            new OnRejectedContext
            {
                HttpContext = _httpContext,
                Lease = new RateLimitRejectionTestContext.StubLease(retryAfter: TimeSpan.FromSeconds(90.4)),
            },
            CancellationToken.None
        );
    }

    [Test]
    public void It_serves_the_recommended_delay_rounded_up_as_the_retry_after_header()
    {
        _httpContext.Response.Headers.RetryAfter.ToString().Should().Be("91");
    }
}

/// <summary>
/// Builds the minimal HttpContext the rejection handler needs (request services carrying the
/// frontend AppSettings options and a readable response body), plus a lease stub whose
/// retry-after metadata can be present or absent.
/// </summary>
internal static class RateLimitRejectionTestContext
{
    public static DefaultHttpContext CreateHttpContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton(
            Options.Create(
                new AppSettings
                {
                    AuthenticationService = "http://test-auth-service",
                    Datastore = "postgresql",
                    CorrelationIdHeader = "",
                }
            )
        );

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
    }

    public static async Task<JsonNode> ReadBody(DefaultHttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        return JsonNode.Parse(await reader.ReadToEndAsync())!;
    }

    public sealed class StubLease(TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is not null && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter.Value;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
