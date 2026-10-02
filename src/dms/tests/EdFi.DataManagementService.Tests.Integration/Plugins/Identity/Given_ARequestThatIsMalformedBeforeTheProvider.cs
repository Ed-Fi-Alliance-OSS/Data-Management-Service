// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// F28, host half: a request whose body or media type is structurally wrong is rejected before the
/// provider is invoked - duplicate property, malformed JSON, empty body, a wrong top-level shape for
/// create, find and search, a non-string or null find entry, a non-object search entry, and an
/// unsupported media type (including an Ed-Fi profile media type).
/// </summary>
/// <remarks>
/// The provider's own report of invocations is the evidence that it was not called, and the control is
/// a well-formed request of each operation that does reach it (the count then rises from 0 to 1).
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_ARequestThatIsMalformedBeforeTheProvider
{
    private sealed record Case(string Path, string Body, string ContentType, string Operation);

    private const string Json = "application/json";

    private static IReadOnlyDictionary<string, Case> Cases { get; } =
        new Dictionary<string, Case>
        {
            ["duplicate-property"] = new(
                "identities",
                """{"FirstName":"Jane","FirstName":"Jane"}""",
                Json,
                "create"
            ),
            ["malformed-create"] = new("identities", """{"FirstName":""", Json, "create"),
            ["empty-create"] = new("identities", "", Json, "create"),
            ["array-create"] = new("identities", "[]", Json, "create"),
            ["string-create"] = new("identities", "\"abc\"", Json, "create"),
            ["malformed-find"] = new("identities/find", """["a",""", Json, "find"),
            ["empty-find"] = new("identities/find", "", Json, "find"),
            ["object-find"] = new("identities/find", "{}", Json, "find"),
            ["number-find-entry"] = new("identities/find", """["a", 123]""", Json, "find"),
            ["null-find-entry"] = new("identities/find", """["a", null]""", Json, "find"),
            ["malformed-search"] = new("identities/search", """[{"LastSurname":""", Json, "search"),
            ["empty-search"] = new("identities/search", "", Json, "search"),
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
    private readonly Dictionary<string, IdentityHttpOutcome> _outcomes = [];
    private int _invocationsAfterRejections = -1;
    private readonly Dictionary<string, int> _invocationsByOperation = [];
    private readonly Dictionary<string, HttpStatusCode> _controls = [];
    private readonly Dictionary<string, int> _invocationsAfterControls = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync();

        foreach ((string name, Case request) in Cases)
        {
            _outcomes[name] = await _run.PostAsync(request.Path, request.Body, request.ContentType);
        }

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
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_run is not null)
        {
            await _run.DisposeAsync();
            _run = null;
        }
    }

    [TestCase("duplicate-property")]
    [TestCase("malformed-create")]
    [TestCase("empty-create")]
    [TestCase("array-create")]
    [TestCase("string-create")]
    [TestCase("malformed-find")]
    [TestCase("empty-find")]
    [TestCase("object-find")]
    [TestCase("number-find-entry")]
    [TestCase("null-find-entry")]
    [TestCase("malformed-search")]
    [TestCase("empty-search")]
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

    [Test]
    public void It_reports_the_duplicate_property_under_validation_errors()
    {
        // The host names the repeated property by its JSON path, under one fixed message.
        JsonObject validationErrors = _outcomes["duplicate-property"].Body!["validationErrors"]!.AsObject();

        validationErrors.Select(error => error.Key).Should().Equal("$.FirstName");
        validationErrors["$.FirstName"]!
            .AsArray()
            .Select(message => message!.GetValue<string>())
            .Should()
            .Equal("An item with the same key has already been added.");
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
}
