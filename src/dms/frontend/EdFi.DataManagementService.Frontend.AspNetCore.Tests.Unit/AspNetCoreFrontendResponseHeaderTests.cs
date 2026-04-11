// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.Model;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_AspNetCoreFrontend_Response_Header_Writing
{
    [Test]
    public async Task It_writes_a_valid_etag_header_for_frontend_responses()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("localhost");
        httpContext.Request.Path = "/data/testproject/widgets";
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        var response = new FrontendResponse(
            StatusCode: 201,
            Body: null,
            Headers: new Dictionary<string, string> { ["etag"] = "\"61\"" },
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
        httpContext.Response.Headers.ETag.ToString().Should().Be("\"61\"");
    }
}
