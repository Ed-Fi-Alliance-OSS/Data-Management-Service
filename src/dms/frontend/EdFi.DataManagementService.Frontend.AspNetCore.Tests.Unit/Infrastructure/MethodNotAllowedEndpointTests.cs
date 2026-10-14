// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Drives MethodNotAllowedEndpoint through the two POST-only routes that map it, /oauth/token and
/// /oauth/token_info, on the real application. Without it, the catch-all fallback answers these
/// requests with 404.
/// </summary>
[NonParallelizable]
public class MethodNotAllowedEndpointTests
{
    private static HttpResponseMessage Send(
        HttpMethod method,
        string path,
        bool multiTenancy = false,
        HttpContent? content = null
    )
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                if (multiTenancy)
                {
                    collection.Configure<AppSettings>(opts =>
                    {
                        opts.MultiTenancy = true;
                        opts.RouteQualifierSegments = "schoolYear";
                    });
                }
            });
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, path) { Content = content };

        HttpResponseMessage response = client.SendAsync(request).GetAwaiter().GetResult();
        // Buffer the body before the factory is disposed.
        response.Content.LoadIntoBufferAsync().GetAwaiter().GetResult();
        return response;
    }

    [TestFixture("GET", "/oauth/token", false)]
    [TestFixture("PUT", "/oauth/token", false)]
    [TestFixture("DELETE", "/oauth/token", false)]
    [TestFixture("PATCH", "/oauth/token", false)]
    [TestFixture("GET", "/tenant1/2026/oauth/token", true)]
    [TestFixture("GET", "/oauth/token", true)]
    [TestFixture("GET", "/oauth/token_info", false)]
    [TestFixture("DELETE", "/tenant1/2026/oauth/token_info", true)]
    public class Given_An_Unsupported_Method_On_A_Post_Only_Route(
        string method,
        string path,
        bool multiTenancy
    ) : MethodNotAllowedEndpointTests
    {
        private HttpResponseMessage _response = null!;
        private JsonNode _body = null!;

        [SetUp]
        public void SetUp()
        {
            _response = Send(new HttpMethod(method), path, multiTenancy);
            _body = JsonNode.Parse(_response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_responds_with_method_not_allowed()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        }

        [Test]
        public void It_advertises_post_as_the_only_allowed_method()
        {
            _response.Content.Headers.Allow.Should().Equal("POST");
        }

        [Test]
        public void It_returns_the_method_not_allowed_problem_details()
        {
            _body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:method-not-allowed");
            _body["title"]!.GetValue<string>().Should().Be("Method Not Allowed");
            _body["status"]!.GetValue<int>().Should().Be(405);
        }

        [Test]
        public void It_names_the_rejected_method()
        {
            _body["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal($"The endpoint of the request does not support the '{method}' method.");
        }
    }

    [TestFixture]
    public class Given_A_Head_Request_On_The_Token_Route : MethodNotAllowedEndpointTests
    {
        private HttpResponseMessage _response = null!;

        [SetUp]
        public void SetUp()
        {
            _response = Send(HttpMethod.Head, "/oauth/token");
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_responds_with_method_not_allowed()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        }

        [Test]
        public void It_advertises_post_as_the_only_allowed_method()
        {
            _response.Content.Headers.Allow.Should().Equal("POST");
        }
    }

    /// <summary>
    /// A POST that the token route's content-type-specific endpoints turn down must not be reported as
    /// a 405: POST is the method the route supports. The terminal maps every method except POST for
    /// exactly this reason, and a method-less terminal would answer this request 405. It still reaches
    /// the fallback, as it did before the terminal existed.
    /// </summary>
    [TestFixture]
    public class Given_A_Post_With_An_Unsupported_Content_Type_On_The_Token_Route
        : MethodNotAllowedEndpointTests
    {
        private HttpResponseMessage _response = null!;

        [SetUp]
        public void SetUp()
        {
            _response = Send(
                HttpMethod.Post,
                "/oauth/token",
                content: new StringContent("grant_type=client_credentials", Encoding.UTF8, "text/plain")
            );
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_is_not_answered_by_the_method_not_allowed_terminal()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void It_does_not_advertise_allowed_methods()
        {
            _response.Content.Headers.Allow.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_A_Path_That_Only_Resembles_The_Token_Route : MethodNotAllowedEndpointTests
    {
        private HttpResponseMessage _response = null!;

        [SetUp]
        public void SetUp()
        {
            _response = Send(HttpMethod.Get, "/oauth/tokens");
        }

        [TearDown]
        public void TearDown()
        {
            _response.Dispose();
        }

        [Test]
        public void It_still_responds_with_not_found()
        {
            _response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }
}
