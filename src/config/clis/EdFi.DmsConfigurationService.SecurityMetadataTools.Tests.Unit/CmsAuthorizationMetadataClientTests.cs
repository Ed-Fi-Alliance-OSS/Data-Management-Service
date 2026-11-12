// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

internal sealed class CmsAuthorizationMetadataClientFixture : IDisposable
{
    internal const string Token = "distinctive-secret-bearer-token-DO-NOT-PRINT";
    internal string VariableName { get; } = $"DMS_TEST_TOKEN_{Guid.NewGuid():N}";
    internal RecordingHandler Handler { get; } = new();
    internal HttpClient HttpClient { get; }
    internal CmsAuthorizationMetadataClient Client { get; }

    internal CmsAuthorizationMetadataClientFixture()
    {
        Environment.SetEnvironmentVariable(VariableName, Token);
        HttpClient = new(Handler);
        Client = new(HttpClient);
    }

    public void Dispose()
    {
        HttpClient.Dispose();
        Environment.SetEnvironmentVariable(VariableName, null);
    }

    internal sealed class RecordingHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal HttpMethod Method { get; private set; } = HttpMethod.Post;
        internal string Url { get; private set; } = "";
        internal Dictionary<string, string[]> Headers { get; private set; } = [];
        internal bool HasContent { get; private set; }
        internal HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        internal string Body { get; set; } = AuthorizationMetadataValidatorFixtures.ValidJson;
        internal Func<HttpResponseMessage> Respond { get; set; }

        internal RecordingHandler() =>
            Respond = () =>
                new(StatusCode)
                {
                    Content = new StringContent(Body, Encoding.UTF8, "application/json"),
                    ReasonPhrase = Token,
                };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            Method = request.Method;
            Url = request.RequestUri!.AbsoluteUri;
            Headers = request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray());
            HasContent = request.Content is not null;
            return Task.FromResult(Respond());
        }
    }

    internal static CmsAuthorizationMetadataResult.Failure Failure(CmsAuthorizationMetadataResult result) =>
        result.Should().BeOfType<CmsAuthorizationMetadataResult.Failure>().Subject;

    internal static string RenderFailure(CmsAuthorizationMetadataResult result)
    {
        using StringWriter error = new();
        CommandDiagnostics.ExecutionFailure(error, "Verification", Failure(result).Message).Should().Be(1);
        return error.ToString();
    }
}

[TestFixture("https://cms.example", "https://cms.example/v3/authorizationMetadata", "")]
[TestFixture("https://cms.example/", "https://cms.example/v3/authorizationMetadata", "district-one")]
[TestFixture(
    "https://cms.example/platform/cms",
    "https://cms.example/platform/cms/v3/authorizationMetadata",
    "district-one"
)]
[TestFixture(
    "https://cms.example/platform/cms/",
    "https://cms.example/platform/cms/v3/authorizationMetadata",
    ""
)]
[TestFixture(
    "http://localhost:8081/cms",
    "http://localhost:8081/cms/v3/authorizationMetadata",
    "district-two"
)]
[TestFixture(
    "https://cms.example/a%20prefix/",
    "https://cms.example/a%20prefix/v3/authorizationMetadata",
    ""
)]
public class Given_CmsAuthorizationMetadataClient_request(string baseUrl, string expectedUrl, string tenant)
{
    private CmsAuthorizationMetadataClientFixture _fixture = null!;
    private CmsAuthorizationMetadataResult _result = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = new();
        _result = await _fixture.Client.GetAsync(baseUrl, _fixture.VariableName, tenant);
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public void It_gets_the_complete_metadata_endpoint_once_without_a_filter_or_body()
    {
        _fixture.Handler.Calls.Should().Be(1);
        _fixture.Handler.Method.Should().Be(HttpMethod.Get);
        _fixture.Handler.Url.Should().Be(expectedUrl);
        _fixture.Handler.HasContent.Should().BeFalse();
    }

