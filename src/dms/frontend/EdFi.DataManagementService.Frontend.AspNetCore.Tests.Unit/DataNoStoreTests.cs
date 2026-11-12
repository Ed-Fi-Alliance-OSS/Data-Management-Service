// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Every response from an endpoint that serves education data to an authenticated client carries
/// exactly one <c>Cache-Control: no-store</c>, success shapes included, so no HTTP cache keeps a
/// copy of the records. The resource endpoints still send their <c>ETag</c>, so a client that keeps
/// it can revalidate with <c>If-None-Match</c>.
/// </summary>
[TestFixture]
[NonParallelizable]
public class DataNoStoreTests
{
    private static IFrontendResponse CannedResponse(int statusCode)
    {
        var response = A.Fake<IFrontendResponse>();
        A.CallTo(() => response.StatusCode).Returns(statusCode);
        A.CallTo(() => response.Body)
            .Returns(statusCode is 204 or 304 ? null : new JsonObject { ["id"] = "abc" });
        A.CallTo(() => response.Headers).Returns(new Dictionary<string, string> { ["ETag"] = "\"5249\"" });
        A.CallTo(() => response.ContentType).Returns("application/json");
        return response;
    }

    private static async Task<HttpResponseMessage> SendAsync(string method, string path, int statusCode)
    {
        var apiService = A.Fake<IApiService>();
        A.CallTo(apiService)
            .WithReturnType<Task<IFrontendResponse>>()
            .Returns(Task.FromResult(CannedResponse(statusCode)));

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);
                services.AddTransient(_ => apiService);
            });
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    private static IEnumerable<TestCaseData> DataResponses()
    {
        yield return new TestCaseData("GET", "/data/ed-fi/schools/abc", 200).SetName(
            "resource GET by id 200"
        );
        yield return new TestCaseData("GET", "/data/ed-fi/schools", 200).SetName("resource GET query 200");
        yield return new TestCaseData("GET", "/data/ed-fi/schools/abc", 304).SetName(
            "resource GET by id 304"
        );
        yield return new TestCaseData("POST", "/data/ed-fi/schools", 201).SetName("resource POST 201");
        yield return new TestCaseData("PUT", "/data/ed-fi/schools/abc", 204).SetName("resource PUT 204");
        yield return new TestCaseData("DELETE", "/data/ed-fi/schools/abc", 204).SetName(
            "resource DELETE 204"
        );
        yield return new TestCaseData("GET", "/data/ed-fi/schools/deletes", 200).SetName("deletes 200");
        yield return new TestCaseData("GET", "/data/ed-fi/schools/keyChanges", 200).SetName("keyChanges 200");
        yield return new TestCaseData("GET", "/changeQueries/v1/availableChangeVersions", 200).SetName(
            "availableChangeVersions 200"
        );
    }

    [TestCaseSource(nameof(DataResponses))]
    public async Task It_carries_cache_control_no_store_exactly_once(
        string method,
        string path,
        int statusCode
    )
    {
        using var response = await SendAsync(method, path, statusCode);

        ((int)response.StatusCode).Should().Be(statusCode);
        response.Headers.NonValidated["Cache-Control"].Should().Equal("no-store");
    }

    [Test]
    public async Task It_keeps_the_etag_on_a_resource_get()
    {
        using var response = await SendAsync("GET", "/data/ed-fi/schools/abc", 200);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.Should().Be("\"5249\"");
    }

    [Test]
    public async Task It_does_not_add_pragma_to_a_resource_get()
    {
        // Pragma: no-cache is the RFC 6749 token-response requirement, not part of this policy.
        using var response = await SendAsync("GET", "/data/ed-fi/schools/abc", 200);

        response.Headers.Contains("Pragma").Should().BeFalse();
    }
}
