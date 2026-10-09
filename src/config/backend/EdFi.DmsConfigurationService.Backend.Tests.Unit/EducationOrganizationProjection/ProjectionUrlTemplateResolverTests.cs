// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using FluentAssertions;
using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

public class ProjectionUrlTemplateResolverTests
{
    private const string BaseUrl = "https://dms.example.org/api";
    private const string Root = "https://dms.example.org/api/Tenant_255901";

    private static readonly Dictionary<string, string> _contexts = new()
    {
        ["districtId"] = "255901",
        ["schoolYear"] = "2026",
    };

    private static ProjectionUrlResolution Resolve(
        string template,
        IReadOnlyDictionary<string, string>? contexts = null,
        string baseUrl = BaseUrl
    ) => ProjectionUrlTemplateResolver.Resolve(template, contexts ?? _contexts, new Uri(baseUrl));

    private static string ResolvedUrl(ProjectionUrlResolution resolution) =>
        resolution.Should().BeOfType<ProjectionUrlResolution.Resolved>().Which.Url.AbsoluteUri;

    private static void ShouldBeRejected(ProjectionUrlResolution resolution, Code code) =>
        resolution.Should().Be(new ProjectionUrlResolution.Rejected(code));

    /// <summary>
    /// Test-case text with <c>&lt;high&gt;</c> and <c>&lt;low&gt;</c> replaced by lone surrogates. Attribute strings
    /// are stored as UTF-8, which turns a literal lone surrogate into U+FFFD before the test sees it.
    /// </summary>
    internal static string WithLoneSurrogates(string text) =>
        text.Replace("<high>", "\uD800").Replace("<low>", "\uDC00");

    [TestFixture]
    public class Given_the_discovery_templates
    {
        [TestCase(Root + "/{districtId}/{schoolYear}/oauth/token", Root + "/255901/2026/oauth/token")]
        [TestCase(
            Root + "/{districtId}/{schoolYear}/management/education-organizations",
            Root + "/255901/2026/management/education-organizations"
        )]
        [TestCase(Root + "/management/education-organizations", Root + "/management/education-organizations")]
        [TestCase(Root + "/d{districtId}-y{schoolYear}/x", Root + "/d255901-y2026/x")]
        public void It_fills_every_placeholder(string template, string expected) =>
            ResolvedUrl(Resolve(template)).Should().Be(expected);
    }

    [TestFixture]
    public class Given_context_keys_in_another_letter_case
    {
        [Test]
        public void It_matches_them_ignoring_case() =>
            ResolvedUrl(
                    Resolve(
                        Root + "/{districtId}/{schoolYear}/x",
                        new Dictionary<string, string> { ["DISTRICTID"] = "1", ["SchoolYear"] = "2026" }
                    )
                )
                .Should()
                .Be(Root + "/1/2026/x");

        [Test]
        public void It_prefers_the_exactly_matching_key() =>
            ResolvedUrl(
                    Resolve(
                        Root + "/{districtId}/x",
                        new Dictionary<string, string> { ["districtid"] = "9", ["districtId"] = "1" }
                    )
                )
                .Should()
                .Be(Root + "/1/x");

        [Test]
        public void It_accepts_case_variant_keys_with_one_value() =>
            ResolvedUrl(
                    Resolve(
                        Root + "/{districtId}/x",
                        new Dictionary<string, string> { ["DistrictID"] = "1", ["DISTRICTID"] = "1" }
                    )
                )
                .Should()
                .Be(Root + "/1/x");

        [Test]
        public void It_refuses_case_variant_keys_with_different_values() =>
            ShouldBeRejected(
                Resolve(
                    Root + "/{districtId}/x",
                    new Dictionary<string, string> { ["DistrictID"] = "1", ["DISTRICTID"] = "2" }
                ),
                Code.TargetNotRoutable
            );
    }