    [Test]
    public void It_sends_only_the_requested_credentials_and_optional_tenant()
    {
        Dictionary<string, string[]> expected = new()
        {
            ["Authorization"] = [$"Bearer {CmsAuthorizationMetadataClientFixture.Token}"],
        };
        if (tenant.Length > 0)
        {
            expected.Add("Tenant", [tenant]);
        }

        _fixture.Handler.Headers.Should().BeEquivalentTo(expected);
        _fixture.HttpClient.DefaultRequestHeaders.Should().BeEmpty();
    }

    [Test]
    public void It_returns_the_validated_dereferenced_permissions()
    {
        var success = _result.Should().BeOfType<CmsAuthorizationMetadataResult.Success>().Subject;
        success
            .Metadata.Should()
            .BeEquivalentTo(
                AuthorizationMetadataValidatorFixtures.Success(
                    AuthorizationMetadataValidatorFixtures.ValidJson
                ),
                options => options.WithStrictOrdering()
            );
    }
}

[TestFixture]
public class Given_CmsAuthorizationMetadataClient_invalid_configuration
{
    [TestCase("missing")]
    [TestCase("empty")]
    [TestCase("whitespace")]
    public async Task It_rejects_missing_credentials_before_http(string kind)
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        Environment.SetEnvironmentVariable(
            fixture.VariableName,
            kind switch
            {
                "missing" => null,
                "empty" => "",
                _ => " \t ",
            }
        );

        var result = await fixture.Client.GetAsync("https://cms.example", fixture.VariableName);

        CmsAuthorizationMetadataClientFixture
            .Failure(result)
            .Kind.Should()
            .Be(CmsAuthorizationMetadataFailureKind.MissingCredentials);
        fixture.Handler.Calls.Should().Be(0);
    }

    [TestCase("")]
    [TestCase("invalid=variable")]
    public async Task It_rejects_unavailable_variable_names_before_http(string name)
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        var result = await fixture.Client.GetAsync("https://cms.example", name);

        CmsAuthorizationMetadataClientFixture
            .Failure(result)
            .Kind.Should()
            .Be(CmsAuthorizationMetadataFailureKind.MissingCredentials);
        fixture.Handler.Calls.Should().Be(0);
    }

    [TestCase("relative/cms")]
    [TestCase("file:///cms")]
    [TestCase("https://user:password@cms.example")]
    [TestCase("https://cms.example?claimSetName=School")]
    [TestCase("https://cms.example/#fragment")]
    public async Task It_rejects_invalid_or_ambiguous_base_urls_before_http(string url)
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        var result = await fixture.Client.GetAsync(url, fixture.VariableName);

        CmsAuthorizationMetadataClientFixture
            .Failure(result)
            .Kind.Should()
            .Be(CmsAuthorizationMetadataFailureKind.InvalidRequest);
        fixture.Handler.Calls.Should().Be(0);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_rejects_invalid_header_values_without_echoing_them(bool invalidToken)
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        string invalid = $"{CmsAuthorizationMetadataClientFixture.Token}\r\nInjected: value";
        Environment.SetEnvironmentVariable(
            fixture.VariableName,
            invalidToken ? invalid : CmsAuthorizationMetadataClientFixture.Token
        );
        var result = await fixture.Client.GetAsync(
            "https://cms.example",
            fixture.VariableName,
            invalidToken ? "" : invalid
        );

        CmsAuthorizationMetadataClientFixture
            .Failure(result)
            .Kind.Should()
            .Be(CmsAuthorizationMetadataFailureKind.InvalidRequest);
        CmsAuthorizationMetadataClientFixture
            .RenderFailure(result)
            .Should()
            .Be($"Verification failed: CMS request URL or headers are invalid.{Environment.NewLine}");
        result.ToString().Should().NotContain(CmsAuthorizationMetadataClientFixture.Token);
        fixture.Handler.Calls.Should().Be(0);
    }
}

