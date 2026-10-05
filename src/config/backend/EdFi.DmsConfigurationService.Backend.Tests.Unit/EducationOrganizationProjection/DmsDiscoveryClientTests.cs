// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;
using System.Text;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;
using Stage = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionStage;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

public class DmsDiscoveryClientTests
{
    private const string BaseUrl = "https://dms.example.org/api";
    private const string Tenant = "Tenant_255901";
    private const string TenantRoot = BaseUrl + "/" + Tenant;
    private const string Version = "educationOrganizationProjection.v1";

    private static readonly DateTimeOffset _start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Real time allowed for a step the test has already released through the fake clock, a token or a handler. A
    /// correct client finishes without waiting; the bound only turns a regression that leaves the read pending into a
    /// failure instead of a hung run.
    /// </summary>
    private static readonly TimeSpan _hangGuard = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, string> _contexts = new()
    {
        ["districtId"] = "255901",
        ["schoolYear"] = "2026",
    };

    /// <summary>A DMS Discovery document with the projection members, unknown members included.</summary>
    private static string Document(
        string root = TenantRoot,
        string? tokenUrl = null,
        string? projectionUrl = null,
        string versions = "\"" + Version + "\""
    ) =>
        $$"""
            {
              "version": "8.1.0",
              "applicationName": "Ed-Fi Alliance Data Management Service",
              "dataModels": [{ "name": "Ed-Fi", "version": "5.2.0" }],
              "urls": {
                "dependencies": "{{root}}/metadata/dependencies",
                "openApiMetadata": "{{root}}/metadata/specifications",
                "oauth": "{{tokenUrl ?? root + "/{districtId}/{schoolYear}/oauth/token"}}",
                "dataManagementApi": "{{root}}/{districtId}/{schoolYear}/data",
                "educationOrganizationProjection": "{{projectionUrl
                ?? root + "/{districtId}/{schoolYear}/management/education-organizations"}}"
              },
              "educationOrganizationProjection": { "contractVersions": [{{versions}}], "future": true }
            }
            """;

    private static HttpResponseMessage Ok(string body) => DmsResponses.Text(HttpStatusCode.OK, body);

    /// <summary>A Discovery client over a fake DMS and a fake clock.</summary>
    public sealed class Harness
    {
        public Harness(
            FakeDmsHandler handler,
            Action<DmsEducationOrganizationProjectionSettings>? configure = null
        )
        {
            Handler = handler;
            DmsEducationOrganizationProjectionSettings settings = new()
            {
                DmsBaseUrl = BaseUrl,
                Credentials = new() { ClientId = "id", ClientSecret = "secret" },
            };
            configure?.Invoke(settings);
            Client = new DmsDiscoveryClient(
                new SingleHandlerHttpClientFactory(
                    handler,
                    DmsEducationOrganizationProjectionHttpClient.Name
                ),
                Options.Create(settings),
                Time
            );
        }

        public FakeTimeProvider Time { get; } = new(_start);

        public FakeDmsHandler Handler { get; }

        public DmsDiscoveryClient Client { get; }

        public Task<DmsDiscoveryResolution> ResolveAsync(
            string? tenant = Tenant,
            IReadOnlyDictionary<string, string>? contexts = null,
            DateTimeOffset? deadline = null,
            CancellationToken cancellationToken = default
        ) =>
            Client.ResolveAsync(
                tenant,
                contexts ?? _contexts,
                deadline ?? Time.GetUtcNow().AddSeconds(600),
                cancellationToken
            );
    }

    private static DmsDiscoveryResolution.Resolved ShouldBeResolved(DmsDiscoveryResolution resolution) =>
        resolution.Should().BeOfType<DmsDiscoveryResolution.Resolved>().Which;

    private static EducationOrganizationProjectionFailure ShouldBeFailed(DmsDiscoveryResolution resolution) =>
        resolution.Should().BeOfType<DmsDiscoveryResolution.Failed>().Which.Failure;

    /// <summary>A Discovery-stage failure with the code's own category and nothing read.</summary>
    private static void ShouldFailWith(DmsDiscoveryResolution resolution, Code code, int? httpStatus)
    {
        EducationOrganizationProjectionFailure failure = ShouldBeFailed(resolution);
        failure.Code.Should().Be(code);
        failure.Category.Should().Be(EducationOrganizationProjectionFailure.CategoryOf(code));
        failure.Stage.Should().Be(Stage.Discovery);
        failure.HttpStatus.Should().Be(httpStatus);
        (failure.PagesRead, failure.Restarts).Should().Be((0, 0));
    }

    [TestFixture]
    public class Given_a_multi_tenant_read
    {
        private Harness _harness = null!;
        private DmsDiscoveryResolution.Resolved _resolved = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(FakeDmsHandler.Answering(() => Ok(Document())));
            _resolved = ShouldBeResolved(await _harness.ResolveAsync());
        }

        [Test]
        public void It_reads_the_tenant_discovery_document_once() =>
            _harness
                .Handler.Requests.Select(request => (request.Method, request.Uri.AbsoluteUri))
                .Should()
                .Equal((HttpMethod.Get, TenantRoot));