    [TestFixture]
    public class Given_context_values_that_need_encoding
    {
        [TestCase("a b", "a%20b")]
        [TestCase("a/b", "a%2Fb")]
        [TestCase("a\\b", "a%5Cb")]
        [TestCase("a?b#c", "a%3Fb%23c")]
        [TestCase("100%", "100%25")]
        [TestCase("%2E%2E", "%252E%252E")]
        [TestCase("{schoolYear}", "%7BschoolYear%7D")]
        [TestCase("é", "%C3%A9")]
        [TestCase("😀", "%F0%9F%98%80")]
        [TestCase("a.b", "a.b")]
        [TestCase("...", "...")]
        public void It_escapes_each_value_as_one_segment(string value, string expectedSegment) =>
            ResolvedUrl(
                    Resolve(
                        Root + "/{districtId}/x",
                        new Dictionary<string, string> { ["districtId"] = value }
                    )
                )
                .Should()
                .Be(Root + "/" + expectedSegment + "/x");
    }

    [TestFixture]
    public class Given_a_placeholder_no_context_can_fill
    {
        [TestCase(Root + "/{missing}/x", "districtId", "1")]
        [TestCase(Root + "/{districtId}/x", "districtId", "")]
        [TestCase(Root + "/{districtId}/x", "districtId", ".")]
        [TestCase(Root + "/{districtId}/x", "districtId", "..")]
        [TestCase(Root + "/{districtId}/x", "districtId", "a<high>b")]
        [TestCase(Root + "/{districtId}/x", "districtId", "a<low>b")]
        [TestCase(Root + "/{districtId}/x", "districtId", "a<low><high>b")]
        [TestCase(Root + "/{districtId}/x", "districtId", "ab<high>")]
        [TestCase("https://dms.example.org/api/{tenant}/x", "tenant", "Tenant_255901")]
        [TestCase("https://dms.example.org/api/{TENANT}/x", "TENANT", "Tenant_255901")]
        public void It_is_not_routable(string template, string key, string value) =>
            ShouldBeRejected(
                Resolve(template, new Dictionary<string, string> { [key] = WithLoneSurrogates(value) }),
                Code.TargetNotRoutable
            );

        [Test]
        public void It_routes_a_value_holding_a_surrogate_pair_or_a_literal_replacement_character() =>
            ResolvedUrl(
                    Resolve(
                        Root + "/{districtId}/x",
                        new Dictionary<string, string> { ["districtId"] = "😀�" }
                    )
                )
                .Should()
                .Be(Root + "/%F0%9F%98%80%EF%BF%BD/x");

        [Test]
        public void It_is_not_routable_when_values_form_a_dot_segment() =>
            ShouldBeRejected(
                Resolve(Root + "/{a}{b}/x", new Dictionary<string, string> { ["a"] = ".", ["b"] = "." }),
                Code.TargetNotRoutable
            );
    }

    [TestFixture]
    public class Given_a_malformed_template
    {
        [TestCase(Root + "/{districtId/x")]
        [TestCase(Root + "/districtId}/x")]
        [TestCase(Root + "/}{districtId}/x")]
        [TestCase(Root + "/{}/x")]
        [TestCase(Root + "/{{districtId}}/x")]
        [TestCase(Root + "/{a{districtId}/x")]
        [TestCase("https://{districtId}.example.org/api/x")]
        [TestCase("https://dms.example.org:{port}/api/x")]
        [TestCase("https://{districtId}@dms.example.org/api/x")]
        [TestCase(Root + "/x?dataStoreId=1")]
        [TestCase(Root + "/x#fragment")]
        [TestCase(Root + "\\x")]
        [TestCase("/api/Tenant_255901/x")]
        [TestCase("dms.example.org/api/x")]
        [TestCase("https://user@dms.example.org/api/x")]
        [TestCase("https://user:secret@dms.example.org/api/x")]
        [TestCase("https://dms.example.org:notaport/api/x")]
        public void It_is_invalid(string template) =>
            ShouldBeRejected(Resolve(template), Code.DiscoveryInvalid);
    }