[TestFixture]
public class Given_CmsAuthorizationMetadataClient_http_failures
{
    [TestCase(401)]
    [TestCase(403)]
    [TestCase(404)]
    [TestCase(429)]
    [TestCase(500)]
    [TestCase(302)]
    [TestCase(204)]
    [TestCase(206)]
    public async Task It_returns_a_safe_structured_failure_without_using_the_body_or_reason(int status)
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        fixture.Handler.StatusCode = (HttpStatusCode)status;
        fixture.Handler.Body = CmsAuthorizationMetadataClientFixture.Token;

        var result = await fixture.Client.GetAsync("https://cms.example", fixture.VariableName);

        CmsAuthorizationMetadataClientFixture
            .Failure(result)
            .Should()
            .Be(
                new CmsAuthorizationMetadataResult.Failure(
                    CmsAuthorizationMetadataFailureKind.HttpFailure,
                    $"CMS authorization metadata request returned HTTP {status}.",
                    status
                )
            );
        CmsAuthorizationMetadataClientFixture
            .RenderFailure(result)
            .Should()
            .Be(
                $"Verification failed: CMS authorization metadata request returned HTTP {status}.{Environment.NewLine}"
            );
        result.ToString().Should().NotContain(CmsAuthorizationMetadataClientFixture.Token);
        fixture.Handler.Calls.Should().Be(1);
    }

    [Test]
    public async Task It_rejects_partial_content_even_when_it_contains_valid_metadata()
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        fixture.Handler.StatusCode = HttpStatusCode.PartialContent;
        var result = await fixture.Client.GetAsync("https://cms.example", fixture.VariableName);

        CmsAuthorizationMetadataClientFixture.Failure(result).StatusCode.Should().Be(206);
    }

    [TestCase("http")]
    [TestCase("io")]
    [TestCase("canceled")]
    [TestCase("timeout")]
    public async Task It_does_not_expose_transport_exception_messages(string kind)
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        fixture.Handler.Respond = () =>
            throw kind switch
            {
                "http" => new HttpRequestException(CmsAuthorizationMetadataClientFixture.Token),
                "io" => new IOException(CmsAuthorizationMetadataClientFixture.Token),
                "canceled" => new OperationCanceledException(CmsAuthorizationMetadataClientFixture.Token),
                _ => new TaskCanceledException(CmsAuthorizationMetadataClientFixture.Token),
            };

        var result = await fixture.Client.GetAsync("https://cms.example", fixture.VariableName);

        CmsAuthorizationMetadataClientFixture
            .Failure(result)
            .Kind.Should()
            .Be(CmsAuthorizationMetadataFailureKind.TransportFailure);
        CmsAuthorizationMetadataClientFixture
            .RenderFailure(result)
            .Should()
            .Be(
                $"Verification failed: CMS authorization metadata request could not be completed.{Environment.NewLine}"
            );
        result.ToString().Should().NotContain(CmsAuthorizationMetadataClientFixture.Token);
    }

    [Test]
    public async Task It_does_not_expose_response_read_failures()
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        fixture.Handler.Respond = () => new(HttpStatusCode.OK) { Content = new FailingContent() };

        var result = await fixture.Client.GetAsync("https://cms.example", fixture.VariableName);

        CmsAuthorizationMetadataClientFixture
            .Failure(result)
            .Kind.Should()
            .Be(CmsAuthorizationMetadataFailureKind.TransportFailure);
        CmsAuthorizationMetadataClientFixture
            .RenderFailure(result)
            .Should()
            .NotContain(CmsAuthorizationMetadataClientFixture.Token);
    }

    private sealed class FailingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException(CmsAuthorizationMetadataClientFixture.Token);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

[TestFixture]
public class Given_CmsAuthorizationMetadataClient_cancellation_during_response_body
{
    private CmsAuthorizationMetadataResult _result = null!;

