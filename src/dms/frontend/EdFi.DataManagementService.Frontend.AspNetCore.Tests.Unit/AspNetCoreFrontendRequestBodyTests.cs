// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_AspNetCoreFrontend_Request_Body_Extraction
{
    private static async Task<FrontendRequest> ExtractRequest(string json, string operation)
    {
        FrontendRequest captured = new("", null, null, [], [], new TraceId(""), []);
        IApiService api = A.Fake<IApiService>();
        A.CallTo(() => api.Upsert(A<FrontendRequest>._, A<CancellationToken>._))
            .Invokes(call => captured = call.GetArgument<FrontendRequest>(0)!)
            .Returns(Task.FromResult<IFrontendResponse>(new FrontendResponse(200, null, [])));
        A.CallTo(() => api.UpdateById(A<FrontendRequest>._, A<CancellationToken>._))
            .Invokes(call => captured = call.GetArgument<FrontendRequest>(0)!)
            .Returns(Task.FromResult<IFrontendResponse>(new FrontendResponse(200, null, [])));
        A.CallTo(() => api.IdentitySearch(A<FrontendRequest>._, A<CancellationToken>._))
            .Invokes(call => captured = call.GetArgument<FrontendRequest>(0)!)
            .Returns(Task.FromResult<IFrontendResponse>(new FrontendResponse(200, null, [])));
        DefaultHttpContext context = CreateHttpContext(json, "application/json");
        switch (operation)
        {
            case "upsert":
                await AspNetCoreFrontend.Upsert(context, api, "ed-fi/schools", AppSettings());
                break;
            case "update":
                await AspNetCoreFrontend.UpdateById(context, api, "ed-fi/schools/id", AppSettings());
                break;
            default:
                context.Request.Path = "/identity/v2/identities/search";
                await AspNetCoreFrontend.IdentitySearch(
                    context,
                    api,
                    AppSettings(),
                    Options.Create(new KestrelServerOptions())
                );
                break;
        }
        return captured;
    }

    [TestFixtureSource(nameof(IndexedDuplicateCases))]
    public class Given_An_Indexed_Duplicate(string json, string expectedPath, string operation)
    {
        private static IEnumerable<object[]> IndexedDuplicateCases()
        {
            (string Json, string Path)[] examples =
            [
                (
                    """{"studentUniqueId":"1","addresses":[{"city":"a","streetNumberName":"b"},{"city":"x","city":"y"}]}""",
                    "$.addresses[1].city"
                ),
                (
                    """{"studentUniqueId":"1","addresses":[{"city":"a"}],"electronicMails":[{"x":"1","x":"2"}]}""",
                    "$.electronicMails[0].x"
                ),
                (
                    """[{"LastSurname":"a"},{"BirthLocation":{"City":"x","City":"y"}}]""",
                    "$[1].BirthLocation.City"
                ),
            ];
            foreach (var (json, path) in examples)
            {
                foreach (string operation in json.StartsWith('[') ? new[] { "search" } : ["upsert", "update"])
                {
                    yield return [json, path, operation];
                }
            }
            (string Sequence, string Suffix)[] matrix =
            [
                ("""{"x":1,"x":2}""", "[0].x"),
                ("""{"before":1,"x":1,"x":2}""", "[0].x"),
                ("""{"a":1,"b":2},{"before":0,"x":1,"x":2}""", "[1].x"),
                ("""{},{"before":1,"groups":[{"a":1},{"before":0,"x":1,"x":2}]}""", "[1].groups[1].x"),
            ];
            foreach (var (sequence, suffix) in matrix)
            {
                yield return ["{\"items\":[" + sequence + "]}", "$.items" + suffix, "upsert"];
                yield return ["{\"items\":[" + sequence + "]}", "$.items" + suffix, "update"];
                yield return ["[" + sequence + "]", "$" + suffix, "search"];
            }
        }

        private FrontendRequest _captured = null!;

        [SetUp]
        public async Task Setup() => _captured = await ExtractRequest(json, operation);

        [Test]
        public void It_carries_the_exact_indexed_duplicate_path() =>
            _captured.DuplicatePropertyPath.Should().Be(expectedPath);

        [Test]
        public void It_does_not_set_the_raw_body() => _captured.Body.Should().BeNull();

        [Test]
        public void It_does_not_report_a_parse_error() => _captured.BodyParseErrorMessage.Should().BeNull();
    }

    [TestFixture("upsert")]
    [TestFixture("update")]
    [TestFixture("search")]
    public class Given_Malformed_Json_After_A_Duplicate(string operation)
    {
        private FrontendRequest _captured = null!;

        [SetUp]
        public async Task Setup() => _captured = await ExtractRequest("""{"x":1,"x":2,}""", operation);

        [Test]
        public void It_preserves_malformed_after_duplicate_reporting() =>
            _captured.BodyParseErrorMessage.Should().NotBeNullOrEmpty();

        [Test]
        public void It_does_not_report_a_duplicate() => _captured.DuplicatePropertyPath.Should().BeNull();
    }

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
        A.CallTo(() => apiService.Upsert(A<FrontendRequest>._, A<CancellationToken>._))
            .Invokes(call => captureRequest(call.GetArgument<FrontendRequest>(0)!))
            .Returns(Task.FromResult<IFrontendResponse>(new FrontendResponse(200, null, [])));

        return apiService;
    }

    private static IApiService CreateApiServiceForTokenInfo(Action<FrontendRequest> captureRequest)
    {
        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.GetTokenInfo(A<FrontendRequest>._, A<CancellationToken>._))
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

    [Test]
    public async Task It_passes_the_http_context_request_aborted_token_to_upsert()
    {
        CancellationToken? capturedCancellationToken = null;
        var httpContext = CreateHttpContext("""{ "id":"value", "name":"School" }""");
        using var cancellationTokenSource = new CancellationTokenSource();
        httpContext.RequestAborted = cancellationTokenSource.Token;
        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.Upsert(A<FrontendRequest>._, A<CancellationToken>._))
            .Invokes(call => capturedCancellationToken = call.GetArgument<CancellationToken>(1))
            .Returns(Task.FromResult<IFrontendResponse>(new FrontendResponse(200, null, [])));

        await AspNetCoreFrontend.Upsert(httpContext, apiService, "ed-fi/schools", AppSettings());

        capturedCancellationToken.Should().Be(cancellationTokenSource.Token);
    }

    [Test]
    public async Task It_passes_the_http_context_request_aborted_token_to_update_by_id()
    {
        CancellationToken? capturedCancellationToken = null;
        var httpContext = CreateHttpContext("""{ "id":"value", "name":"School" }""");
        using var cancellationTokenSource = new CancellationTokenSource();
        httpContext.RequestAborted = cancellationTokenSource.Token;
        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.UpdateById(A<FrontendRequest>._, A<CancellationToken>._))
            .Invokes(call => capturedCancellationToken = call.GetArgument<CancellationToken>(1))
            .Returns(Task.FromResult<IFrontendResponse>(new FrontendResponse(200, null, [])));

        await AspNetCoreFrontend.UpdateById(httpContext, apiService, "ed-fi/schools", AppSettings());

        capturedCancellationToken.Should().Be(cancellationTokenSource.Token);
    }

    [Test]
    public async Task It_passes_the_http_context_request_aborted_token_to_delete_by_id()
    {
        CancellationToken? capturedCancellationToken = null;
        var httpContext = CreateHttpContext(string.Empty);
        using var cancellationTokenSource = new CancellationTokenSource();
        httpContext.RequestAborted = cancellationTokenSource.Token;
        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.DeleteById(A<FrontendRequest>._, A<CancellationToken>._))
            .Invokes(call => capturedCancellationToken = call.GetArgument<CancellationToken>(1))
            .Returns(Task.FromResult<IFrontendResponse>(new FrontendResponse(204, null, [])));

        await AspNetCoreFrontend.DeleteById(httpContext, apiService, "ed-fi/schools", AppSettings());

        capturedCancellationToken.Should().Be(cancellationTokenSource.Token);
    }

    [Test]
    public async Task It_passes_the_http_context_request_aborted_token_to_get_token_info()
    {
        CancellationToken? capturedCancellationToken = null;
        var httpContext = CreateHttpContext("""{ "Token":"abc" }""", "application/json");
        using var cancellationTokenSource = new CancellationTokenSource();
        httpContext.RequestAborted = cancellationTokenSource.Token;
        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.GetTokenInfo(A<FrontendRequest>._, A<CancellationToken>._))
            .Invokes(call => capturedCancellationToken = call.GetArgument<CancellationToken>(1))
            .Returns(Task.FromResult<IFrontendResponse>(new FrontendResponse(200, null, [])));

        await AspNetCoreFrontend.GetTokenInfo(httpContext, apiService, AppSettings());

        capturedCancellationToken.Should().Be(cancellationTokenSource.Token);
    }
}
