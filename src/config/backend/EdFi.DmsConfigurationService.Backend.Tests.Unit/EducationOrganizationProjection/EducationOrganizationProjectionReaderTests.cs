// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection.ProjectionPages;
using Category = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCategory;
using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;
using Stage = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionStage;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

public class EducationOrganizationProjectionReaderTests
{
    private const string ProblemPrefix = "urn:ed-fi:api:education-organization-projection:";

    /// <summary>
    /// Real time allowed for a step the test has already released through the fake clock, a token or a handler. A
    /// correct reader finishes without waiting; the bound only turns a regression that leaves a read pending into a
    /// failure instead of a hung run.
    /// </summary>
    private static readonly TimeSpan _hangGuard = TimeSpan.FromSeconds(30);

    private static EducationOrganizationProjectionReadResult.Success ShouldSucceed(
        EducationOrganizationProjectionReadResult result
    ) => result.Should().BeOfType<EducationOrganizationProjectionReadResult.Success>().Subject;

    private static EducationOrganizationProjectionFailure ShouldFail(
        EducationOrganizationProjectionReadResult result
    ) => result.Should().BeOfType<EducationOrganizationProjectionReadResult.Failure>().Subject.Detail;

    /// <summary>A failure with the code's own category at the given stage and position.</summary>
    private static void ShouldFailWith(
        EducationOrganizationProjectionReadResult result,
        Code code,
        Stage stage,
        int? httpStatus,
        int pagesRead = 0,
        int restarts = 0
    )
    {
        EducationOrganizationProjectionFailure failure = ShouldFail(result);
        (
            failure.Code,
            failure.Category,
            failure.Stage,
            failure.HttpStatus,
            failure.PagesRead,
            failure.Restarts
        )
            .Should()
            .Be(
                (
                    code,
                    EducationOrganizationProjectionFailure.CategoryOf(code),
                    stage,
                    httpStatus,
                    pagesRead,
                    restarts
                )
            );
    }

    private static Func<HttpResponseMessage> Problem(HttpStatusCode status, string type) =>
        () => DmsResponses.Problem(status, type);

    private static Func<HttpResponseMessage> ProjectionChanged =>
        Problem(HttpStatusCode.Conflict, ProblemPrefix + "projection-changed");

    private static Func<HttpResponseMessage> InvalidCursor =>
        Problem(HttpStatusCode.BadRequest, ProblemPrefix + "invalid-cursor");

    [TestFixture]
    public class Given_a_set_over_three_pages
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = ProjectionReaderHarness.Serving(
                [
                    () => Page("c1", Item(1, Sea), Item(2, Esc, 1)),
                    () => Page("c2", Item(3, Lea, 2), Item(40, School, 3)),
                    () => Page(null, Item(41, School)),
                ],
                settings => settings.PageSize = 2
            );
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_returns_every_item_in_order() =>
            ShouldSucceed(_result)
                .Items.Should()
                .Equal(
                    new EducationOrganizationProjectionItem(1, "Name 1", null, Sea, null),
                    new EducationOrganizationProjectionItem(2, "Name 2", null, Esc, 1),
                    new EducationOrganizationProjectionItem(3, "Name 3", null, Lea, 2),
                    new EducationOrganizationProjectionItem(40, "Name 40", null, School, 3),
                    new EducationOrganizationProjectionItem(41, "Name 41", null, School, null)
                );

        [Test]
        public void It_reports_the_version_pages_and_restarts() =>
            (
                ShouldSucceed(_result).ContractVersion,
                ShouldSucceed(_result).PageCount,
                ShouldSucceed(_result).Restarts
            )
                .Should()
                .Be((ContractVersionV1, 3, 0));

        [Test]
        public void It_follows_each_cursor_verbatim() =>
            _harness
                .PageRequests.Select(request => Query(request, "cursor"))
                .Should()
                .Equal(null, "c1", "c2");

        [Test]
        public void It_repeats_the_store_limit_and_version_on_every_page() =>
            _harness
                .PageRequests.Select(request =>
                    (
                        Query(request, "dataStoreId"),
                        Query(request, "limit"),
                        Query(request, "contractVersion")
                    )
                )
                .Should()
                .AllBeEquivalentTo(("3788", "2", ContractVersionV1));

        [Test]
        public void It_sends_the_bearer_token_and_accepts_json() =>
            _harness
                .PageRequests.Select(request => (request.Method, request.Authorization, request.Accept))
                .Should()
                .AllBeEquivalentTo((HttpMethod.Get, "Bearer token-1", "application/json"));