    [SetUp]
    public async Task Setup()
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        fixture.Handler.Respond = () =>
            new(HttpStatusCode.OK)
            {
                Content = new CancelingContent(fixture.HttpClient.CancelPendingRequests),
            };

        _result = await fixture.Client.GetAsync("https://cms.example", fixture.VariableName);
    }

    [Test]
    public void It_keeps_the_body_read_inside_the_HttpClient_cancellation_scope() =>
        CmsAuthorizationMetadataClientFixture
            .Failure(_result)
            .Kind.Should()
            .Be(CmsAuthorizationMetadataFailureKind.TransportFailure);

    [Test]
    public void It_reports_a_safe_execution_failure() =>
        CmsAuthorizationMetadataClientFixture
            .RenderFailure(_result)
            .Should()
            .Be(
                $"Verification failed: CMS authorization metadata request could not be completed.{Environment.NewLine}"
            );

    private sealed class CancelingContent(Action cancelPendingRequests) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken
        )
        {
            // Exercise HttpClient's timeout cancellation scope without depending on a timer.
            cancelPendingRequests();
            cancellationToken.ThrowIfCancellationRequested();
            await stream.WriteAsync(
                Encoding.UTF8.GetBytes(AuthorizationMetadataValidatorFixtures.ValidJson),
                cancellationToken
            );
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

[TestFixture]
internal class Given_CmsAuthorizationMetadataClient_invalid_metadata
{
    [TestCase("claimSetName")]
    [TestCase("claim")]
    [TestCase("action")]
    [TestCase("strategy")]
    public async Task It_does_not_return_credentials_echoed_in_valid_permission_values(string field)
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        var input = AuthorizationMetadataValidatorFixtures.Valid();
        JsonNode node = field switch
        {
            "claimSetName" => input[0]!,
            "claim" => input[0]!["claims"]![0]!,
            "action" => input[0]!["authorizations"]![0]!["actions"]![0]!,
            _ => input[0]!["authorizations"]![0]!["actions"]![0]!["authorizationStrategies"]![0]!,
        };
        node[field == "claimSetName" ? "claimSetName" : "name"] =
            $"prefix-{CmsAuthorizationMetadataClientFixture.Token}-suffix";
        // JSON escaping cannot hide a reflected credential from the client.
        fixture.Handler.Body = input
            .ToJsonString()
            .Replace("distinctive", "\\u0064istinctive", StringComparison.Ordinal);
        AuthorizationMetadataValidator
            .Validate(fixture.Handler.Body)
            .Should()
            .BeOfType<AuthorizationMetadataValidationResult.Success>();

        var result = await fixture.Client.GetAsync("https://cms.example", fixture.VariableName);

        CmsAuthorizationMetadataClientFixture
            .Failure(result)
            .Kind.Should()
            .Be(CmsAuthorizationMetadataFailureKind.InvalidMetadata);
        CmsAuthorizationMetadataClientFixture
            .RenderFailure(result)
            .Should()
            .Be(
                $"Verification failed: CMS authorization metadata contains credential material and cannot be reported.{Environment.NewLine}"
            );
        result.ToString().Should().NotContain(CmsAuthorizationMetadataClientFixture.Token);
    }

    private static IEnumerable<TestCaseData> InvalidPayloads()
    {
        yield return new TestCaseData(
            CmsAuthorizationMetadataClientFixture.Token,
            AuthorizationMetadataFailureKind.InvalidJson
        ).SetName("It_rejects_malformed_JSON_without_echoing_the_body");
        yield return new TestCaseData("{}", AuthorizationMetadataFailureKind.InvalidShape).SetName(
            "It_rejects_an_invalid_shape"
        );
        yield return new TestCaseData("", AuthorizationMetadataFailureKind.InvalidJson).SetName(
            "It_rejects_an_empty_body"
        );

        var unknownReference = AuthorizationMetadataValidatorFixtures.Valid();
        unknownReference[0]!["claimSetName"] = CmsAuthorizationMetadataClientFixture.Token;
        unknownReference[0]!["claims"]![0]!["authorizationId"] = 99;
        yield return new TestCaseData(
            unknownReference.ToJsonString(),
            AuthorizationMetadataFailureKind.UnknownAuthorizationId
        ).SetName("It_rejects_invalid_references_without_echoing_claim_set_names");

        var duplicateUri = AuthorizationMetadataValidatorFixtures.Valid();
        duplicateUri[0]!["claims"]![0]!["name"] = CmsAuthorizationMetadataClientFixture.Token;
        duplicateUri[0]!["claims"]!.AsArray().Add(duplicateUri[0]!["claims"]![0]!.DeepClone());
        yield return new TestCaseData(
            duplicateUri.ToJsonString(),
            AuthorizationMetadataFailureKind.DuplicateClaimName
        ).SetName("It_rejects_duplicate_per_set_URIs_without_echoing_them");

        yield return new TestCaseData(
            $$"""[{"{{CmsAuthorizationMetadataClientFixture.Token}}":1,"{{CmsAuthorizationMetadataClientFixture.Token}}":2}]""",
            AuthorizationMetadataFailureKind.DuplicateProperty
        ).SetName("It_rejects_duplicate_properties_without_echoing_them");

        var partiallyValid = AuthorizationMetadataValidatorFixtures.Valid();
        partiallyValid.Add(new JsonObject { ["claimSetName"] = "Invalid second set" });
        yield return new TestCaseData(
            partiallyValid.ToJsonString(),
            AuthorizationMetadataFailureKind.InvalidShape
        ).SetName("It_rejects_the_entire_response_when_a_later_set_is_invalid");
    }

    [TestCaseSource(nameof(InvalidPayloads))]
    public async Task It_exposes_only_safe_validation_categories(
        string body,
        AuthorizationMetadataFailureKind kind
    )
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        fixture.Handler.Body = body;
        var result = await fixture.Client.GetAsync("https://cms.example", fixture.VariableName);

        var failure = CmsAuthorizationMetadataClientFixture.Failure(result);
        failure.Kind.Should().Be(CmsAuthorizationMetadataFailureKind.InvalidMetadata);
        failure.ValidationFailureKinds.Should().Contain(kind);
        CmsAuthorizationMetadataClientFixture
            .RenderFailure(result)
            .Should()
            .Be($"Verification failed: CMS returned invalid authorization metadata.{Environment.NewLine}");
        result.ToString().Should().NotContain(CmsAuthorizationMetadataClientFixture.Token);
    }
}

[TestFixture]
public class Given_CmsAuthorizationMetadataClient_reuse
{
    [Test]
    public async Task It_reads_the_named_variable_each_time_and_does_not_reuse_tenant_headers()
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        await fixture.Client.GetAsync("https://cms.example", fixture.VariableName, "district-one");
        Environment.SetEnvironmentVariable(fixture.VariableName, "replacement-token");

        var result = await fixture.Client.GetAsync("https://cms.example", fixture.VariableName);

        result.Should().BeOfType<CmsAuthorizationMetadataResult.Success>();
        fixture.Handler.Calls.Should().Be(2);
        fixture
            .Handler.Headers.Should()
            .BeEquivalentTo(
                new Dictionary<string, string[]> { ["Authorization"] = ["Bearer replacement-token"] }
            );
    }

    [Test]
    public async Task It_accepts_a_complete_empty_catalog()
    {
        using CmsAuthorizationMetadataClientFixture fixture = new();
        fixture.Handler.Body = "[]";

        var result = await fixture.Client.GetAsync("https://cms.example", fixture.VariableName);

        result
            .Should()
            .BeOfType<CmsAuthorizationMetadataResult.Success>()
            .Which.Metadata.ClaimSets.Should()
            .BeEmpty();
    }
}
