// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E.Keycloak;

// Offline tests (no Keycloak needed) for the characterization harness's disclosure rules (AC8):
// a failed prerequisite must surface as a fixed label, the HTTP status and a body category, never
// as request or response content, and the evidence log must refuse a row that would carry any
// secret or token the run has seen. Sentinels stand in for the values an echoing provider could
// leak; every assertion walks the exception's message, its string form and its inner chain.

public sealed class StubHandler(HttpStatusCode status, string body, string mediaType) : HttpMessageHandler
{
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
            RequestMessage = request,
        };
    }
}

public static class Caught
{
    public static async Task<Exception?> ExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    public static Exception? Exception(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}

public static class ExceptionText
{
    /// <summary>Everything a test runner or log sink could print for an exception.</summary>
    public static string Of(Exception exception)
    {
        StringBuilder text = new();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message);
            text.AppendLine(current.ToString());
            foreach (object? value in current.Data.Values)
            {
                text.AppendLine(value?.ToString());
            }
        }

        return text.ToString();
    }
}

public abstract class DiagnosticsFixture
{
    protected const string BodySentinel = "SECRET-BODY-SENTINEL-7f3a";
    protected const string SecretSentinel = "SECRET-CLIENT-SENTINEL-9c1d";
    protected const string TokenSentinel = "SECRET-TOKEN-SENTINEL-4b8e";

    protected SensitiveValueRegistry Sensitive { get; } = new();

    protected static KeycloakCharacterizationApi ApiOver(
        StubHandler handler,
        SensitiveValueRegistry sensitive
    ) => new("http://keycloak.invalid", "edfi", "master", sensitive, handler);
}

[TestFixture]
public class Given_an_admin_client_creation_that_fails_echoing_the_representation : DiagnosticsFixture
{
    private Exception? _thrown;
    private string? _sentRequestBody;

    [OneTimeSetUp]
    public async Task ArrangeAndAct()
    {
        using StubHandler handler = new(
            HttpStatusCode.BadRequest,
            $$"""{"errorMessage":"{{BodySentinel}} secret={{SecretSentinel}}"}""",
            "application/json"
        );
        using KeycloakCharacterizationApi api = ApiOver(handler, Sensitive);
        JsonObject representation = new() { ["clientId"] = "cs-char-test", ["secret"] = SecretSentinel };
        _thrown = await Caught.ExceptionAsync(() => api.CreateClientAsync(TokenSentinel, representation));
        _sentRequestBody = handler.LastRequestBody;
    }

    [Test]
    public void It_throws() => _thrown.Should().BeOfType<InvalidOperationException>();

    [Test]
    public void It_names_the_operation_status_and_body_category_only() =>
        _thrown!
            .Message.Should()
            .Be("Creating the characterization client answered HTTP 400 with a JSON object body.");

    [Test]
    public void It_discloses_no_response_content() =>
        ExceptionText.Of(_thrown!).Should().NotContain(BodySentinel);

    [Test]
    public void It_discloses_no_secret_from_the_request() =>
        ExceptionText.Of(_thrown!).Should().NotContain(SecretSentinel).And.NotContain(TokenSentinel);

    [Test]
    public void It_registered_the_secret_it_sent() =>
        Sensitive.ContainsAny($"x {SecretSentinel} y").Should().BeTrue();

    [Test]
    public void It_sent_the_representation_to_the_provider() =>
        _sentRequestBody.Should().Contain(SecretSentinel);
}

[TestFixture]
public class Given_a_token_request_that_fails_echoing_the_credentials : DiagnosticsFixture
{
    private Exception? _thrown;

    [OneTimeSetUp]
    public async Task ArrangeAndAct()
    {
        using StubHandler handler = new(
            HttpStatusCode.Unauthorized,
            $$"""{"error":"invalid_client","error_description":"{{BodySentinel}} {{SecretSentinel}}"}""",
            "application/json"
        );
        using KeycloakCharacterizationApi api = ApiOver(handler, Sensitive);
        _thrown = await Caught.ExceptionAsync(() =>
            api.RequestTokenAsync(
                new FormBody()
                    .Add("grant_type", "client_credentials")
                    .Add("client_id", "cs-char-test")
                    .Add("client_secret", SecretSentinel)
            )
        );
    }

    [Test]
    public void It_names_the_operation_status_and_body_category_only() =>
        _thrown!
            .Message.Should()
            .Be(
                "The token request answered HTTP 401 with a JSON object body; the characterization prerequisite failed."
            );

    [Test]
    public void It_discloses_neither_the_body_nor_the_secret() =>
        ExceptionText.Of(_thrown!).Should().NotContain(BodySentinel).And.NotContain(SecretSentinel);
}

[TestFixture]
public class Given_a_token_request_that_answers_a_non_json_body : DiagnosticsFixture
{
    private Exception? _thrown;

