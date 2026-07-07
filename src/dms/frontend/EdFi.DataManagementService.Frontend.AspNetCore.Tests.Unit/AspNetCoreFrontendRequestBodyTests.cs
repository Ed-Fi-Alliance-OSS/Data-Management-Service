// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_AspNetCoreFrontend_Request_Body_Extraction
{
    private static IOptions<AppSettings> AppSettings() =>
        Options.Create(
            new AppSettings
            {
                AuthenticationService = "test",
                Datastore = "postgresql",
                CorrelationIdHeader = "X-Correlation-ID",
            }
        );

    private static DefaultHttpContext CreateHttpContext(string body, string? contentType = null)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        return CreateHttpContext(bodyBytes, contentType);
    }

    private static DefaultHttpContext CreateHttpContext(byte[] bodyBytes, string? contentType = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = contentType;
        return httpContext;
    }

    private static IApiService CreateApiServiceForUpsert(Action<FrontendRequest> captureRequest)
    {
        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.Upsert(A<FrontendRequest>._))
            .Invokes(call => captureRequest(call.GetArgument<FrontendRequest>(0)!))
            .Returns(Task.FromResult<IFrontendResponse>(new FrontendResponse(200, null, [])));

        return apiService;
    }

    private static IApiService CreateApiServiceForTokenInfo(Action<FrontendRequest> captureRequest)
    {
        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.GetTokenInfo(A<FrontendRequest>._))
            .Invokes(call => captureRequest(call.GetArgument<FrontendRequest>(0)!))
            .Returns(Task.FromResult<IFrontendResponse>(new FrontendResponse(200, null, [])));

        return apiService;
    }

    [Test]
    public async Task It_parses_json_request_bodies_without_setting_the_raw_body_string()
    {
        FrontendRequest? capturedRequest = null;
        var httpContext = CreateHttpContext("""{ "id":"value", "name":"School" }""");
        var apiService = CreateApiServiceForUpsert(request => capturedRequest = request);

        await AspNetCoreFrontend.Upsert(httpContext, apiService, "ed-fi/schools", AppSettings());

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Body.Should().BeNull();
        capturedRequest.ParsedBody.Should().NotBeNull();
        capturedRequest.ParsedBody!.ToJsonString().Should().Be("""{"id":"value","name":"School"}""");
        capturedRequest.BodyParseErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task It_parses_utf8_bom_prefixed_json_request_bodies()
    {
        FrontendRequest? capturedRequest = null;
        byte[] bodyBytes =
        [
            0xEF,
            0xBB,
            0xBF,
            .. Encoding.UTF8.GetBytes("""{ "id":"value", "name":"School" }"""),
        ];
        var httpContext = CreateHttpContext(bodyBytes);
        var apiService = CreateApiServiceForUpsert(request => capturedRequest = request);

        await AspNetCoreFrontend.Upsert(httpContext, apiService, "ed-fi/schools", AppSettings());

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Body.Should().BeNull();
        capturedRequest.ParsedBody.Should().NotBeNull();
        capturedRequest.ParsedBody!.ToJsonString().Should().Be("""{"id":"value","name":"School"}""");
        capturedRequest.BodyParseErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task It_treats_whitespace_only_json_request_bodies_as_missing_bodies()
    {
        FrontendRequest? capturedRequest = null;
        var httpContext = CreateHttpContext(" \r\n\t\v\f ");
        var apiService = CreateApiServiceForUpsert(request => capturedRequest = request);

        await AspNetCoreFrontend.Upsert(httpContext, apiService, "ed-fi/schools", AppSettings());

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Body.Should().BeNull();
        capturedRequest.ParsedBody.Should().BeNull();
        capturedRequest.BodyParseErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task It_treats_unicode_whitespace_only_json_request_bodies_as_missing_bodies()
    {
        FrontendRequest? capturedRequest = null;
        var httpContext = CreateHttpContext("\u00A0\u2003");
        var apiService = CreateApiServiceForUpsert(request => capturedRequest = request);

        await AspNetCoreFrontend.Upsert(httpContext, apiService, "ed-fi/schools", AppSettings());

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Body.Should().BeNull();
        capturedRequest.ParsedBody.Should().BeNull();
        capturedRequest.BodyParseErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task It_carries_json_parse_errors_without_setting_the_raw_body_string()
    {
        FrontendRequest? capturedRequest = null;
        var httpContext = CreateHttpContext("""{ "id":"value" "name":"School" }""");
        var apiService = CreateApiServiceForUpsert(request => capturedRequest = request);

        await AspNetCoreFrontend.Upsert(httpContext, apiService, "ed-fi/schools", AppSettings());

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Body.Should().BeNull();
        capturedRequest.ParsedBody.Should().BeNull();
        capturedRequest.BodyParseErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task It_carries_duplicate_property_paths_without_setting_the_raw_body_string()
    {
        FrontendRequest? capturedRequest = null;
        var httpContext = CreateHttpContext("""{ "schoolId":255901001, "schoolId":255901001 }""");
        var apiService = CreateApiServiceForUpsert(request => capturedRequest = request);

        await AspNetCoreFrontend.Upsert(httpContext, apiService, "ed-fi/schools", AppSettings());

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Body.Should().BeNull();
        capturedRequest.ParsedBody.Should().NotBeNull();
        capturedRequest.BodyParseErrorMessage.Should().BeNull();
        capturedRequest.DuplicatePropertyPath.Should().Be("$.schoolId");
    }

    [Test]
    public async Task It_keeps_form_url_encoded_bodies_out_of_the_json_parse_path()
    {
        FrontendRequest? capturedRequest = null;
        var httpContext = CreateHttpContext(
            "token=abc&token_type_hint=access_token",
            "application/x-www-form-urlencoded"
        );
        var apiService = CreateApiServiceForTokenInfo(request => capturedRequest = request);

        await AspNetCoreFrontend.GetTokenInfo(httpContext, apiService, AppSettings());

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Body.Should().BeNull();
        capturedRequest.ParsedBody.Should().BeNull();
        capturedRequest.BodyParseErrorMessage.Should().BeNull();
        capturedRequest.Form.Should().Contain("token", "abc");
    }

    [Test]
    public async Task It_keeps_token_info_json_bodies_as_raw_body_strings()
    {
        FrontendRequest? capturedRequest = null;
        const string body = """{ "Token":"abc" }""";
        var httpContext = CreateHttpContext(body, "application/json");
        var apiService = CreateApiServiceForTokenInfo(request => capturedRequest = request);

        await AspNetCoreFrontend.GetTokenInfo(httpContext, apiService, AppSettings());

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Body.Should().Be(body);
        capturedRequest.ParsedBody.Should().BeNull();
        capturedRequest.BodyParseErrorMessage.Should().BeNull();
        capturedRequest.DuplicatePropertyPath.Should().BeNull();
        capturedRequest.Form.Should().BeNull();
    }
}
