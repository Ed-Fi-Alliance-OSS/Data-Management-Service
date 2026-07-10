// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_AspNetCoreFrontend_Response_Header_Writing
{
    [Test]
    public async Task It_writes_the_etag_header_as_a_quoted_strong_validator()
    {
        // The composed opaque _etag value (unquoted, as it appears in the JSON body); the ETag
        // response header must serve it as a quoted strong validator per RFC 9110 §8.8.3.
        const string opaqueEtag = "5-a1b2c3d4.j._.l";
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("localhost");
        httpContext.Request.Path = "/data/testproject/widgets";
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        var response = new FrontendResponse(
            StatusCode: 201,
            Body: null,
            Headers: new Dictionary<string, string> { ["etag"] = opaqueEtag },
            LocationHeaderPath: "/data/testproject/widgets/aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"
        );

        var toResultMethod =
            typeof(AspNetCoreFrontend).GetMethod("ToResult", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate AspNetCoreFrontend.ToResult.");

        var result =
            (IResult?)toResultMethod.Invoke(null, [response, httpContext, "/data/testproject/widgets"])
            ?? throw new InvalidOperationException("AspNetCoreFrontend.ToResult returned null.");

        await result.ExecuteAsync(httpContext);

        httpContext.Response.StatusCode.Should().Be(201);
        httpContext.Response.Headers.ETag.ToString().Should().Be($"\"{opaqueEtag}\"");
    }

    [Test]
    public async Task It_does_not_double_quote_or_emit_an_etag_header_for_an_empty_etag_value()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("localhost");
        httpContext.Request.Path = "/data/testproject/widgets";
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        var response = new FrontendResponse(
            StatusCode: 200,
            Body: null,
            Headers: new Dictionary<string, string> { ["etag"] = string.Empty }
        );

        var toResultMethod =
            typeof(AspNetCoreFrontend).GetMethod("ToResult", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate AspNetCoreFrontend.ToResult.");

        var result =
            (IResult?)toResultMethod.Invoke(null, [response, httpContext, "/data/testproject/widgets"])
            ?? throw new InvalidOperationException("AspNetCoreFrontend.ToResult returned null.");

        await result.ExecuteAsync(httpContext);

        httpContext.Response.Headers.ETag.ToString().Should().BeEmpty();
    }

    [Test]
    public async Task It_maps_a_304_response_to_a_quoted_etag_header_and_no_body()
    {
        // A conditional-GET short-circuit (If-None-Match matched the current representation) is
        // signaled by the Core handler as a 304 with a null body; the ETag header must still be
        // served as a quoted strong validator, and no body content should be written.
        const string opaqueEtag = "5-a1b2c3d4.j._.l";
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Host = new HostString("localhost");
        httpContext.Request.Path = "/data/testproject/widgets/aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb";
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddSingleton(A.Fake<IResponseCompressionProvider>())
            .BuildServiceProvider();
        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        var response = new FrontendResponse(
            StatusCode: 304,
            Body: null,
            Headers: new Dictionary<string, string> { ["etag"] = opaqueEtag }
        );

        var toResultMethod =
            typeof(AspNetCoreFrontend).GetMethod("ToResult", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate AspNetCoreFrontend.ToResult.");

        var result =
            (IResult?)toResultMethod.Invoke(null, [response, httpContext, "/data/testproject/widgets"])
            ?? throw new InvalidOperationException("AspNetCoreFrontend.ToResult returned null.");

        await result.ExecuteAsync(httpContext);

        httpContext.Response.StatusCode.Should().Be(304);
        httpContext.Response.Headers.ETag.ToString().Should().Be($"\"{opaqueEtag}\"");
        responseBody.Length.Should().Be(0);
        httpContext
            .Response.Headers.GetCommaSeparatedValues("Vary")
            .Should()
            .BeEquivalentTo("Accept", "Accept-Encoding");
    }

    [Test]
    public async Task It_varies_a_successful_get_without_a_response_etag_by_accept_and_accept_encoding()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Host = new HostString("localhost");
        httpContext.Request.Path = "/data/testproject/widgets";
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddSingleton(A.Fake<IResponseCompressionProvider>())
            .BuildServiceProvider();

        var response = new FrontendResponse(StatusCode: 200, Body: new JsonArray(), Headers: []);

        var toResultMethod =
            typeof(AspNetCoreFrontend).GetMethod("ToResult", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate AspNetCoreFrontend.ToResult.");

        var result =
            (IResult?)toResultMethod.Invoke(null, [response, httpContext, "/data/testproject/widgets"])
            ?? throw new InvalidOperationException("AspNetCoreFrontend.ToResult returned null.");

        await result.ExecuteAsync(httpContext);

        httpContext
            .Response.Headers.GetCommaSeparatedValues("Vary")
            .Should()
            .BeEquivalentTo("Accept", "Accept-Encoding");
    }

    [Test]
    public async Task It_varies_a_successful_get_by_accept_when_response_compression_is_disabled()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Host = new HostString("localhost");
        httpContext.Request.Path = "/data/testproject/widgets";
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        var response = new FrontendResponse(StatusCode: 200, Body: new JsonArray(), Headers: []);

        var toResultMethod =
            typeof(AspNetCoreFrontend).GetMethod("ToResult", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate AspNetCoreFrontend.ToResult.");

        var result =
            (IResult?)toResultMethod.Invoke(null, [response, httpContext, "/data/testproject/widgets"])
            ?? throw new InvalidOperationException("AspNetCoreFrontend.ToResult returned null.");

        await result.ExecuteAsync(httpContext);

        httpContext.Response.Headers.GetCommaSeparatedValues("Vary").Should().ContainSingle("Accept");
    }
}