    [OneTimeSetUp]
    public async Task ArrangeAndAct()
    {
        using StubHandler handler = new(HttpStatusCode.OK, $"<html>{BodySentinel}</html>", "text/html");
        using KeycloakCharacterizationApi api = ApiOver(handler, Sensitive);
        _thrown = await Caught.ExceptionAsync(() =>
            api.RequestTokenAsync(new FormBody().Add("grant_type", "client_credentials"))
        );
    }

    [Test]
    public void It_reports_the_body_category_instead_of_the_body() =>
        _thrown!
            .Message.Should()
            .Be("The token request answered HTTP 200 with a non-JSON body instead of a JSON object.");

    [Test]
    public void It_discloses_no_response_content() =>
        ExceptionText.Of(_thrown!).Should().NotContain(BodySentinel);
}

[TestFixture]
public class Given_an_introspection_response_without_an_active_member : DiagnosticsFixture
{
    private Exception? _thrown;

    [OneTimeSetUp]
    public async Task ArrangeAndAct()
    {
        using StubHandler handler = new(
            HttpStatusCode.OK,
            $$"""{"unexpected":"{{BodySentinel}}"}""",
            "application/json"
        );
        using KeycloakCharacterizationApi api = ApiOver(handler, Sensitive);
        _thrown = await Caught.ExceptionAsync(() =>
            api.IntrospectAsync(
                new ClientCredentials("observer", SecretSentinel),
                TokenSentinel,
                tokenTypeHint: null
            )
        );
    }

    [Test]
    public void It_uses_a_fixed_message() =>
        _thrown!
            .Message.Should()
            .Be(
                "The introspection response has no boolean 'active' member; the characterization prerequisite failed."
            );

    [Test]
    public void It_discloses_neither_the_body_nor_the_credentials_nor_the_token() =>
        ExceptionText
            .Of(_thrown!)
            .Should()
            .NotContain(BodySentinel)
            .And.NotContain(SecretSentinel)
            .And.NotContain(TokenSentinel);

    [Test]
    public void It_registered_the_basic_parameter_it_sent() =>
        Sensitive
            .ContainsAny(new ClientCredentials("observer", SecretSentinel).ToBasicHeader().Parameter!)
            .Should()
            .BeTrue();
}

[TestFixture]
public class Given_an_evidence_row_that_would_carry_a_run_token : DiagnosticsFixture
{
    private EvidenceLog? _log;
    private Exception? _thrown;
    private Exception? _cleanRowFailure;

    [OneTimeSetUp]
    public void ArrangeAndAct()
    {
        Sensitive.Register(TokenSentinel);
        _log = new EvidenceLog(Sensitive);
        HttpOutcome outcome = new(
            400,
            "invalid_request",
            $"echo {TokenSentinel}",
            60,
            BodyKind.JsonObject,
            "application/json",
            null
        );
        EvidenceRow leaking = new(
            "K-TEST",
            "a provider that echoes the token",
            RowPrediction.ErrorBody(400, "invalid_request", "x", Predicted<bool>.NotApplicable),
            new RowObservation(outcome, null, null, "notes")
        );
        _thrown = Caught.Exception(() => _log.Record(leaking));

        EvidenceRow clean = leaking with
        {
            Id = "K-CLEAN",
            Observation = new RowObservation(
                outcome with
                {
                    ErrorDescription = "Unmatching clients",
                },
                null,
                null,
                "notes"
            ),
        };
        _cleanRowFailure = Caught.Exception(() => _log.Record(clean));
    }

    [Test]
    public void It_refuses_the_row() => _thrown.Should().BeOfType<InvalidOperationException>();

    [Test]
    public void It_names_the_row_without_the_token() =>
        _thrown!
            .Message.Should()
            .Be(
                "Evidence row K-TEST would contain a secret or token issued during this run; the row was not recorded."
            );

    [Test]
    public void It_keeps_the_token_out_of_the_rendered_table() =>
        _log!
            .RenderMarkdown("test", "external", "run")
            .Should()
            .NotContain(TokenSentinel)
            .And.NotContain("K-TEST");

    [Test]
    public void It_still_records_a_clean_row()
    {
        _cleanRowFailure.Should().BeNull();
        _log!.RenderMarkdown("test", "external", "run").Should().Contain("| K-CLEAN |");
    }
}

[TestFixture]
public class Given_the_sensitive_value_registry : DiagnosticsFixture
{
    [OneTimeSetUp]
    public void ArrangeAndAct()
    {
        Sensitive.Register(SecretSentinel);
        Sensitive.Register(null);
        Sensitive.Register("");
        Sensitive.Register("short");
    }

    [Test]
    public void It_finds_a_registered_value_inside_text() =>
        Sensitive.ContainsAny($"| K-01 | {SecretSentinel} |").Should().BeTrue();

    [Test]
    public void It_matches_ordinally_and_not_by_case() =>
        Sensitive.ContainsAny(SecretSentinel.ToLowerInvariant()).Should().BeFalse();

    [Test]
    public void It_ignores_null_empty_and_short_values() =>
        Sensitive.ContainsAny("short text here").Should().BeFalse();
}
