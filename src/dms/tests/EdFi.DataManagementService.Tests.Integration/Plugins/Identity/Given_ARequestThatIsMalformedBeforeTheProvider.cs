// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// A request whose body or media type is structurally wrong is rejected before the
/// provider is invoked - duplicate property, malformed JSON, empty or whitespace-only body, a wrong
/// top-level shape for create, find and search, a non-string or null find entry, a non-object search
/// entry, and an unsupported media type (including an Ed-Fi profile media type). A malformed, empty,
/// whitespace-only or duplicate-property body is answered as application/problem+json, the media type
/// the served document declares for the operation's 400, with the body shape unchanged.
/// </summary>
/// <remarks>
/// The provider's own report of invocations is the evidence that it was not called, and the control is
/// a well-formed request of each operation that does reach it (the count then rises from 0 to 1). The
/// ordering controls show an anonymous client is still refused before its body is read, and a host
/// that does not advertise search still refuses the operation before its body is read.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_ARequestThatIsMalformedBeforeTheProvider
{
    private sealed record Case(string Path, string Body, string ContentType, string Operation);

    private const string Json = "application/json";
    private const string ProblemJson = "application/problem+json";
    private const string WhitespaceBody = " \r\n\t ";

    private static IReadOnlyDictionary<string, Case> Cases { get; } =
        new Dictionary<string, Case>
        {
            ["duplicate-property"] = new(
                "identities",
                """{"FirstName":"Jane","FirstName":"Jane"}""",
                Json,
                "create"
            ),
            ["duplicate-search-indexed"] = new(
                "identities/search",
                """[{"LastSurname":"a"},{"BirthLocation":{"City":"x","City":"y"}}]""",
                Json,
                "search"
            ),
            ["malformed-create"] = new("identities", """{"FirstName":""", Json, "create"),
            ["empty-create"] = new("identities", "", Json, "create"),
            ["whitespace-create"] = new("identities", WhitespaceBody, Json, "create"),
            ["array-create"] = new("identities", "[]", Json, "create"),
            ["string-create"] = new("identities", "\"abc\"", Json, "create"),
            ["duplicate-find"] = new("identities/find", """[{"a":1,"a":1}]""", Json, "find"),
            ["malformed-find"] = new("identities/find", """["a",""", Json, "find"),
            ["empty-find"] = new("identities/find", "", Json, "find"),
            ["whitespace-find"] = new("identities/find", WhitespaceBody, Json, "find"),
            ["object-find"] = new("identities/find", "{}", Json, "find"),
            ["number-find-entry"] = new("identities/find", """["a", 123]""", Json, "find"),
            ["null-find-entry"] = new("identities/find", """["a", null]""", Json, "find"),
            ["duplicate-search"] = new(
                "identities/search",
                """[{"LastSurname":"x","LastSurname":"y"}]""",
                Json,
                "search"
            ),
            ["malformed-search"] = new("identities/search", """[{"LastSurname":""", Json, "search"),
            ["empty-search"] = new("identities/search", "", Json, "search"),
            ["whitespace-search"] = new("identities/search", WhitespaceBody, Json, "search"),
            ["object-search"] = new("identities/search", "{}", Json, "search"),
            ["string-search-entry"] = new(
                "identities/search",
                """[{"LastSurname":"x"}, "x"]""",
                Json,
                "search"
            ),
            ["null-search-entry"] = new(
                "identities/search",
                """[{"LastSurname":"x"}, null]""",
                Json,
                "search"
            ),
            ["xml-create"] = new("identities", "{}", "application/xml", "create"),
            ["profile-create"] = new("identities", "{}", "application/vnd.ed-fi.student.v1+json", "create"),
            ["xml-find"] = new("identities/find", "[]", "application/xml", "find"),
            ["profile-find"] = new("identities/find", "[]", "application/vnd.ed-fi.student.v1+json", "find"),
            ["xml-search"] = new("identities/search", "[]", "application/xml", "search"),
            ["profile-search"] = new(
                "identities/search",
                "[]",
                "application/vnd.ed-fi.student.v1+json",
                "search"
            ),
        };

    private IdentityHttpRun? _run;
    private IdentityHttpRun? _withoutSearch;
    private IdentityServedOpenApi? _openApi;
    private readonly Dictionary<string, IdentityHttpOutcome> _outcomes = [];
    private int _invocationsAfterRejections = -1;
    private readonly Dictionary<string, int> _invocationsByOperation = [];
    private readonly Dictionary<string, HttpStatusCode> _controls = [];
    private readonly Dictionary<string, int> _invocationsAfterControls = [];
    private IdentityHttpOutcome? _anonymousMalformedCreate;
    private IdentityHttpOutcome? _unadvertisedMalformedSearch;

    private JsonNode DuplicateSearchBody => _outcomes["duplicate-search-indexed"].Body!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync(
            settings: new Dictionary<string, string>
            {
                ["AppSettings:CorrelationIdHeader"] = "X-Correlation-ID",
            }
        );
        _run.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "dms-1594-http");
        _openApi = await IdentityServedOpenApi.FetchAsync(_run.Client);

        foreach ((string name, Case request) in Cases)
        {
            _outcomes[name] = await _run.PostAsync(request.Path, request.Body, request.ContentType);
        }

        using HttpClient unauthorized = _run.Host.CreateClient(IdentityTestClients.UnknownToken);
        _outcomes["unauthorized-duplicate"] = await _run.PostAsync(
            "identities/search",
            Cases["duplicate-search-indexed"].Body,
            client: unauthorized
        );

        _invocationsAfterRejections = _run.Stub.InvocationCount();

        foreach (string operation in new[] { "create", "find", "search" })
        {
            _invocationsByOperation[operation] = _run.Stub.InvocationCount(operation);
        }

        _controls["create"] = (await _run.PostAsync("identities", """{"LastSurname":"Rivera"}""")).Status;
        _controls["find"] = (await _run.PostAsync("identities/find", """["nobody"]""")).Status;
        _controls["search"] = (
            await _run.PostAsync("identities/search", """[{"LastSurname":"Rivera"}]""")
        ).Status;

        foreach (string operation in new[] { "create", "find", "search" })
        {
            _invocationsAfterControls[operation] = _run.Stub.InvocationCount(operation);
        }

        using (HttpClient anonymous = _run.Host.CreateClient())
        {
            _anonymousMalformedCreate = await _run.PostAsync(
                "identities",
                Cases["malformed-create"].Body,
                client: anonymous
            );
        }

        _withoutSearch = await IdentityHttpRun.StartAsync(
            fixtureSettings: new Dictionary<string, string>
            {
                ["IdentityFixture:Capabilities"] = "Create,GetById,Find,Results",
            }
        );
        _unadvertisedMalformedSearch = await _withoutSearch.PostAsync(
            "identities/search",
            Cases["malformed-search"].Body
        );
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_run is not null)
        {
            await _run.DisposeAsync();
            _run = null;
        }

        if (_withoutSearch is not null)
        {
            await _withoutSearch.DisposeAsync();
            _withoutSearch = null;
        }
    }

    /// <summary>The single <c>Content-Type</c> a rejection carried, parsed.</summary>
    private MediaTypeHeaderValue ContentTypeOf(string name) =>
        MediaTypeHeaderValue.Parse(
            _outcomes[name].HeaderValues("Content-Type").Should().ContainSingle().Subject
        );

    /// <summary>The media types the served document declares for the case's operation and status.</summary>
    private IEnumerable<string> DeclaredMediaTypes(string name, string status) =>
        _openApi!.Response($"/{Cases[name].Path}", "post", status)["content"]!
            .AsObject()
            .Select(pair => pair.Key);

    [TestCase("duplicate-property")]
    [TestCase("duplicate-search-indexed")]
    [TestCase("malformed-create")]
    [TestCase("empty-create")]
    [TestCase("whitespace-create")]
    [TestCase("array-create")]
    [TestCase("string-create")]
    [TestCase("duplicate-find")]
    [TestCase("malformed-find")]
    [TestCase("empty-find")]
    [TestCase("whitespace-find")]
    [TestCase("object-find")]
    [TestCase("number-find-entry")]
    [TestCase("null-find-entry")]
    [TestCase("duplicate-search")]
    [TestCase("malformed-search")]
    [TestCase("empty-search")]
    [TestCase("whitespace-search")]
    [TestCase("object-search")]
    [TestCase("string-search-entry")]
    [TestCase("null-search-entry")]
    public void It_rejects_a_structurally_wrong_body_with_400(string name)
    {
        _outcomes[name].Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestCase("xml-create")]
    [TestCase("profile-create")]
    [TestCase("xml-find")]
    [TestCase("profile-find")]
    [TestCase("xml-search")]
    [TestCase("profile-search")]
    public void It_rejects_an_unsupported_media_type_with_415(string name)
    {
        _outcomes[name].Status.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [TestCase("duplicate-property")]
    [TestCase("malformed-create")]
    [TestCase("empty-create")]
    [TestCase("whitespace-create")]
    [TestCase("duplicate-find")]
    [TestCase("malformed-find")]
    [TestCase("empty-find")]
    [TestCase("whitespace-find")]
    [TestCase("duplicate-search")]
    [TestCase("malformed-search")]
    [TestCase("empty-search")]
    [TestCase("whitespace-search")]
    public void It_answers_a_body_validation_rejection_as_problem_json(string name)
    {
        // The status is asserted first so a case that did not reach the path it names cannot pass.
        _outcomes[name].Status.Should().Be(HttpStatusCode.BadRequest);

        MediaTypeHeaderValue contentType = ContentTypeOf(name);

        contentType.MediaType.Should().Be(ProblemJson);
        contentType.CharSet.Should().Be("utf-8");
        DeclaredMediaTypes(name, "400").Should().Contain(contentType.MediaType);
    }

    [TestCase("xml-create")]
    [TestCase("profile-create")]
    [TestCase("xml-find")]
    [TestCase("profile-find")]
    [TestCase("xml-search")]
    [TestCase("profile-search")]
    public void It_answers_an_unsupported_media_type_as_problem_json(string name)
    {
        _outcomes[name].Status.Should().Be(HttpStatusCode.UnsupportedMediaType);

        MediaTypeHeaderValue contentType = ContentTypeOf(name);

        contentType.MediaType.Should().Be(ProblemJson);
        contentType.CharSet.Should().Be("utf-8");
        DeclaredMediaTypes(name, "415").Should().Contain(contentType.MediaType);
    }

    [TestCase("duplicate-property", "urn:ed-fi:api:bad-request:data-validation-failed")]
    [TestCase("malformed-create", "urn:ed-fi:api:bad-request:data-validation-failed")]
    [TestCase("empty-create", "urn:ed-fi:api:bad-request")]
    [TestCase("whitespace-create", "urn:ed-fi:api:bad-request")]
    [TestCase("duplicate-find", "urn:ed-fi:api:bad-request:data-validation-failed")]
    [TestCase("malformed-find", "urn:ed-fi:api:bad-request:data-validation-failed")]
    [TestCase("empty-find", "urn:ed-fi:api:bad-request")]
    [TestCase("whitespace-find", "urn:ed-fi:api:bad-request")]
    [TestCase("duplicate-search", "urn:ed-fi:api:bad-request:data-validation-failed")]
    [TestCase("malformed-search", "urn:ed-fi:api:bad-request:data-validation-failed")]
    [TestCase("empty-search", "urn:ed-fi:api:bad-request")]
    [TestCase("whitespace-search", "urn:ed-fi:api:bad-request")]
    public void It_keeps_the_problem_body(string name, string type)
    {
        bool validationArm = type.EndsWith(":data-validation-failed", StringComparison.Ordinal);
        JsonNode body = _outcomes[name].Body!;

        body["type"]!.GetValue<string>().Should().Be(type);
        body["title"]!
            .GetValue<string>()
            .Should()
            .Be(validationArm ? "Data Validation Failed" : "Bad Request");
        body["detail"]!
            .GetValue<string>()
            .Should()
            .Be(
                validationArm
                    ? "Data validation failed. See 'validationErrors' for details."
                    : "The request could not be processed. See 'errors' for details."
            );
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        _openApi!.Conforms($"/{Cases[name].Path}", "post", "400", body, ProblemJson).Should().BeTrue();
    }

    [TestCase("malformed-create")]
    [TestCase("malformed-find")]
    [TestCase("malformed-search")]
    public void It_reports_a_malformed_body_under_the_root_path(string name)
    {
        // The message is System.Text.Json's, so only its presence is pinned.
        JsonNode body = _outcomes[name].Body!;
        JsonObject validationErrors = body["validationErrors"]!.AsObject();

        validationErrors.Select(error => error.Key).Should().Equal("$.");
        validationErrors["$."]!
            .AsArray()
            .Should()
            .ContainSingle()
            .Which!.GetValue<string>()
            .Should()
            .NotBeNullOrWhiteSpace();
        body["errors"]!.AsArray().Should().BeEmpty();
    }

    [TestCase("empty-create")]
    [TestCase("whitespace-create")]
    [TestCase("empty-find")]
    [TestCase("whitespace-find")]
    [TestCase("empty-search")]
    [TestCase("whitespace-search")]
    public void It_reports_an_empty_body_under_errors(string name)
    {
        JsonNode body = _outcomes[name].Body!;

        body["errors"]!
            .AsArray()
            .Select(error => error!.GetValue<string>())
            .Should()
            .Equal("A non-empty request body is required.");
        body["validationErrors"]!.AsObject().Should().BeEmpty();
    }

    [TestCase("duplicate-property", "$.FirstName")]
    [TestCase("duplicate-find", "$[0].a")]
    [TestCase("duplicate-search", "$[0].LastSurname")]
    public void It_reports_the_duplicate_property_under_validation_errors(string name, string path)
    {
        // The host names the repeated property by its JSON path, under one fixed message.
        JsonNode body = _outcomes[name].Body!;
        JsonObject validationErrors = body["validationErrors"]!.AsObject();

        validationErrors.Select(error => error.Key).Should().Equal(path);
        validationErrors[path]!
            .AsArray()
            .Select(message => message!.GetValue<string>())
            .Should()
            .Equal("An item with the same key has already been added.");
        body["errors"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public void It_reports_the_indexed_search_problem_type()
    {
        DuplicateSearchBody["type"]!
            .GetValue<string>()
            .Should()
            .Be("urn:ed-fi:api:bad-request:data-validation-failed");
    }

    [Test]
    public void It_reports_the_indexed_search_problem_title()
    {
        DuplicateSearchBody["title"]!.GetValue<string>().Should().Be("Data Validation Failed");
    }

    [Test]
    public void It_reports_the_indexed_search_problem_status()
    {
        DuplicateSearchBody["status"]!.GetValue<int>().Should().Be(400);
    }

    [Test]
    public void It_reports_the_indexed_search_problem_detail()
    {
        DuplicateSearchBody["detail"]!
            .GetValue<string>()
            .Should()
            .Be("Data validation failed. See 'validationErrors' for details.");
    }

    [Test]
    public void It_reports_the_indexed_search_problem_correlation_id()
    {
        DuplicateSearchBody["correlationId"]!.GetValue<string>().Should().Be("dms-1594-http");
    }

    [Test]
    public void It_reports_no_general_errors_for_the_indexed_search_problem()
    {
        DuplicateSearchBody["errors"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public void It_reports_the_indexed_search_duplicate_path()
    {
        DuplicateSearchBody["validationErrors"]!
            .AsObject()
            .Select(error => error.Key)
            .Should()
            .Equal("$[1].BirthLocation.City");
    }

    [Test]
    public void It_reports_the_duplicate_message_for_the_indexed_search_path()
    {
        DuplicateSearchBody["validationErrors"]!["$[1].BirthLocation.City"]!
            .AsArray()
            .Select(message => message!.GetValue<string>())
            .Should()
            .Equal("An item with the same key has already been added.");
    }

    [Test]
    public void It_keeps_the_indexed_search_problem_out_of_caches()
    {
        _outcomes["duplicate-search-indexed"].HeaderValues("Cache-Control").Should().Equal("no-store");
    }

    [Test]
    public void It_keeps_unauthorized_before_duplicate_reporting()
    {
        _outcomes["unauthorized-duplicate"].Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public void It_does_not_report_the_duplicate_to_an_unauthorized_client()
    {
        _outcomes["unauthorized-duplicate"].Body?["validationErrors"].Should().BeNull();
    }

    [Test]
    public void It_never_invoked_the_provider_for_any_rejected_request()
    {
        _invocationsAfterRejections.Should().Be(0);
    }

    [TestCase("create")]
    [TestCase("find")]
    [TestCase("search")]
    public void It_never_invoked_the_operation_for_its_rejected_requests(string operation)
    {
        _invocationsByOperation[operation].Should().Be(0);
    }

    [TestCase("create")]
    [TestCase("find")]
    [TestCase("search")]
    public void It_accepts_a_well_formed_request_of_the_operation(string operation)
    {
        _controls[operation].Should().Be(HttpStatusCode.OK);
    }

    [TestCase("create")]
    [TestCase("find")]
    [TestCase("search")]
    public void It_invokes_the_provider_once_for_the_well_formed_request(string operation)
    {
        _invocationsAfterControls[operation].Should().Be(1);
    }

    [Test]
    public void It_refuses_an_anonymous_client_before_reading_its_body()
    {
        _anonymousMalformedCreate!.Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public void It_refuses_an_unadvertised_operation_before_reading_its_body()
    {
        _unadvertisedMalformedSearch!.Status.Should().Be(HttpStatusCode.NotFound);
        _unadvertisedMalformedSearch
            .ProblemType.Should()
            .Be("urn:ed-fi:api:identities:operation-not-supported");
    }
}