        [Test]
        public void It_asks_for_json() => _harness.Handler.Requests[0].Accept.Should().Be("application/json");

        [Test]
        public void It_sends_no_credentials_or_cookies() =>
            (_harness.Handler.Requests[0].HasAuthorization, _harness.Handler.Requests[0].HasCookie)
                .Should()
                .Be((false, false));

        [Test]
        public void It_resolves_the_token_url() =>
            _resolved.TokenUrl.AbsoluteUri.Should().Be(TenantRoot + "/255901/2026/oauth/token");

        [Test]
        public void It_resolves_the_projection_url() =>
            _resolved
                .ProjectionUrl.AbsoluteUri.Should()
                .Be(TenantRoot + "/255901/2026/management/education-organizations");

        [Test]
        public void It_chooses_the_contract_version() => _resolved.ContractVersion.Should().Be(Version);
    }

    [TestFixture]
    public class Given_a_single_tenant_read
    {
        private Harness _harness = null!;
        private DmsDiscoveryResolution.Resolved _resolved = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(FakeDmsHandler.Answering(() => Ok(Document(root: BaseUrl))));
            _resolved = ShouldBeResolved(await _harness.ResolveAsync(tenant: null));
        }

        [Test]
        public void It_reads_the_root_discovery_document() =>
            _harness
                .Handler.Requests.Select(request => request.Uri.AbsoluteUri)
                .Should()
                .Equal(BaseUrl + "/");

        [Test]
        public void It_resolves_both_urls() =>
            (_resolved.TokenUrl.AbsoluteUri, _resolved.ProjectionUrl.AbsoluteUri)
                .Should()
                .Be(
                    (
                        BaseUrl + "/255901/2026/oauth/token",
                        BaseUrl + "/255901/2026/management/education-organizations"
                    )
                );
    }

    [TestFixture("https://dms.example.org/api/", Tenant, "https://dms.example.org/api/Tenant_255901")]
    [TestFixture("https://dms.example.org/api/", null, "https://dms.example.org/api/")]
    [TestFixture("https://dms.example.org", Tenant, "https://dms.example.org/Tenant_255901")]
    [TestFixture("https://dms.example.org/", null, "https://dms.example.org/")]
    [TestFixture(
        "https://dms.example.org:8443/a/b",
        Tenant,
        "https://dms.example.org:8443/a/b/Tenant_255901"
    )]
    [TestFixture(BaseUrl, "Tenant 1/x?y#z%", "https://dms.example.org/api/Tenant%201%2Fx%3Fy%23z%25")]
    [TestFixture(BaseUrl, "Ünïcødé", "https://dms.example.org/api/%C3%9Cn%C3%AFc%C3%B8d%C3%A9")]
    public class Given_a_base_url_and_tenant(string baseUrl, string? tenant, string expectedUrl)
    {
        private Harness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(
                FakeDmsHandler.Answering(() => Ok(Document())),
                settings => settings.DmsBaseUrl = baseUrl
            );
            await _harness.ResolveAsync(tenant);
        }

        [Test]
        public void It_reads_discovery_at_the_tenant_segment_under_the_base_path() =>
            _harness.Handler.Requests.Single().Uri.AbsoluteUri.Should().Be(expectedUrl);
    }

    [TestFixture("")]
    [TestFixture(".")]
    [TestFixture("..")]
    [TestFixture("Tenant<high>")]
    [TestFixture("<low>Tenant")]
    public class Given_a_tenant_that_cannot_be_a_path_segment(string tenant)
    {
        private Harness _harness = null!;
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(FakeDmsHandler.Answering(() => Ok(Document())));
            _resolution = await _harness.ResolveAsync(
                ProjectionUrlTemplateResolverTests.WithLoneSurrogates(tenant)
            );
        }

        [Test]
        public void It_is_not_routable() => ShouldFailWith(_resolution, Code.TargetNotRoutable, null);

        [Test]
        public void It_sends_no_request() => _harness.Handler.Requests.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_no_base_url
    {
        private Harness _harness = null!;
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(
                FakeDmsHandler.Answering(() => Ok(Document())),
                settings => settings.DmsBaseUrl = null
            );
            _resolution = await _harness.ResolveAsync();
        }

        [Test]
        public void It_is_not_configured() => ShouldFailWith(_resolution, Code.NotConfigured, null);

        [Test]
        public void It_sends_no_request() => _harness.Handler.Requests.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_store_context_the_templates_need_is_missing
    {
        private Harness _harness = null!;
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(FakeDmsHandler.Answering(() => Ok(Document())));
            await _harness.ResolveAsync();
            _resolution = await _harness.ResolveAsync(
                contexts: new Dictionary<string, string> { ["districtId"] = "255901" }
            );
        }

        [Test]
        public void It_is_not_routable() => ShouldFailWith(_resolution, Code.TargetNotRoutable, null);

        [Test]
        public void It_sends_no_request_beyond_the_cached_discovery_read() =>
            _harness.Handler.Requests.Should().ContainSingle();

        [Test]
        public async Task It_keeps_the_cached_document_for_other_stores()
        {
            ShouldBeResolved(await _harness.ResolveAsync());
            _harness.Handler.Requests.Should().ContainSingle();
        }
    }

    [TestFixture("urls.educationOrganizationProjection")]
    [TestFixture("educationOrganizationProjection")]
    [TestFixture("both")]
    public class Given_discovery_without_the_projection(string missing)
    {
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup()
        {
            string url = $$"""
                "educationOrganizationProjection": "{{TenantRoot}}/{districtId}/{schoolYear}/management/education-organizations"
                """;
            string member = $$"""
                "educationOrganizationProjection": { "contractVersions": ["{{Version}}"] }
                """;
            string document = $$"""
                {
                  "urls": {
                    "oauth": "{{TenantRoot}}/{districtId}/{schoolYear}/oauth/token"
                    {{(missing is "urls.educationOrganizationProjection" or "both" ? "" : "," + url)}}
                  }
                  {{(missing is "educationOrganizationProjection" or "both" ? "" : "," + member)}}
                }
                """;
            _resolution = await new Harness(FakeDmsHandler.Answering(() => Ok(document))).ResolveAsync();
        }

        [Test]
        public void It_is_unsupported() => ShouldFailWith(_resolution, Code.Unsupported, 200);
    }

    [TestFixture("not json")]
    [TestFixture("")]
    [TestFixture("[]")]
    [TestFixture("""{}""")]
    [TestFixture("""{"urls":[]}""")]
    [TestFixture(
        """{"urls":{"educationOrganizationProjection":"https://dms.example.org/api/x"},"educationOrganizationProjection":{"contractVersions":["educationOrganizationProjection.v1"]}}"""
    )]
    [TestFixture(
        """{"urls":{"oauth":7,"educationOrganizationProjection":"https://dms.example.org/api/x"},"educationOrganizationProjection":{"contractVersions":["educationOrganizationProjection.v1"]}}"""
    )]
    [TestFixture(
        """{"urls":{"oauth":"","educationOrganizationProjection":"https://dms.example.org/api/x"},"educationOrganizationProjection":{"contractVersions":["educationOrganizationProjection.v1"]}}"""
    )]
    [TestFixture(
        """{"urls":{"oauth":"https://dms.example.org/api/t","educationOrganizationProjection":7},"educationOrganizationProjection":{"contractVersions":["educationOrganizationProjection.v1"]}}"""
    )]
    [TestFixture(
        """{"urls":{"oauth":"https://dms.example.org/api/t","educationOrganizationProjection":null},"educationOrganizationProjection":{"contractVersions":["educationOrganizationProjection.v1"]}}"""
    )]
    [TestFixture(
        """{"urls":{"oauth":"https://dms.example.org/api/t","educationOrganizationProjection":""},"educationOrganizationProjection":{"contractVersions":["educationOrganizationProjection.v1"]}}"""
    )]
    [TestFixture(
        """{"urls":{"oauth":"https://dms.example.org/api/t","educationOrganizationProjection":"https://dms.example.org/api/x"},"educationOrganizationProjection":["educationOrganizationProjection.v1"]}"""
    )]
    [TestFixture(
        """{"urls":{"oauth":"https://dms.example.org/api/t","educationOrganizationProjection":"https://dms.example.org/api/x"},"educationOrganizationProjection":{}}"""
    )]
    [TestFixture(
        """{"urls":{"oauth":"https://dms.example.org/api/t","educationOrganizationProjection":"https://dms.example.org/api/x"},"educationOrganizationProjection":{"contractVersions":"educationOrganizationProjection.v1"}}"""
    )]
    [TestFixture(
        """{"urls":{"oauth":"https://dms.example.org/api/t","educationOrganizationProjection":"https://dms.example.org/api/x"},"educationOrganizationProjection":{"contractVersions":[]}}"""
    )]
    [TestFixture(
        """{"urls":{"oauth":"https://dms.example.org/api/t","educationOrganizationProjection":"https://dms.example.org/api/x"},"educationOrganizationProjection":{"contractVersions":["educationOrganizationProjection.v1",null]}}"""
    )]
    [TestFixture(
        """{"urls":{"oauth":"https://dms.example.org/api/t","educationOrganizationProjection":"https://dms.example.org/api/x"},"educationOrganizationProjection":{"contractVersions":["educationOrganizationProjection.v1"]}"""
    )]
    public class Given_a_malformed_discovery_document(string document)
    {
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup() =>
            _resolution = await new Harness(FakeDmsHandler.Answering(() => Ok(document))).ResolveAsync();

        [Test]
        public void It_is_invalid() => ShouldFailWith(_resolution, Code.DiscoveryInvalid, 200);
    }

    [TestFixture]
    public class Given_discovery_with_invalid_utf8_in_a_used_member
    {
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup()
        {
            byte[] document = [.. Encoding.UTF8.GetBytes(Document(tokenUrl: TenantRoot + "/oauth/tokenXX"))];
            int marker = Encoding.UTF8.GetString(document).IndexOf("tokenXX", StringComparison.Ordinal) + 5;
            document[marker] = 0xC3;
            document[marker + 1] = 0x28;

            _resolution = await new Harness(
                FakeDmsHandler.Answering(() =>
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(document) }
                )
            ).ResolveAsync();
        }

        [Test]
        public void It_is_invalid() => ShouldFailWith(_resolution, Code.DiscoveryInvalid, 200);
    }

    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_discovery_documents_around_the_size_limit(bool declaredLength)
    {
        /// <summary>The Discovery document with a padding member that makes it exactly <paramref name="length"/> bytes.</summary>
        private static byte[] Padded(int length)
        {
            string prefix = Document()[..^1] + ",\"pad\":\"";
            int padding = length - Encoding.UTF8.GetByteCount(prefix) - 2;
            return Encoding.UTF8.GetBytes(prefix + new string('a', padding) + "\"}");
        }

        private HttpResponseMessage Response(byte[] body) =>
            declaredLength
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : DmsResponses.Stream(HttpStatusCode.OK, new UnknownLengthStream(body));

        [Test]
        public async Task It_reads_a_document_of_exactly_the_limit()
        {
            byte[] body = Padded(DmsDiscoveryClient.MaxDocumentBytes);
            body.Length.Should().Be(DmsDiscoveryClient.MaxDocumentBytes);

            ShouldBeResolved(
                await new Harness(FakeDmsHandler.Answering(() => Response(body))).ResolveAsync()
            );
        }

        [Test]
        public async Task It_refuses_a_document_over_the_limit()
        {
            byte[] body = Padded(DmsDiscoveryClient.MaxDocumentBytes + 1);

            ShouldFailWith(
                await new Harness(FakeDmsHandler.Answering(() => Response(body))).ResolveAsync(),
                Code.DiscoveryInvalid,
                200
            );
        }
    }

    [TestFixture]
    public class Given_no_contract_version_in_common
    {
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup() =>
            _resolution = await new Harness(
                FakeDmsHandler.Answering(() =>
                    Ok(
                        Document(
                            versions: "\"educationOrganizationProjection.v2\", \"EducationOrganizationProjection.V1\""
                        )
                    )
                )
            ).ResolveAsync();

        [Test]
        public void It_is_an_unsupported_contract() =>
            ShouldFailWith(_resolution, Code.UnsupportedContract, 200);
    }

    [TestFixture]
    public class Given_discovery_lists_more_versions_than_this_service_offers
    {
        private DmsDiscoveryResolution.Resolved _resolved = null!;

        [SetUp]
        public async Task Setup() =>
            _resolved = ShouldBeResolved(
                await new Harness(
                    FakeDmsHandler.Answering(() =>
                        Ok(Document(versions: "\"educationOrganizationProjection.v9\", \"" + Version + "\""))
                    ),
                    settings => settings.ContractVersions = [Version]
                ).ResolveAsync()
            );

        [Test]
        public void It_chooses_the_one_both_support() => _resolved.ContractVersion.Should().Be(Version);
    }

    [TestFixture]
    public class Given_discovery_lists_only_versions_this_service_is_not_configured_to_offer
    {
        private DmsDiscoveryResolution _resolution = null!;

        // Startup validation refuses a configured version this service cannot parse; the settings are built directly
        // here so the "offered by this service" half of the choice is pinned while only one version exists.
        [SetUp]
        public async Task Setup() =>
            _resolution = await new Harness(
                FakeDmsHandler.Answering(() => Ok(Document())),
                settings => settings.ContractVersions = ["educationOrganizationProjection.v9"]
            ).ResolveAsync();

        [Test]
        public void It_is_an_unsupported_contract() =>
            ShouldFailWith(_resolution, Code.UnsupportedContract, 200);
    }

    [TestFixture(HttpStatusCode.MovedPermanently)]
    [TestFixture(HttpStatusCode.Found)]
    [TestFixture(HttpStatusCode.SeeOther)]
    [TestFixture(HttpStatusCode.NotModified)]
    [TestFixture(HttpStatusCode.TemporaryRedirect)]
    [TestFixture(HttpStatusCode.PermanentRedirect)]
    public class Given_a_3xx_status(HttpStatusCode status)
    {
        private Harness _harness = null!;
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(
                FakeDmsHandler.Answering(() =>
                {
                    HttpResponseMessage response = new(status);
                    response.Headers.Location = new Uri("https://attacker.example/api/Tenant_255901");
                    return response;
                })
            );
            _resolution = await _harness.ResolveAsync();
        }

        [Test]
        public void It_is_invalid_discovery() =>
            ShouldFailWith(_resolution, Code.DiscoveryInvalid, (int)status);

        [Test]
        public void It_does_not_follow_it() =>
            _harness.Handler.Requests.Should().ContainSingle().Which.Uri.Host.Should().Be("dms.example.org");
    }

    private const string Security = "urn:ed-fi:api:system:configuration:security";

    [TestFixture(404, "application/problem+json", "urn:ed-fi:api:not-found", Code.TargetNotFound)]
    [TestFixture(404, "text/plain", "", Code.TargetNotFound)]
    [TestFixture(400, "application/problem+json", "urn:ed-fi:api:bad-request", Code.UnexpectedResponse)]
    [TestFixture(
        401,
        "application/problem+json",
        "urn:ed-fi:api:security:authentication",
        Code.UnexpectedResponse
    )]
    [TestFixture(
        403,
        "application/problem+json",
        "urn:ed-fi:api:security:authorization",
        Code.UnexpectedResponse
    )]
    [TestFixture(405, "", "", Code.UnexpectedResponse)]
    [TestFixture(410, "", "", Code.UnexpectedResponse)]
    [TestFixture(201, "application/json", "{}", Code.UnexpectedResponse)]
    [TestFixture(203, "application/json", "{}", Code.UnexpectedResponse)]
    [TestFixture(204, "", "", Code.UnexpectedResponse)]
    [TestFixture(429, "application/problem+json", "urn:ed-fi:api:too-many-requests", Code.RateLimited)]
    [TestFixture(500, "application/problem+json", Security, Code.Forbidden)]
    [TestFixture(
        500,
        "application/problem+json",
        "URN:ED-FI:API:SYSTEM:CONFIGURATION:SECURITY",
        Code.ServiceUnavailable
    )]
    [TestFixture(500, "application/problem+json", Security + ":x", Code.ServiceUnavailable)]
    [TestFixture(500, "application/json", Security, Code.ServiceUnavailable)]
    [TestFixture(500, "application/problem+json", "urn:ed-fi:api:system", Code.ServiceUnavailable)]
    [TestFixture(500, "", "", Code.ServiceUnavailable)]
    [TestFixture(501, "application/problem+json", Security, Code.ServiceUnavailable)]
    [TestFixture(502, "", "", Code.ServiceUnavailable)]
    [TestFixture(
        503,
        "application/problem+json",
        "urn:ed-fi:api:service-unavailable",
        Code.ServiceUnavailable
    )]
    [TestFixture(504, "", "", Code.ServiceUnavailable)]
    public class Given_a_discovery_status_other_than_200(
        int status,
        string mediaType,
        string problemType,
        Code expected
    )
    {
        private DmsDiscoveryResolution _resolution = null!;

        /// <summary>No content when there is no media type; a problem-shaped body for a URN; otherwise the text.</summary>
        private HttpResponseMessage Response()
        {
            if (mediaType.Length == 0)
            {
                return new HttpResponseMessage((HttpStatusCode)status);
            }
            string body = problemType.StartsWith("urn", StringComparison.OrdinalIgnoreCase)
                ? $$"""{"type":"{{problemType}}","correlationId":"c-1"}"""
                : problemType;
            return DmsResponses.Text((HttpStatusCode)status, body, mediaType);
        }

        [SetUp]
        public async Task Setup() =>
            _resolution = await new Harness(FakeDmsHandler.Answering(Response)).ResolveAsync();

        [Test]
        public void It_is_classified() => ShouldFailWith(_resolution, expected, status);
    }

    [TestFixture]
    public class Given_the_non_problem_500_body
    {
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup() =>
            _resolution = await new Harness(
                FakeDmsHandler.Answering(() =>
                    DmsResponses.Text(
                        HttpStatusCode.InternalServerError,
                        """{"message":"The server encountered an unexpected condition.","traceId":"0HN:02"}"""
                    )
                )
            ).ResolveAsync();

        [Test]
        public void It_is_service_unavailable_without_problem_fields()
        {
            ShouldFailWith(_resolution, Code.ServiceUnavailable, 500);
            EducationOrganizationProjectionFailure failure = ShouldBeFailed(_resolution);
            failure.ProblemType.Should().BeNull();
            failure.CorrelationId.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_a_security_problem_too_large_to_read
    {
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup()
        {
            string body =
                $$"""{"type":"{{Security}}","pad":"{{new string('a', ProjectionHttpContent.MaxProblemBodyBytes)}}"}""";
            _resolution = await new Harness(
                FakeDmsHandler.Answering(() =>
                    DmsResponses.Text(HttpStatusCode.InternalServerError, body, "application/problem+json")
                )
            ).ResolveAsync();
        }

        [Test]
        public void It_is_service_unavailable() => ShouldFailWith(_resolution, Code.ServiceUnavailable, 500);
    }

    [TestFixture]
    public class Given_a_problem_document_with_hostile_values
    {
        private EducationOrganizationProjectionFailure _failure = null!;

        [SetUp]
        public async Task Setup() =>
            _failure = ShouldBeFailed(
                await new Harness(
                    FakeDmsHandler.Answering(() =>
                        DmsResponses.Text(
                            HttpStatusCode.NotFound,
                            """{"type":"urn:ed-fi:api:not-found\r\nFORGED <b>","correlationId":"c-1\r\n{token}","detail":"secret detail"}""",
                            "application/problem+json"
                        )
                    )
                ).ResolveAsync()
            );

        [Test]
        public void It_keeps_the_sanitized_problem_type() =>
            _failure.ProblemType.Should().Be("urn:ed-fi:api:not-foundFORGEDb");

        [Test]
        public void It_keeps_the_sanitized_correlation_id() => _failure.CorrelationId.Should().Be("c-1token");

        [Test]
        public void It_carries_no_url_or_body_text() =>
            _failure
                .ToString()
                .Should()
                .NotContainAny("dms.example.org", Tenant, "secret detail", "\r", "\n");
    }

    [TestFixture]
    public class Given_a_projection_template_outside_the_base_url
    {
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup() =>
            _resolution = await new Harness(
                FakeDmsHandler.Answering(() =>
                    Ok(Document(projectionUrl: "https://dms.example.org/api-other/Tenant_255901/x"))
                )
            ).ResolveAsync();

        [Test]
        public void It_is_invalid_discovery_without_a_status() =>
            ShouldFailWith(_resolution, Code.DiscoveryInvalid, null);
    }

    [TestFixture("http")]
    [TestFixture("io")]
    [TestFixture("socket")]
    public class Given_a_transport_failure(string kind)
    {
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup()
        {
            Exception failure = kind switch
            {
                "http" => new HttpRequestException("connect https://dms.example.org secret"),
                "io" => new IOException("reset secret"),
                _ => new SocketException((int)SocketError.ConnectionRefused),
            };
            _resolution = await new Harness(new FakeDmsHandler((_, _) => throw failure)).ResolveAsync();
        }

        [Test]
        public void It_is_a_network_error() => ShouldFailWith(_resolution, Code.NetworkError, null);
    }

    [TestFixture]
    public class Given_a_body_that_fails_while_reading
    {
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup() =>
            _resolution = await new Harness(
                FakeDmsHandler.Answering(() =>
                    DmsResponses.Stream(HttpStatusCode.OK, new StalledStream(new IOException("reset secret")))
                )
            ).ResolveAsync();

        [Test]
        public void It_is_a_network_error() => ShouldFailWith(_resolution, Code.NetworkError, null);
    }

    [TestFixture]
    public class Given_discovery_that_does_not_answer_in_time
    {
        private bool _completedBeforeTheTimeout;
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup()
        {
            Harness harness = new(
                FakeDmsHandler.NeverAnswering(),
                settings => settings.DiscoveryTimeoutSeconds = 10
            );
            Task<DmsDiscoveryResolution> pending = harness.ResolveAsync();
            await harness.Handler.RequestReceived.Task.WaitAsync(_hangGuard);

            harness.Time.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
            _completedBeforeTheTimeout = pending.IsCompleted;
            harness.Time.Advance(TimeSpan.FromTicks(1));
            _resolution = await pending.WaitAsync(_hangGuard);
        }

        [Test]
        public void It_waits_for_the_discovery_timeout() => _completedBeforeTheTimeout.Should().BeFalse();

        [Test]
        public void It_times_out() => ShouldFailWith(_resolution, Code.Timeout, null);
    }

    [TestFixture]
    public class Given_a_read_deadline_before_the_discovery_timeout
    {
        private bool _completedBeforeTheDeadline;
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup()
        {
            Harness harness = new(
                FakeDmsHandler.NeverAnswering(),
                settings => settings.DiscoveryTimeoutSeconds = 10
            );
            Task<DmsDiscoveryResolution> pending = harness.ResolveAsync(
                deadline: harness.Time.GetUtcNow().AddSeconds(3)
            );
            await harness.Handler.RequestReceived.Task.WaitAsync(_hangGuard);

            harness.Time.Advance(TimeSpan.FromSeconds(3) - TimeSpan.FromTicks(1));
            _completedBeforeTheDeadline = pending.IsCompleted;
            harness.Time.Advance(TimeSpan.FromTicks(1));
            _resolution = await pending.WaitAsync(_hangGuard);
        }

        [Test]
        public void It_waits_for_the_read_deadline() => _completedBeforeTheDeadline.Should().BeFalse();

        [Test]
        public void It_times_out_at_the_read_deadline() => ShouldFailWith(_resolution, Code.Timeout, null);
    }

    [TestFixture(0)]
    [TestFixture(-1)]
    public class Given_a_read_deadline_already_reached(int secondsFromNow)
    {
        private Harness _harness = null!;
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(FakeDmsHandler.Answering(() => Ok(Document())));
            _resolution = await _harness.ResolveAsync(
                deadline: _harness.Time.GetUtcNow().AddSeconds(secondsFromNow)
            );
        }

        [Test]
        public void It_times_out() => ShouldFailWith(_resolution, Code.Timeout, null);

        [Test]
        public void It_sends_no_request() => _harness.Handler.Requests.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_body_that_stalls
    {
        private DmsDiscoveryResolution _resolution = null!;

        [SetUp]
        public async Task Setup()
        {
            StalledStream body = new();
            Harness harness = new(
                FakeDmsHandler.Answering(() => DmsResponses.Stream(HttpStatusCode.OK, body))
            );
            Task<DmsDiscoveryResolution> pending = harness.ResolveAsync();
            await body.ReadStarted.Task.WaitAsync(_hangGuard);

            harness.Time.Advance(TimeSpan.FromSeconds(10));
            _resolution = await pending.WaitAsync(_hangGuard);
        }

        [Test]
        public void It_times_out_while_reading_the_body() => ShouldFailWith(_resolution, Code.Timeout, null);
    }

    [TestFixture]
    public class Given_caller_cancellation_while_discovery_is_outstanding
    {
        private CancellationTokenSource _caller = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _caller = new CancellationTokenSource();
            Harness harness = new(FakeDmsHandler.NeverAnswering());
            Task<DmsDiscoveryResolution> pending = harness.ResolveAsync(cancellationToken: _caller.Token);
            await harness.Handler.RequestReceived.Task.WaitAsync(_hangGuard);

            await _caller.CancelAsync();
            try
            {
                await pending.WaitAsync(_hangGuard);
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [TearDown]
        public void TearDown() => _caller.Dispose();

        [Test]
        public void It_throws_with_the_caller_token() =>
            _exception
                .Should()
                .BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(_caller.Token);
    }

    [TestFixture("http")]
    [TestFixture("canceled")]
    public class Given_caller_cancellation_with_a_timeout_and_another_failure_pending(string failure)
    {
        private CancellationTokenSource _caller = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _caller = new CancellationTokenSource();
            TaskCompletionSource<HttpResponseMessage> answer = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            Harness harness = new(new FakeDmsHandler((_, _) => answer.Task));
            Task<DmsDiscoveryResolution> pending = harness.ResolveAsync(cancellationToken: _caller.Token);
            await harness.Handler.RequestReceived.Task.WaitAsync(_hangGuard);

            await _caller.CancelAsync();
            harness.Time.Advance(TimeSpan.FromSeconds(10));
            answer.SetException(
                failure == "http" ? new HttpRequestException("reset") : new TaskCanceledException("timeout")
            );

            try
            {
                await pending.WaitAsync(_hangGuard);
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [TearDown]
        public void TearDown() => _caller.Dispose();

        [Test]
        public void It_reports_caller_cancellation_first() =>
            _exception
                .Should()
                .BeAssignableTo<OperationCanceledException>()
                .Which.CancellationToken.Should()
                .Be(_caller.Token);
    }

    [TestFixture]
    public class Given_a_caller_token_already_cancelled
    {
        private Harness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(FakeDmsHandler.Answering(() => Ok(Document())));
            try
            {
                await _harness.ResolveAsync(cancellationToken: new CancellationToken(true));
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [Test]
        public void It_throws() => _exception.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_sends_no_request() => _harness.Handler.Requests.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_reads_within_the_cache_period
    {
        private Harness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(FakeDmsHandler.Answering(() => Ok(Document())));
            await _harness.ResolveAsync();
            _harness.Time.Advance(TimeSpan.FromSeconds(300) - TimeSpan.FromTicks(1));
            ShouldBeResolved(await _harness.ResolveAsync());
        }

        [Test]
        public void It_reads_discovery_once() => _harness.Handler.Requests.Should().ContainSingle();

        [Test]
        public async Task It_reads_discovery_again_once_the_period_ends()
        {
            _harness.Time.Advance(TimeSpan.FromTicks(1));
            ShouldBeResolved(await _harness.ResolveAsync());
            _harness.Handler.Requests.Should().HaveCount(2);
        }

        [Test]
        public async Task It_reads_discovery_again_after_invalidation()
        {
            _harness.Client.Invalidate(Tenant);
            ShouldBeResolved(await _harness.ResolveAsync());
            _harness.Handler.Requests.Should().HaveCount(2);
        }

        [Test]
        public async Task It_keeps_one_tenant_cached_when_another_is_invalidated()
        {
            _harness.Client.Invalidate("Other");
            _harness.Client.Invalidate(null);
            ShouldBeResolved(await _harness.ResolveAsync());
            _harness.Handler.Requests.Should().ContainSingle();
        }
    }

    [TestFixture]
    public class Given_reads_for_two_tenants_and_single_tenant_mode
    {
        private Harness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(FakeDmsHandler.Answering(() => Ok(Document())));
            await _harness.ResolveAsync(Tenant);
            await _harness.ResolveAsync("Tenant_2");
            await _harness.ResolveAsync(null);
            await _harness.ResolveAsync(Tenant);
            await _harness.ResolveAsync("Tenant_2");
            await _harness.ResolveAsync(null);
        }

        [Test]
        public void It_caches_each_tenant_separately() =>
            _harness
                .Handler.Requests.Select(request => request.Uri.AbsoluteUri)
                .Should()
                .Equal(TenantRoot, BaseUrl + "/Tenant_2", BaseUrl + "/");

        [Test]
        public async Task It_invalidates_single_tenant_mode_by_null()
        {
            _harness.Client.Invalidate(null);
            await _harness.ResolveAsync(null);
            await _harness.ResolveAsync(Tenant);
            _harness.Handler.Requests.Should().HaveCount(4);
        }
    }

    [TestFixture]
    public class Given_caching_disabled
    {
        private Harness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(
                FakeDmsHandler.Answering(() => Ok(Document())),
                settings => settings.DiscoveryCacheSeconds = 0
            );
            ShouldBeResolved(await _harness.ResolveAsync());
            ShouldBeResolved(await _harness.ResolveAsync());
        }

        [Test]
        public void It_reads_discovery_every_time() => _harness.Handler.Requests.Should().HaveCount(2);
    }

    [TestFixture]
    public class Given_a_failed_discovery_read
    {
        private Harness _harness = null!;
        private DmsDiscoveryResolution _second = null!;

        [SetUp]
        public async Task Setup()
        {
            int calls = 0;
            _harness = new Harness(
                FakeDmsHandler.Answering(() =>
                    Interlocked.Increment(ref calls) == 1
                        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        : Ok(Document())
                )
            );
            ShouldFailWith(await _harness.ResolveAsync(), Code.ServiceUnavailable, 503);
            _second = await _harness.ResolveAsync();
        }

        [Test]
        public void It_does_not_cache_the_failure()
        {
            ShouldBeResolved(_second);
            _harness.Handler.Requests.Should().HaveCount(2);
        }
    }

    [TestFixture("token")]
    [TestFixture("projection")]
    public class Given_a_cached_template_that_fails_containment(string member)
    {
        private Harness _harness = null!;
        private DmsDiscoveryResolution _first = null!;

        [SetUp]
        public async Task Setup()
        {
            const string Outside = "https://dms.example.org/api-other/Tenant_255901/x";
            _harness = new Harness(
                FakeDmsHandler.Answering(() =>
                    Ok(member == "token" ? Document(tokenUrl: Outside) : Document(projectionUrl: Outside))
                )
            );
            _first = await _harness.ResolveAsync();
            await _harness.ResolveAsync();
        }

        [Test]
        public void It_is_invalid_discovery() => ShouldFailWith(_first, Code.DiscoveryInvalid, null);

        [Test]
        public void It_drops_the_cached_document() => _harness.Handler.Requests.Should().HaveCount(2);
    }

    [TestFixture]
    public class Given_the_production_registration_and_hostile_dms_values
    {
        private const string Hostile = "FORGED\r\nsecret-1440";
        private const string HostileJson = "FORGED\\r\\nsecret-1440";
        private RecordingLoggerProvider _recorder = null!;
        private List<DmsDiscoveryResolution> _resolutions = null!;

        [SetUp]
        public async Task Setup()
        {
            _recorder = new RecordingLoggerProvider();
            int call = 0;
            Func<HttpRequestMessage, HttpResponseMessage> respond = _ =>
                Interlocked.Increment(ref call) switch
                {
                    1 => DmsResponses.Text(
                        HttpStatusCode.NotFound,
                        $$"""{"type":"{{HostileJson}}","detail":"{{HostileJson}}","correlationId":"{{HostileJson}}"}""",
                        "application/problem+json"
                    ),
                    2 => throw new HttpRequestException(Hostile, new IOException(Hostile)),
                    _ => Ok(
                        Document(projectionUrl: "https://attacker.example/" + Uri.EscapeDataString(Hostile))
                    ),
                };

            ServiceCollection services = new();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(_recorder));
            services.AddDmsEducationOrganizationProjectionReader(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["DmsEducationOrganizationProjectionSettings:DmsBaseUrl"] = BaseUrl,
                            ["DmsEducationOrganizationProjectionSettings:Credentials:ClientId"] = "id",
                            ["DmsEducationOrganizationProjectionSettings:Credentials:ClientSecret"] =
                                "client-secret",
                        }
                    )
                    .Build()
            );
            services
                .AddHttpClient(DmsEducationOrganizationProjectionHttpClient.Name)
                .ConfigurePrimaryHttpMessageHandler(() =>
                    new FakeDmsHandler((request, _) => Task.FromResult(respond(request)))
                );

            await using ServiceProvider provider = services.BuildServiceProvider();
            IDmsDiscoveryClient client = provider.GetRequiredService<IDmsDiscoveryClient>();
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(10);
            _resolutions = [];
            for (int i = 0; i < 3; i++)
            {
                _resolutions.Add(
                    await client.ResolveAsync(Tenant, _contexts, deadline, CancellationToken.None)
                );
            }
        }

        [TearDown]
        public void TearDown() => _recorder.Dispose();

        [Test]
        public void It_classifies_each_read() =>
            _resolutions
                .Select(resolution => ShouldBeFailed(resolution).Code)
                .Should()
                .Equal(Code.TargetNotFound, Code.NetworkError, Code.DiscoveryInvalid);

        [Test]
        public void It_logs_only_the_projection_http_records() =>
            _recorder
                .Records.Select(record => record.Category)
                .Should()
                .OnlyContain(category => category == typeof(ProjectionHttpClientLogger).FullName);

        [Test]
        public void It_logs_no_dms_value_secret_or_exception_text_at_any_level() =>
            _recorder
                .Records.Should()
                .NotContain(record =>
                    record.AllText.Contains("FORGED")
                    || record.AllText.Contains("secret")
                    || record.AllText.Contains("attacker")
                );

        [Test]
        public void It_attaches_no_exception_to_any_record() =>
            _recorder
                .Records.Select(record => record.Exception)
                .Should()
                .AllSatisfy(exception => exception.Should().BeNull());

        [Test]
        public void It_carries_only_the_sanitized_problem_fields() =>
            (ShouldBeFailed(_resolutions[0]).ProblemType, ShouldBeFailed(_resolutions[0]).CorrelationId)
                .Should()
                .Be(("FORGEDsecret-1440", "FORGEDsecret-1440"));

        [Test]
        public void It_carries_no_url_or_line_break_in_the_failures() =>
            _resolutions
                .Select(resolution => ShouldBeFailed(resolution).ToString())
                .Should()
                .NotContain(text =>
                    text.Contains("attacker")
                    || text.Contains("example.org")
                    || text.Contains('\r')
                    || text.Contains('\n')
                );
    }
}