    [TestFixture]
    public class Given_a_template_outside_the_base_url
    {
        [TestCase("https://dms.example.org/api-other/x")]
        [TestCase("https://dms.example.org/apix")]
        [TestCase("https://dms.example.org/x/api")]
        [TestCase("https://dms.example.org/")]
        [TestCase("https://dms.example.org")]
        [TestCase("https://dms.example.org/API/x")]
        [TestCase("https://dms.example.org//api/x")]
        [TestCase("http://dms.example.org/api/x")]
        [TestCase("https://other.example.org/api/x")]
        [TestCase("https://dms.example.org.attacker.example/api/x")]
        [TestCase("https://dms.example.org:8443/api/x")]
        [TestCase("https://dms.example.org/api/../x")]
        [TestCase("https://dms.example.org/api/./x")]
        [TestCase("https://dms.example.org/api/x/..")]
        [TestCase("https://dms.example.org/api/%2E%2E/x")]
        [TestCase("https://dms.example.org/api/%2e%2E/x")]
        [TestCase("https://dms.example.org/api/.%2E/x")]
        [TestCase("https://dms.example.org/api/%2e/x")]
        public void It_is_invalid(string template) =>
            ShouldBeRejected(Resolve(template), Code.DiscoveryInvalid);
    }

    [TestFixture]
    public class Given_a_template_under_the_base_url_spelled_differently
    {
        [TestCase(BaseUrl, "https://DMS.Example.ORG/api/x", "https://dms.example.org/api/x")]
        [TestCase(BaseUrl, "HTTPS://dms.example.org/api/x", "https://dms.example.org/api/x")]
        [TestCase(BaseUrl, "https://dms.example.org:443/api/x", "https://dms.example.org/api/x")]
        [TestCase(BaseUrl, "https://dms.example.org/%61pi/x", "https://dms.example.org/api/x")]
        [TestCase(BaseUrl, "https://dms.example.org/api", "https://dms.example.org/api")]
        [TestCase(BaseUrl, "https://dms.example.org/api/", "https://dms.example.org/api/")]
        [TestCase(
            "https://dms.example.org/api/",
            "https://dms.example.org/api/x",
            "https://dms.example.org/api/x"
        )]
        [TestCase("https://dms.example.org", "https://dms.example.org/x", "https://dms.example.org/x")]
        [TestCase(
            "https://dms.example.org/a%2fb",
            "https://dms.example.org/a%2Fb/x",
            "https://dms.example.org/a%2Fb/x"
        )]
        [TestCase(
            "http://dms.example.org/api",
            "http://dms.example.org:80/api/x",
            "http://dms.example.org/api/x"
        )]
        [TestCase(
            "http://dms.example.org:8080/api",
            "http://dms.example.org:8080/api/x",
            "http://dms.example.org:8080/api/x"
        )]
        public void It_is_contained(string baseUrl, string template, string expected) =>
            ResolvedUrl(Resolve(template, baseUrl: baseUrl)).Should().Be(expected);
    }

    [TestFixture]
    public class Given_a_template_with_more_than_one_problem
    {
        [Test]
        public void It_reports_a_foreign_origin_before_an_unfilled_placeholder() =>
            ShouldBeRejected(Resolve("https://other.example.org/api/{missing}/x"), Code.DiscoveryInvalid);

        [Test]
        public void It_reports_a_literal_dot_segment_before_an_unfilled_placeholder() =>
            ShouldBeRejected(Resolve(Root + "/{missing}/%2E%2E/x"), Code.DiscoveryInvalid);

        [Test]
        public void It_reports_a_malformed_placeholder_before_an_unfilled_one() =>
            ShouldBeRejected(Resolve(Root + "/{missing}/{broken"), Code.DiscoveryInvalid);

        [Test]
        public void It_reports_an_unfilled_placeholder_before_path_containment() =>
            ShouldBeRejected(Resolve("https://dms.example.org/api-other/{missing}"), Code.TargetNotRoutable);
    }
}