        [Test]
        public void It_reads_discovery_and_a_token_once() =>
            (_harness.DiscoveryRequests.Count, _harness.TokenRequests.Count).Should().Be((1, 1));
    }

    [TestFixture]
    public class Given_an_empty_set
    {
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup() =>
            _result = await ProjectionReaderHarness.Serving([() => Page(null)]).ReadAsync();

        [Test]
        public void It_succeeds_with_no_items_after_one_page() =>
            (ShouldSucceed(_result).Items.Count, ShouldSucceed(_result).PageCount).Should().Be((0, 1));
    }

    [TestFixture]
    public class Given_ids_that_are_zero_or_negative
    {
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup() =>
            _result = await ProjectionReaderHarness
                .Serving([() => Page(null, Item(-5, Sea), Item(0, Esc, -5), Item(7, Lea, 0))])
                .ReadAsync();

        [Test]
        public void It_accepts_any_int64_id_in_ascending_order() =>
            ShouldSucceed(_result)
                .Items.Select(item => item.EducationOrganizationId)
                .Should()
                .Equal(-5, 0, 7);
    }

    [TestFixture]
    public class Given_a_parent_with_a_larger_id_on_a_later_page
    {
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup() =>
            _result = await ProjectionReaderHarness
                .Serving([() => Page("c1", Item(1, School, 9)), () => Page(null, Item(9, Lea))])
                .ReadAsync();

        [Test]
        public void It_resolves_the_parent_across_pages() =>
            ShouldSucceed(_result).Items.Select(item => item.ParentId).Should().Equal(9, null);
    }

    [TestFixture]
    public class Given_the_contract_examples
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _pages = null!;
        private EducationOrganizationProjectionReadResult _empty = null!;

        [SetUp]
        public async Task Setup()
        {
            // The Discovery fragment is a multi-tenant document whose templates carry two route qualifiers.
            _harness = new ProjectionReaderHarness(
                (call, request, _) =>
                    Task.FromResult(
                        DmsResponses.Text(
                            HttpStatusCode.OK,
                            Encoding.UTF8.GetString(
                                ProjectionContractExamples.Bytes(ExampleFor(call, request.RequestUri!))
                            )
                        )
                    ),
                settings =>
                {
                    settings.DmsBaseUrl = "http://localhost:8080";
                    settings.PageSize = 2;
                },
                discovery: () =>
                    DmsResponses.Text(
                        HttpStatusCode.OK,
                        Encoding.UTF8.GetString(ProjectionContractExamples.Bytes("discovery-fragment.json"))
                    )
            );
            Dictionary<string, string> contexts = new()
            {
                ["districtId"] = "255901",
                ["schoolYear"] = "2026",
            };
            _harness.Request = new("Tenant_255901", 3788, contexts);
            _pages = await _harness.ReadAsync();
            _harness.Request = new("Tenant_255901", 3789, contexts);
            _empty = await _harness.ReadAsync();
        }

        /// <summary>The empty example for store 3789; the two example pages, in order, for any other.</summary>
        private static string ExampleFor(int call, Uri pageUrl)
        {
            if (Query(pageUrl, "dataStoreId") == "3789")
            {
                return "empty.json";
            }
            return call == 1 ? "success-page.json" : "success-last-page.json";
        }

        [Test]
        public void It_reads_both_example_pages() =>
            ShouldSucceed(_pages)
                .Items.Should()
                .Equal(
                    new EducationOrganizationProjectionItem(
                        1,
                        "Example State Department of Education",
                        "ESDE",
                        Sea,
                        null
                    ),
                    new EducationOrganizationProjectionItem(
                        10,
                        "Region 10 Education Service Center",
                        "ESC 10",
                        Esc,
                        1
                    ),
                    new EducationOrganizationProjectionItem(900001, "Independent Academy", null, School, null)
                );

        [Test]
        public void It_reads_the_empty_example() => ShouldSucceed(_empty).Items.Should().BeEmpty();

        [Test]
        public void It_requests_the_resolved_projection_path() =>
            _harness
                .Handler.Requests.Select(request => request.Method + " " + request.Uri.AbsolutePath)
                .Distinct()
                .Should()
                .Equal(
                    "GET /Tenant_255901",
                    "POST /Tenant_255901/255901/2026/oauth/token",
                    "GET /Tenant_255901/255901/2026/management/education-organizations"
                );

        [Test]
        public void It_returns_the_example_cursor_verbatim() =>
            Query(_harness.PageRequests[1], "cursor")
                .Should()
                .Be(
                    "MSwzNzg4LDEwLEdheEJQTk5KeEhyQm1fU0g1eGRfTDl0M2FMb3ZORi1NUXBDN19jX1l2X28sMTc5MDk1MzIwMCw0NGNlM2RiYTc0ZWM4ZjhiYTE0ZGFjN2U5NDNmYWRmNw"
                );
    }

    /// <summary>
    /// Every checked-in problem example, and the non-problem 500 body, served for a first page: the reader assigns the
    /// category and code the contract's <c>x-ed-fi-cms-classification</c> gives its schema.
    /// </summary>
    [TestFixture]
    public class Given_each_contract_failure_example
    {
        private List<string> _mismatches = null!;
        private List<string> _replayed = null!;

        [SetUp]
        public async Task Setup()
        {
            _mismatches = [];
            _replayed = [];
            IReadOnlyList<ContractClassification> classifications =
                ProjectionContractExamples.Classifications();

            List<(string File, int Status, string MediaType, ContractClassification Expected)> cases = [];
            foreach (string file in ProjectionContractExamples.ProblemExampleFiles)
            {
                string body = Encoding.UTF8.GetString(ProjectionContractExamples.Bytes(file));
                System.Text.Json.Nodes.JsonNode problem = System.Text.Json.Nodes.JsonNode.Parse(body)!;
                string type = problem["type"]!.GetValue<string>();
                ContractClassification expected = classifications.Single(classification =>
                    classification.ProblemType == type
                );
                cases.Add((file, problem["status"]!.GetValue<int>(), "application/problem+json", expected));
            }
            cases.Add(
                (
                    "unexpected-condition.json",
                    500,
                    "application/json",
                    classifications.Single(classification =>
                        classification.Schema == "UnexpectedConditionBody"
                    )
                )
            );

            foreach ((string file, int status, string mediaType, ContractClassification expected) in cases)
            {
                string body = Encoding.UTF8.GetString(ProjectionContractExamples.Bytes(file));
                ProjectionReaderHarness harness = ProjectionReaderHarness.Serving([
                    () => DmsResponses.Text((HttpStatusCode)status, body, mediaType),
                ]);
                EducationOrganizationProjectionFailure failure = ShouldFail(await harness.ReadAsync());
                _replayed.Add(file);
                if (
                    failure.Code.ToString() != expected.Code
                    || failure.Category.ToString() != expected.Category
                    || failure.Stage != Stage.Page
                    || failure.HttpStatus != status
                )
                {
                    _mismatches.Add(
                        $"{file}: {failure.Category} {failure.Code}, contract {expected.Category} {expected.Code}"
                    );
                }
            }
        }

        [Test]
        public void It_classifies_each_example_as_the_contract_does() => _mismatches.Should().BeEmpty();

        [Test]
        public void It_replays_every_example() => _replayed.Should().HaveCount(23);
    }

    [TestFixture(301, null, Code.DiscoveryInvalid)]
    [TestFixture(302, null, Code.DiscoveryInvalid)]
    [TestFixture(307, null, Code.DiscoveryInvalid)]
    [TestFixture(308, null, Code.DiscoveryInvalid)]
    [TestFixture(201, null, Code.UnexpectedResponse)]
    [TestFixture(204, null, Code.UnexpectedResponse)]
    [TestFixture(400, "urn:ed-fi:api:bad-request:parameter-validation-failed", Code.InvalidRequest)]
    [TestFixture(400, "urn:ed-fi:api:bad-request", Code.InvalidRequest)]
    [TestFixture(400, null, Code.InvalidRequest)]
    [TestFixture(400, ProblemPrefix + "unsupported-contract-version", Code.UnsupportedContract)]
    [TestFixture(400, ProblemPrefix + "Unsupported-Contract-Version", Code.InvalidRequest)]
    [TestFixture(400, ProblemPrefix + "invalid-cursor", Code.InvalidRequest)]
    [TestFixture(403, null, Code.Forbidden)]
    [TestFixture(404, null, Code.TargetNotFound)]
    [TestFixture(404, "urn:ed-fi:api:not-found", Code.TargetNotFound)]
    [TestFixture(405, null, Code.UnexpectedResponse)]
    [TestFixture(409, ProblemPrefix + "target-schema-incompatible", Code.TargetSchemaIncompatible)]
    [TestFixture(409, ProblemPrefix + "target-provider-unsupported", Code.TargetSchemaIncompatible)]
    [TestFixture(409, ProblemPrefix + "projection-unsupported", Code.Unsupported)]
    [TestFixture(409, ProblemPrefix + "projection-too-large", Code.LimitExceeded)]
    [TestFixture(409, ProblemPrefix + "projection-data-invalid", Code.DataInvalid)]
    [TestFixture(409, ProblemPrefix + "Projection-Changed", Code.UnexpectedResponse)]
    [TestFixture(409, "urn:ed-fi:api:conflict", Code.UnexpectedResponse)]
    [TestFixture(409, null, Code.UnexpectedResponse)]
    [TestFixture(410, null, Code.UnexpectedResponse)]
    [TestFixture(422, null, Code.UnexpectedResponse)]
    [TestFixture(429, null, Code.RateLimited)]
    [TestFixture(500, "urn:ed-fi:api:system:configuration:security", Code.Forbidden)]
    [TestFixture(500, "urn:ed-fi:api:system:configuration:Security", Code.ServiceUnavailable)]
    [TestFixture(500, null, Code.ServiceUnavailable)]
    [TestFixture(502, null, Code.ServiceUnavailable)]
    [TestFixture(503, ProblemPrefix + "target-unavailable", Code.ServiceUnavailable)]
    public class Given_a_page_status_other_than_200(int status, string? type, Code expected)
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = ProjectionReaderHarness.Serving([
                type is null
                    ? () => DmsResponses.Text((HttpStatusCode)status, "{}")
                    : Problem((HttpStatusCode)status, type),
            ]);
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_classifies_the_status_and_type() =>
            ShouldFailWith(_result, expected, Stage.Page, status);

        [Test]
        public void It_does_not_restart() => _harness.PageRequests.Should().HaveCount(1);

        [Test]
        public void It_carries_the_problem_fields() =>
            (ShouldFail(_result).ProblemType, ShouldFail(_result).CorrelationId)
                .Should()
                .Be(type is null ? (null, null) : (type, "0HN:01"));
    }

    [TestFixture("malformed", "{\"type\":")]
    [TestFixture("not an object", "[\"urn:ed-fi:api:education-organization-projection:projection-changed\"]")]
    [TestFixture("type not a string", "{\"type\":7}")]
    [TestFixture("over 64 KB", null)]
    public class Given_a_409_whose_problem_body_has_no_usable_type(string kind, string? body)
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            string problem =
                body
                ?? $$"""{"type":"{{ProblemPrefix}}projection-changed","detail":"{{new string(
                    'x',
                    ProjectionHttpContent.MaxProblemBodyBytes
                )}}"}""";
            _harness = ProjectionReaderHarness.Serving([
                () => DmsResponses.Text(HttpStatusCode.Conflict, problem, "application/problem+json"),
            ]);
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_is_an_unexpected_response_without_a_restart() =>
            (ShouldFail(_result).Code, ShouldFail(_result).ProblemType, _harness.PageRequests.Count)
                .Should()
                .Be((Code.UnexpectedResponse, null, 1), kind);
    }

    [TestFixture]
    public class Given_the_projection_changed_type_outside_a_problem_document
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = ProjectionReaderHarness.Serving([
                () =>
                    DmsResponses.Text(
                        HttpStatusCode.Conflict,
                        $$"""{"type":"{{ProblemPrefix}}projection-changed"}"""
                    ),
            ]);
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_is_an_unexpected_response_without_a_restart() =>
            (ShouldFail(_result).Code, _harness.PageRequests.Count).Should().Be((Code.UnexpectedResponse, 1));
    }

    [TestFixture]
    public class Given_a_later_page_fails
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = ProjectionReaderHarness.Serving([
                () => Page("c1", Schools(1, 2)),
                () => Page("c2", Schools(3, 4)),
                Problem(HttpStatusCode.ServiceUnavailable, "urn:ed-fi:api:service-unavailable"),
            ]);
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_fails_without_items_and_counts_the_pages_read() =>
            ShouldFailWith(_result, Code.ServiceUnavailable, Stage.Page, 503, pagesRead: 2);
    }

    [TestFixture("empty continuation page")]
    [TestFixture("cursor with no items")]
    [TestFixture("same cursor again")]
    [TestFixture("earlier cursor again")]
    [TestFixture("other contract version")]
    [TestFixture("other data store")]
    [TestFixture("unknown member")]
    public class Given_a_page_that_breaks_the_read_rules(string rule)
    {
        private EducationOrganizationProjectionReadResult _result = null!;
        private int _pagesRead;

        [SetUp]
        public async Task Setup()
        {
            Func<HttpResponseMessage>[] pages = rule switch
            {
                "empty continuation page" => [() => Page("c1", Schools(1)), () => Page(null)],
                "cursor with no items" => [() => Page("c1")],
                "same cursor again" => [() => Page("c1", Schools(1)), () => Page("c1", Schools(2))],
                "earlier cursor again" =>
                [
                    () => Page("c1", Schools(1)),
                    () => Page("c2", Schools(2)),
                    () => Page("c1", Schools(3)),
                ],
                "other contract version" =>
                [
                    () =>
                        DmsResponses.Text(
                            HttpStatusCode.OK,
                            PageJson(null, Schools(1), contractVersion: "educationOrganizationProjection.v2")
                        ),
                ],
                "other data store" =>
                [
                    () => DmsResponses.Text(HttpStatusCode.OK, PageJson(null, Schools(1), dataStoreId: 3789)),
                ],
                _ =>
                [
                    () => Page("c1", Schools(1)),
                    () =>
                        DmsResponses.Text(
                            HttpStatusCode.OK,
                            PageJson(null, Schools(2)).Replace("\"items\"", "\"extra\":1,\"items\"")
                        ),
                ],
            };
            _pagesRead = rule switch
            {
                "empty continuation page" or "same cursor again" => 2,
                "earlier cursor again" => 3,
                "cursor with no items" => 1,
                "unknown member" => 1,
                _ => 0,
            };
            _result = await ProjectionReaderHarness.Serving(pages).ReadAsync();
        }

        [Test]
        public void It_is_a_malformed_response() =>
            ShouldFailWith(_result, Code.MalformedResponse, Stage.Page, 200, pagesRead: _pagesRead);
    }

    [TestFixture("unknown discriminator")]
    [TestFixture("discriminator in another case")]
    [TestFixture("empty name")]
    [TestFixture("equal ids on one page")]
    [TestFixture("descending ids on one page")]
    [TestFixture("equal ids across pages")]
    [TestFixture("descending ids across pages")]
    [TestFixture("own parent")]
    [TestFixture("parent not in the set")]
    public class Given_items_that_break_a_value_rule(string rule)
    {
        private EducationOrganizationProjectionReadResult _result = null!;
        private int _pagesRead;

        [SetUp]
        public async Task Setup()
        {
            Func<HttpResponseMessage>[] pages = rule switch
            {
                "unknown discriminator" => [() => Page(null, Item(1, "edfi.Course"))],
                "discriminator in another case" => [() => Page(null, Item(1, "edfi.school"))],
                "empty name" => [() => Page(null, Item(1, name: ""))],
                "equal ids on one page" => [() => Page(null, Schools(1, 1))],
                "descending ids on one page" => [() => Page(null, Schools(2, 1))],
                "equal ids across pages" => [() => Page("c1", Schools(1, 2)), () => Page(null, Schools(2))],
                "descending ids across pages" =>
                [
                    () => Page("c1", Schools(1, 5)),
                    () => Page(null, Schools(3)),
                ],
                "own parent" => [() => Page(null, Item(1, Lea, parentId: 1))],
                _ => [() => Page("c1", Item(1, School, parentId: 2)), () => Page(null, Item(3, Lea))],
            };
            _pagesRead = rule.EndsWith("across pages") || rule == "parent not in the set" ? 2 : 1;
            _result = await ProjectionReaderHarness.Serving(pages).ReadAsync();
        }

        [Test]
        public void It_is_invalid_data() =>
            ShouldFailWith(_result, Code.DataInvalid, Stage.Page, 200, pagesRead: _pagesRead);
    }

    [TestFixture]
    public class Given_a_whitespace_name_and_empty_short_name
    {
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup() =>
            _result = await ProjectionReaderHarness
                .Serving([() => Page(null, Item(1, name: " ", shortName: ""))])
                .ReadAsync();

        [Test]
        public void It_accepts_them() =>
            ShouldSucceed(_result).Items.Single().ShortNameOfInstitution.Should().Be("");
    }

    [TestFixture(2, true)]
    [TestFixture(3, false)]
    public class Given_a_page_limit(int maxPages, bool exceeded)
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = ProjectionReaderHarness.Serving(
                [() => Page("c1", Schools(1)), () => Page("c2", Schools(2)), () => Page(null, Schools(3))],
                settings => settings.MaxPages = maxPages
            );
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_fails_only_past_the_limit()
        {
            if (exceeded)
            {
                ShouldFailWith(_result, Code.LimitExceeded, Stage.Page, 200, pagesRead: 3);
            }
            else
            {
                ShouldSucceed(_result).PageCount.Should().Be(3);
            }
        }
    }

    [TestFixture(3, true)]
    [TestFixture(4, false)]
    public class Given_an_item_limit(int maxItems, bool exceeded)
    {
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup() =>
            _result = await ProjectionReaderHarness
                .Serving(
                    [() => Page("c1", Schools(1, 2)), () => Page(null, Schools(3, 4))],
                    settings => settings.MaxItems = maxItems
                )
                .ReadAsync();

        [Test]
        public void It_fails_only_past_the_limit()
        {
            if (exceeded)
            {
                ShouldFailWith(_result, Code.LimitExceeded, Stage.Page, 200, pagesRead: 2);
            }
            else
            {
                ShouldSucceed(_result).Items.Should().HaveCount(4);
            }
        }
    }

    /// <summary>
    /// Body caps in bytes, with a name of supplementary characters: each is 2 UTF-16 units and 4 UTF-8 bytes (raw) or
    /// 12 bytes (escaped).
    /// </summary>
    [TestFixture(0, true, false)]
    [TestFixture(0, false, false)]
    [TestFixture(1, true, true)]
    [TestFixture(1, false, true)]
    public class Given_a_body_around_the_size_limit(int overBy, bool declaredLength, bool exceeded)
    {
        private static readonly string _name = string.Concat(
            Enumerable.Repeat(char.ConvertFromUtf32(0x1F600), 50)
        );
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            byte[] body = Encoding.UTF8.GetBytes(
                PageJson(null, [Item(1)]).Replace("\"Name 1\"", "\"" + _name + "\"")
            );
            _result = await ProjectionReaderHarness
                .Serving(
                    [
                        () =>
                            declaredLength
                                ? new HttpResponseMessage(HttpStatusCode.OK)
                                {
                                    Content = new ByteArrayContent(body),
                                }
                                : DmsResponses.Stream(HttpStatusCode.OK, new UnknownLengthStream(body)),
                    ],
                    settings => settings.MaxResponseBodyBytes = body.Length - overBy
                )
                .ReadAsync();
        }

        [Test]
        public void It_counts_bytes_not_characters()
        {
            if (exceeded)
            {
                ShouldFailWith(_result, Code.LimitExceeded, Stage.Page, 200);
            }
            else
            {
                ShouldSucceed(_result).Items.Single().NameOfInstitution.Should().Be(_name);
            }
        }
    }

    [TestFixture]
    public class Given_the_set_changes_during_the_first_attempt
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            // Two pages per attempt with MaxPages 2: limits and seen cursors are per attempt, so the second attempt
            // may reuse c1 and read two more pages.
            _harness = ProjectionReaderHarness.Serving(
                [
                    () => Page("c1", Schools(1, 2)),
                    ProjectionChanged,
                    () => Page("c1", Schools(1, 2)),
                    () => Page(null, Schools(3, 4)),
                ],
                settings =>
                {
                    settings.MaxPages = 2;
                    settings.MaxItems = 4;
                }
            );
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_restarts_and_returns_only_the_second_attempt() =>
            (
                ShouldSucceed(_result).Items.Select(item => item.EducationOrganizationId),
                ShouldSucceed(_result).PageCount,
                ShouldSucceed(_result).Restarts
            )
                .Should()
                .BeEquivalentTo((new long[] { 1, 2, 3, 4 }, 2, 1));

        [Test]
        public void It_restarts_without_a_cursor() =>
            _harness
                .PageRequests.Select(request => Query(request, "cursor"))
                .Should()
                .Equal(null, "c1", null, "c1");
    }

    [TestFixture(0)]
    [TestFixture(2)]
    public class Given_the_set_keeps_changing(int maxWalkRestarts)
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = ProjectionReaderHarness.Serving(
                [ProjectionChanged],
                settings => settings.MaxWalkRestarts = maxWalkRestarts
            );
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_fails_transiently_after_the_last_restart() =>
            ShouldFailWith(
                _result,
                Code.ProjectionChanged,
                Stage.Page,
                409,
                pagesRead: 0,
                restarts: maxWalkRestarts
            );

        [Test]
        public void It_makes_one_attempt_more_than_the_restarts() =>
            _harness.PageRequests.Count.Should().Be(maxWalkRestarts + 1);
    }

    [TestFixture]
    public class Given_a_stale_cursor
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = ProjectionReaderHarness.Serving([
                () => Page("c1", Schools(1)),
                InvalidCursor,
                () => Page(null, Schools(1)),
            ]);
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_restarts_without_a_cursor_and_succeeds() =>
            (
                ShouldSucceed(_result).Restarts,
                _harness.PageRequests.Select(request => Query(request, "cursor"))
            )
                .Should()
                .BeEquivalentTo((1, new string?[] { null, "c1", null }));
    }

    [TestFixture]
    public class Given_invalid_cursor_without_a_cursor
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = ProjectionReaderHarness.Serving([InvalidCursor]);
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_fails_permanently_without_a_restart() =>
            (ShouldFail(_result).Code, ShouldFail(_result).Category, _harness.PageRequests.Count)
                .Should()
                .Be((Code.InvalidRequest, Category.Permanent, 1));
    }

    [TestFixture]
    public class Given_stale_cursors_and_changes_beyond_the_restarts
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            // Both kinds count against one restart budget of 2: change, stale cursor, then a stale cursor too many.
            _harness = ProjectionReaderHarness.Serving(
                [
                    () => Page("c1", Schools(1)),
                    ProjectionChanged,
                    () => Page("c1", Schools(1)),
                    InvalidCursor,
                    () => Page("c1", Schools(1)),
                    InvalidCursor,
                ],
                settings => settings.MaxWalkRestarts = 2
            );
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_fails_permanently_with_the_stale_cursor() =>
            ShouldFailWith(_result, Code.InvalidRequest, Stage.Page, 400, pagesRead: 1, restarts: 2);

        [Test]
        public void It_reads_three_attempts() => _harness.PageRequests.Should().HaveCount(6);
    }

    [TestFixture]
    public class Given_a_401_then_success
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _first = null!;
        private EducationOrganizationProjectionReadResult _second = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = ProjectionReaderHarness.Serving([
                () => Page("c1", Schools(1)),
                Problem(HttpStatusCode.Unauthorized, "urn:ed-fi:api:security:authentication"),
                () => Page("c2", Schools(2)),
                Problem(HttpStatusCode.Unauthorized, "urn:ed-fi:api:security:authentication"),
                () => Page(null, Schools(3)),
                () => Page(null, Schools(1)),
            ]);
            _first = await _harness.ReadAsync();
            _second = await _harness.ReadAsync();
        }

        [Test]
        public void It_refreshes_the_token_once_for_each_page_that_needs_it() =>
            _harness
                .PageRequests.Select(request => (Query(request, "cursor"), request.Authorization))
                .Should()
                .Equal(
                    (null, "Bearer token-1"),
                    ("c1", "Bearer token-1"),
                    ("c1", "Bearer token-2"),
                    ("c2", "Bearer token-2"),
                    ("c2", "Bearer token-3"),
                    (null, "Bearer token-3")
                );

        [Test]
        public void It_reads_the_whole_set() =>
            ShouldSucceed(_first).Items.Select(item => item.EducationOrganizationId).Should().Equal(1, 2, 3);

        [Test]
        public void It_keeps_the_cached_discovery_document() =>
            (ShouldSucceed(_second).PageCount, _harness.DiscoveryRequests.Count).Should().Be((1, 1));
    }

    [TestFixture]
    public class Given_a_page_refused_twice
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = ProjectionReaderHarness.Serving([
                () => Page("c1", Schools(1)),
                Problem(HttpStatusCode.Unauthorized, "urn:ed-fi:api:security:authentication"),
                Problem(HttpStatusCode.Unauthorized, "urn:ed-fi:api:security:authentication"),
                () => Page(null, Schools(1)),
            ]);
            _result = await _harness.ReadAsync();
            await _harness.ReadAsync();
        }

        [Test]
        public void It_fails_as_unauthorized() =>
            ShouldFailWith(_result, Code.Unauthorized, Stage.Page, 401, pagesRead: 1);

        [Test]
        public void It_sends_the_page_with_two_tokens_only() =>
            _harness
                .PageRequests.Take(3)
                .Select(request => request.Authorization)
                .Should()
                .Equal("Bearer token-1", "Bearer token-1", "Bearer token-2");

        [Test]
        public void It_drops_the_refused_token_and_the_discovery_document() =>
            (
                _harness.PageRequests[3].Authorization,
                _harness.TokenRequests.Count,
                _harness.DiscoveryRequests.Count
            )
                .Should()
                .Be(("Bearer token-3", 3, 2));
    }

    [TestFixture("page 3xx", Code.DiscoveryInvalid, Stage.Page)]
    [TestFixture("page 404", Code.TargetNotFound, Stage.Page)]
    [TestFixture("token 3xx", Code.DiscoveryInvalid, Stage.Token)]
    [TestFixture("token 400", Code.TokenRejected, Stage.Token)]
    [TestFixture("page 403", Code.Forbidden, Stage.Page)]
    [TestFixture("page 503", Code.ServiceUnavailable, Stage.Page)]
    [TestFixture("page malformed", Code.MalformedResponse, Stage.Page)]
    public class Given_a_failure_and_a_later_read(string failure, Code code, Stage stage)
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            int tokens = 0;
            Func<HttpResponseMessage> firstPage = failure switch
            {
                "page 3xx" => () => DmsResponses.Text(HttpStatusCode.Found, ""),
                "page 404" => Problem(HttpStatusCode.NotFound, ProblemPrefix + "target-not-found"),
                "page 403" => Problem(HttpStatusCode.Forbidden, "urn:ed-fi:api:security:authorization:"),
                "page 503" => Problem(HttpStatusCode.ServiceUnavailable, "urn:ed-fi:api:service-unavailable"),
                "page malformed" => () => DmsResponses.Text(HttpStatusCode.OK, "{}"),
                _ => () => Page(null, Schools(1)),
            };
            _harness = new ProjectionReaderHarness(
                (call, _, _) => Task.FromResult(call == 1 ? firstPage() : Page(null, Schools(1))),
                token: () =>
                    Interlocked.Increment(ref tokens) == 1
                        ? failure switch
                        {
                            "token 3xx" => DmsResponses.Text(HttpStatusCode.TemporaryRedirect, ""),
                            "token 400" => DmsResponses.Problem(HttpStatusCode.BadRequest, "urn:x"),
                            _ => TokenResponse(),
                        }
                        : TokenResponse()
            );
            _result = await _harness.ReadAsync();
            ShouldSucceed(await _harness.ReadAsync());
        }

        private static HttpResponseMessage TokenResponse() =>
            DmsResponses.Text(
                HttpStatusCode.OK,
                """{"access_token":"t","token_type":"bearer","expires_in":3600}"""
            );

        [Test]
        public void It_reports_the_failure() =>
            (ShouldFail(_result).Code, ShouldFail(_result).Stage).Should().Be((code, stage));

        [Test]
        public void It_reads_discovery_again_only_after_discovery_invalid_target_not_found_or_unauthorized() =>
            _harness
                .DiscoveryRequests.Count.Should()
                .Be(code is Code.DiscoveryInvalid or Code.TargetNotFound ? 2 : 1);
    }

    [TestFixture]
    public class Given_the_token_refresh_after_a_401_fails
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            int tokens = 0;
            _harness = new ProjectionReaderHarness(
                (call, _, _) =>
                    Task.FromResult(
                        call == 1
                            ? Page("c1", Schools(1))
                            : DmsResponses.Problem(
                                HttpStatusCode.Unauthorized,
                                "urn:ed-fi:api:security:authentication"
                            )
                    ),
                token: () =>
                    Interlocked.Increment(ref tokens) == 1
                        ? DmsResponses.Text(
                            HttpStatusCode.OK,
                            """{"access_token":"t1","token_type":"bearer","expires_in":3600}"""
                        )
                        : DmsResponses.Problem(
                            HttpStatusCode.Unauthorized,
                            "urn:ed-fi:api:security:authentication"
                        )
            );
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_fails_at_the_token_stage_with_the_pages_read() =>
            ShouldFailWith(_result, Code.TokenRejected, Stage.Token, 401, pagesRead: 1);
    }

    [TestFixture]
    public class Given_discovery_fails
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ProjectionReaderHarness(
                (_, _, _) => Task.FromResult(Page(null)),
                discovery: () => DmsResponses.Problem(HttpStatusCode.NotFound, "urn:ed-fi:api:not-found")
            );
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_returns_the_discovery_failure() =>
            ShouldFailWith(_result, Code.TargetNotFound, Stage.Discovery, 404);

        [Test]
        public void It_requests_no_token_or_page() =>
            (_harness.TokenRequests.Count, _harness.PageRequests.Count).Should().Be((0, 0));
    }

    [TestFixture]
    public class Given_a_transport_failure_on_a_later_page
    {
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup() =>
            _result = await new ProjectionReaderHarness(
                (call, _, _) =>
                    call == 1
                        ? Task.FromResult(Page("c1", Schools(1)))
                        : Task.FromException<HttpResponseMessage>(new HttpRequestException("reset"))
            ).ReadAsync();

        [Test]
        public void It_is_a_network_error() =>
            ShouldFailWith(_result, Code.NetworkError, Stage.Page, null, pagesRead: 1);
    }

    [TestFixture]
    public class Given_a_page_that_does_not_answer_in_time
    {
        private EducationOrganizationProjectionReadResult _result = null!;
        private bool _pendingJustBefore;

        [SetUp]
        public async Task Setup()
        {
            ProjectionReaderHarness harness = new(
                async (_, _, cancellationToken) =>
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                },
                settings => settings.PageRequestTimeoutSeconds = 60
            );
            Task<EducationOrganizationProjectionReadResult> pending = harness.ReadAsync();
            await harness.Handler.WaitForRequestsAsync(3).WaitAsync(_hangGuard);

            harness.Time.Advance(TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1));
            _pendingJustBefore = !pending.IsCompleted;
            harness.Time.Advance(TimeSpan.FromTicks(1));
            _result = await pending.WaitAsync(_hangGuard);
        }

        [Test]
        public void It_waits_for_the_page_timeout() => _pendingJustBefore.Should().BeTrue();

        [Test]
        public void It_times_out_at_the_page_stage() =>
            ShouldFailWith(_result, Code.Timeout, Stage.Page, null);
    }

    [TestFixture]
    public class Given_the_read_deadline_before_the_page_timeout
    {
        private EducationOrganizationProjectionReadResult _result = null!;
        private bool _pendingJustBefore;

        [SetUp]
        public async Task Setup()
        {
            // Discovery takes 20 s of a 50 s read, so the page has 30 s left of its 60.
            ProjectionReaderHarness harness = null!;
            harness = new ProjectionReaderHarness(
                async (_, _, cancellationToken) =>
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                },
                settings =>
                {
                    settings.TotalReadTimeoutSeconds = 50;
                    settings.DiscoveryTimeoutSeconds = 60;
                    settings.PageRequestTimeoutSeconds = 60;
                },
                discovery: () =>
                {
                    harness.Time.Advance(TimeSpan.FromSeconds(20));
                    return DmsResponses.Text(HttpStatusCode.OK, ProjectionReaderHarness.DiscoveryDocument);
                }
            );
            Task<EducationOrganizationProjectionReadResult> pending = harness.ReadAsync();
            await harness.Handler.WaitForRequestsAsync(3).WaitAsync(_hangGuard);

            harness.Time.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
            _pendingJustBefore = !pending.IsCompleted;
            harness.Time.Advance(TimeSpan.FromTicks(1));
            _result = await pending.WaitAsync(_hangGuard);
        }

        [Test]
        public void It_waits_until_the_read_deadline() => _pendingJustBefore.Should().BeTrue();

        [Test]
        public void It_times_out_at_the_page_stage() =>
            ShouldFailWith(_result, Code.Timeout, Stage.Page, null);
    }

    [TestFixture]
    public class Given_discovery_and_the_token_use_up_the_read_deadline
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            // Each stage is within its own timeout; together they pass the 100 s read deadline set at the start.
            _harness = null!;
            _harness = new ProjectionReaderHarness(
                (_, _, _) => Task.FromResult(Page(null)),
                settings =>
                {
                    settings.TotalReadTimeoutSeconds = 100;
                    settings.DiscoveryTimeoutSeconds = 300;
                    settings.TokenRequestTimeoutSeconds = 300;
                },
                discovery: () =>
                {
                    _harness.Time.Advance(TimeSpan.FromSeconds(60));
                    return DmsResponses.Text(HttpStatusCode.OK, ProjectionReaderHarness.DiscoveryDocument);
                },
                token: () =>
                {
                    _harness.Time.Advance(TimeSpan.FromSeconds(40));
                    return DmsResponses.Text(
                        HttpStatusCode.OK,
                        """{"access_token":"t","token_type":"bearer","expires_in":3600}"""
                    );
                }
            );
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_times_out_at_the_token_stage() =>
            ShouldFailWith(_result, Code.Timeout, Stage.Token, null);

        [Test]
        public void It_requests_no_page() => _harness.PageRequests.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_restarts_that_use_up_the_read_deadline
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            // Every attempt takes 40 s and the set always changes; the third attempt's page passes the 100 s deadline.
            _harness = null!;
            _harness = new ProjectionReaderHarness(
                (_, _, _) =>
                {
                    _harness.Time.Advance(TimeSpan.FromSeconds(40));
                    return Task.FromResult(ProjectionChanged());
                },
                settings =>
                {
                    settings.TotalReadTimeoutSeconds = 100;
                    settings.PageRequestTimeoutSeconds = 300;
                    settings.MaxWalkRestarts = 10;
                }
            );
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_times_out_with_the_restarts_made() =>
            ShouldFailWith(_result, Code.Timeout, Stage.Page, null, restarts: 2);

        [Test]
        public void It_stops_at_the_deadline() => _harness.PageRequests.Should().HaveCount(3);
    }

    [TestFixture(false)]
    [TestFixture(true)]
    public class Given_the_read_deadline_passes_while_a_token_is_issued(bool callerCancelsToo)
    {
        private CancellationTokenSource _caller = null!;
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult? _result;
        private Exception? _exception;

        /// <summary>Issues a token, moving the clock to the read deadline and running <c>alsoDo</c> as it does.</summary>
        private sealed class SlowTokenProvider(ProjectionReaderHarness harness, Action alsoDo)
            : IProjectionServiceTokenProvider
        {
            public Task<ProjectionServiceTokenResult> GetTokenAsync(
                string? tenantName,
                Uri tokenUrl,
                DateTimeOffset readDeadline,
                CancellationToken cancellationToken
            )
            {
                harness.Time.SetUtcNow(readDeadline);
                alsoDo();
                return Task.FromResult<ProjectionServiceTokenResult>(
                    new ProjectionServiceTokenResult.Issued(new ProjectionServiceToken("t"))
                );
            }

            public void Invalidate(string? tenantName, ProjectionServiceToken token) { }
        }

        [SetUp]
        public async Task Setup()
        {
            _caller = new CancellationTokenSource();
            _harness = new ProjectionReaderHarness(
                (_, _, _) => Task.FromResult(Page(null)),
                tokenProvider: harness => new SlowTokenProvider(
                    harness,
                    () =>
                    {
                        if (callerCancelsToo)
                        {
                            _caller.Cancel();
                        }
                    }
                )
            );
            try
            {
                _result = await _harness.ReadAsync(_caller.Token);
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [TearDown]
        public void TearDown() => _caller.Dispose();

        [Test]
        public void It_times_out_at_the_page_stage_unless_the_caller_cancelled()
        {
            if (callerCancelsToo)
            {
                _exception
                    .Should()
                    .BeAssignableTo<OperationCanceledException>()
                    .Which.CancellationToken.Should()
                    .Be(_caller.Token);
            }
            else
            {
                ShouldFailWith(_result!, Code.Timeout, Stage.Page, null);
            }
        }

        [Test]
        public void It_requests_no_page() => _harness.PageRequests.Should().BeEmpty();
    }

    [TestFixture(409, ProblemPrefix + "invalid-cursor", Code.UnexpectedResponse)]
    [TestFixture(400, ProblemPrefix + "projection-changed", Code.InvalidRequest)]
    [TestFixture(400, ProblemPrefix + "Invalid-Cursor", Code.InvalidRequest)]
    public class Given_a_restart_type_with_another_status_after_a_cursor(
        int status,
        string type,
        Code expected
    )
    {
        private ProjectionReaderHarness _harness = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = ProjectionReaderHarness.Serving([
                () => Page("c1", Schools(1)),
                Problem((HttpStatusCode)status, type),
            ]);
            _result = await _harness.ReadAsync();
        }

        [Test]
        public void It_fails_without_a_restart() =>
            (ShouldFail(_result).Code, ShouldFail(_result).Restarts, _harness.PageRequests.Count)
                .Should()
                .Be((expected, 0, 2));
    }

    [TestFixture]
    public class Given_caller_cancellation_while_a_page_is_outstanding
    {
        private CancellationTokenSource _caller = null!;
        private ProjectionReaderHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _caller = new CancellationTokenSource();
            _harness = new ProjectionReaderHarness(
                async (call, _, cancellationToken) =>
                {
                    if (call == 1)
                    {
                        return Page("c1", Schools(1));
                    }
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                }
            );
            Task<EducationOrganizationProjectionReadResult> pending = _harness.ReadAsync(_caller.Token);
            await _harness.Handler.WaitForRequestsAsync(4).WaitAsync(_hangGuard);

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

        [Test]
        public void It_logs_no_outcome() =>
            _harness
                .Recorder.Records.Should()
                .NotContain(record =>
                    record.Category == typeof(EducationOrganizationProjectionReader).FullName
                );
    }

    [TestFixture("success")]
    [TestFixture("409")]
    [TestFixture("401")]
    public class Given_a_page_that_ends_after_the_caller_cancels_and_the_timeout_passes(string outcome)
    {
        private CancellationTokenSource _caller = null!;
        private ProjectionReaderHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _caller = new CancellationTokenSource();
            _harness = null!;
            _harness = new ProjectionReaderHarness(
                (_, _, _) =>
                {
                    _caller.Cancel();
                    _harness.Time.Advance(TimeSpan.FromSeconds(600));
                    return Task.FromResult(
                        outcome switch
                        {
                            "success" => Page(null, Schools(1)),
                            "409" => ProjectionChanged(),
                            _ => DmsResponses.Problem(
                                HttpStatusCode.Unauthorized,
                                "urn:ed-fi:api:security:authentication"
                            ),
                        }
                    );
                }
            );
            try
            {
                await _harness.ReadAsync(_caller.Token).WaitAsync(_hangGuard);
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

        [Test]
        public void It_sends_nothing_more() => _harness.PageRequests.Should().HaveCount(1);
    }

    [TestFixture]
    public class Given_a_caller_token_already_cancelled
    {
        private ProjectionReaderHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ProjectionReaderHarness((_, _, _) => Task.FromResult(Page(null)));
            try
            {
                await _harness.ReadAsync(new CancellationToken(canceled: true));
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [Test]
        public void It_throws_before_any_request() =>
            (_exception is OperationCanceledException, _harness.Handler.Requests.Count)
                .Should()
                .Be((true, 0));
    }

    [TestFixture]
    public class Given_a_page_url
    {
        [Test]
        public void It_keeps_the_path_and_escapes_the_values() =>
            EducationOrganizationProjectionReader
                .PageUrl(
                    new Uri("https://dms.example.org:8443/api/T%20one/management/education-organizations"),
                    7,
                    2000,
                    "v 1&x=#",
                    "a+b/c="
                )
                .AbsoluteUri.Should()
                .Be(
                    "https://dms.example.org:8443/api/T%20one/management/education-organizations?dataStoreId=7&limit=2000&contractVersion=v%201%26x%3D%23&cursor=a%2Bb%2Fc%3D"
                );

        [Test]
        public void It_sends_no_cursor_on_a_first_page() =>
            EducationOrganizationProjectionReader
                .PageUrl(new Uri("http://h/p"), 1, 2, "v", null)
                .Query.Should()
                .Be("?dataStoreId=1&limit=2&contractVersion=v");
    }

    [TestFixture]
    public class Given_the_production_registration_and_hostile_values
    {
        private const string Tenant = "Tenant\r\n<b>FORGED";
        private const string Cursor = "SECRET-CURSOR-1440";
        private const string AccessToken = "SECRET-ACCESS-TOKEN-1440";
        private const string ClientSecret = "client-secret-1440";
        private RecordingLoggerProvider _recorder = null!;
        private List<EducationOrganizationProjectionReadResult> _results = null!;

        private List<RecordedLog> ReaderRecords =>
            [
                .. _recorder.Records.Where(record =>
                    record.Category == typeof(EducationOrganizationProjectionReader).FullName
                ),
            ];

        [SetUp]
        public async Task Setup()
        {
            _recorder = new RecordingLoggerProvider();
            int pageCalls = 0;
            HttpResponseMessage Respond(HttpRequestMessage request)
            {
                if (request.Method == HttpMethod.Post)
                {
                    return DmsResponses.Text(
                        HttpStatusCode.OK,
                        $$"""{"access_token":"{{AccessToken}}","token_type":"bearer","expires_in":3600}"""
                    );
                }
                if (!ProjectionPages.IsPageRequest(request.RequestUri!))
                {
                    return DmsResponses.Text(HttpStatusCode.OK, ProjectionReaderHarness.DiscoveryDocument);
                }
                return Interlocked.Increment(ref pageCalls) switch
                {
                    1 => Page(Cursor, Schools(1)),
                    2 => DmsResponses.Text(
                        HttpStatusCode.Conflict,
                        """{"type":"FORGED\r\n<b>","detail":"FORGED\r\n<b>","correlationId":"c-1\r\n<b>"}""",
                        "application/problem+json"
                    ),
                    _ => Page(null, Schools(1)),
                };
            }

            ServiceCollection services = new();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(_recorder));
            services.AddDmsEducationOrganizationProjectionReader(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["DmsEducationOrganizationProjectionSettings:DmsBaseUrl"] =
                                ProjectionReaderHarness.BaseUrl,
                            ["DmsEducationOrganizationProjectionSettings:Credentials:ClientId"] = "client",
                            ["DmsEducationOrganizationProjectionSettings:Credentials:ClientSecret"] =
                                ClientSecret,
                        }
                    )
                    .Build()
            );
            services
                .AddHttpClient(DmsEducationOrganizationProjectionHttpClient.Name)
                .ConfigurePrimaryHttpMessageHandler(() =>
                    new FakeDmsHandler((request, _) => Task.FromResult(Respond(request)))
                );

            await using ServiceProvider provider = services.BuildServiceProvider();
            IEducationOrganizationProjectionReader reader =
                provider.GetRequiredService<IEducationOrganizationProjectionReader>();
            EducationOrganizationProjectionReadRequest request = new(
                Tenant,
                DataStoreId,
                new Dictionary<string, string> { ["districtId"] = "255901" }
            );
            _results = [];
            for (int i = 0; i < 2; i++)
            {
                _results.Add(await reader.ReadAllAsync(request, CancellationToken.None));
            }
        }

        [TearDown]
        public void TearDown() => _recorder.Dispose();

        [Test]
        public void It_fails_then_succeeds() =>
            (ShouldFail(_results[0]).Code, ShouldSucceed(_results[1]).Items.Count)
                .Should()
                .Be((Code.UnexpectedResponse, 1));

        [Test]
        public void It_logs_only_the_reader_and_projection_http_records() =>
            _recorder
                .Records.Select(record => record.Category)
                .Distinct()
                .Should()
                .BeEquivalentTo(
                    typeof(ProjectionHttpClientLogger).FullName,
                    typeof(EducationOrganizationProjectionReader).FullName
                );

        [Test]
        public void It_logs_a_warning_for_the_failure_and_information_for_the_success() =>
            ReaderRecords
                .Select(record => record.Level)
                .Should()
                .Equal(LogLevel.Warning, LogLevel.Information);

        [Test]
        public void It_logs_the_sanitized_failure_fields() =>
            ReaderRecords[0]
                .State.Where(pair =>
                    pair.Key is "Tenant" or "ProblemType" or "CorrelationId" or "Code" or "PagesRead"
                )
                .Select(pair => $"{pair.Key}={pair.Value}")
                .Should()
                .BeEquivalentTo(
                    "Tenant=TenantbFORGED",
                    "ProblemType=FORGEDb",
                    "CorrelationId=c-1b",
                    "Code=UnexpectedResponse",
                    "PagesRead=1"
                );

        [Test]
        public void It_logs_no_cursor_token_secret_or_markup_at_any_level() =>
            _recorder
                .Records.Should()
                .NotContain(record =>
                    record.AllText.Contains(Cursor)
                    || record.AllText.Contains(AccessToken)
                    || record.AllText.Contains(ClientSecret)
                    || record.AllText.Contains("Bearer")
                    || record.AllText.Contains('\r')
                    || record.AllText.Contains('<')
                    || record.Message.Contains('\n')
                    || record.State.Any(pair => $"{pair.Value}".Contains('\n'))
                );

        [Test]
        public void It_attaches_no_exception_to_any_record() =>
            _recorder
                .Records.Select(record => record.Exception)
                .Should()
                .AllSatisfy(exception => exception.Should().BeNull());

        [Test]
        public void It_keeps_the_token_and_cursor_out_of_every_result_text() =>
            _results
                .Select(result => result.ToString())
                .Should()
                .NotContain(text =>
                    text.Contains(AccessToken) || text.Contains(Cursor) || text.Contains('\n')
                );
    }
}
