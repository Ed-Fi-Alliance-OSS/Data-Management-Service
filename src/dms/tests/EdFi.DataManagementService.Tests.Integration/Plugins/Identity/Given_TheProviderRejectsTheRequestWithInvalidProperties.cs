// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// Each shape of provider <c>InvalidProperties</c> - a create field error, a search item error,
/// a path-less error (blank, and null) and two messages at one key - yields a <c>400</c> whose body
/// equals the example of the same name pinned in the served OpenAPI document, once property order is
/// ignored and the correlation id is normalized.
/// </summary>
/// <remarks>
/// Each shape is raised from the operation its pinned example describes (the search item error from
/// the third item of a search batch). The negative control alters one message in a copy of a pinned
/// example and shows the comparison then fails, and the correlation id of the live body is shown to
/// differ from the pinned value so the normalization is doing work.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheProviderRejectsTheRequestWithInvalidProperties
{
    private const string PinnedCorrelationId = "0HNOOQ2BHB6VR";

    private IdentityHttpRun? _run;
    private IdentityServedOpenApi? _openApi;
    private readonly Dictionary<string, IdentityHttpOutcome> _outcomes = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync();
        _openApi = await IdentityServedOpenApi.FetchAsync(_run.Client);

        _outcomes["createFieldError"] = await _run.PostAsync(
            "identities",
            """{ "~FixtureReturn": "invalid-createFieldError" }"""
        );
        _outcomes["searchItemError"] = await _run.PostAsync(
            "identities/search",
            """[{ "LastSurname": "a" }, { "LastSurname": "b" }, { "~FixtureReturn": "invalid-searchItemError" }]"""
        );
        _outcomes["pathlessError"] = await _run.PostAsync(
            "identities/find",
            """["~fixture:return:invalid-pathlessError"]"""
        );
        _outcomes["pathlessNullError"] = await _run.PostAsync(
            "identities/find",
            """["~fixture:return:invalid-pathlessNullError"]"""
        );
        _outcomes["twoMessagesOneKey"] = await _run.PostAsync(
            "identities",
            """{ "~FixtureReturn": "invalid-twoMessagesOneKey" }"""
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
    }

    private JsonNode Pinned(string exampleName) =>
        _openApi!.Example("/identities", "post", "400", exampleName, "application/problem+json");

    [TestCase("createFieldError")]
    [TestCase("searchItemError")]
    [TestCase("pathlessError")]
    [TestCase("pathlessNullError")]
    [TestCase("twoMessagesOneKey")]
    public void It_answers_400(string shape)
    {
        _outcomes[shape].Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestCase("createFieldError", "createFieldError")]
    [TestCase("searchItemError", "searchItemError")]
    [TestCase("pathlessError", "pathlessError")]
    [TestCase("pathlessNullError", "pathlessError")]
    [TestCase("twoMessagesOneKey", "twoMessagesOneKey")]
    public void It_equals_the_pinned_example_after_canonical_comparison(string shape, string exampleName)
    {
        JsonNode live = IdentityServedOpenApi.NormalizeCorrelationId(_outcomes[shape].Body!);
        JsonNode pinned = IdentityServedOpenApi.NormalizeCorrelationId(Pinned(exampleName));

        IdentityServedOpenApi
            .AreCanonicallyEqual(live, pinned)
            .Should()
            .BeTrue(
                $"live: {IdentityServedOpenApi.Canonicalize(live)} pinned: {IdentityServedOpenApi.Canonicalize(pinned)}"
            );
    }

    [Test]
    public void It_pins_the_correlation_id_value_it_normalizes_away()
    {
        Pinned("createFieldError")["correlationId"]!.GetValue<string>().Should().Be(PinnedCorrelationId);
    }

    [Test]
    public void It_carries_a_live_correlation_id_that_is_not_the_pinned_one()
    {
        _outcomes["createFieldError"].Body!["correlationId"]!
            .GetValue<string>()
            .Should()
            .NotBe(PinnedCorrelationId);
    }

    [Test]
    public void It_would_fail_the_comparison_if_one_message_differed()
    {
        JsonNode altered = Pinned("twoMessagesOneKey").DeepClone();
        altered["validationErrors"]!["$.FirstName"]![1] = "First name must not exceed 76 characters.";

        IdentityServedOpenApi
            .AreCanonicallyEqual(
                IdentityServedOpenApi.NormalizeCorrelationId(_outcomes["twoMessagesOneKey"].Body!),
                IdentityServedOpenApi.NormalizeCorrelationId(altered)
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_would_fail_the_comparison_against_the_pinned_example_of_another_shape()
    {
        IdentityServedOpenApi
            .AreCanonicallyEqual(
                IdentityServedOpenApi.NormalizeCorrelationId(_outcomes["createFieldError"].Body!),
                IdentityServedOpenApi.NormalizeCorrelationId(Pinned("pathlessError"))
            )
            .Should()
            .BeFalse();
    }
}
