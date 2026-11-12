// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

[TestFixture("en-US")]
[TestFixture("tr-TR")]
[TestFixture("sv-SE")]
public class Given_VerificationReportFormatter_all_difference_kinds(string culture)
{
    private CultureInfo _originalCulture = null!;
    private EffectivePermissionsComparison _comparison = null!;
    private string _report = string.Empty;

    private const string ExpectedReport = """
        CMS authorization metadata differs from the expected effective permissions.
        Missing claim set: claim set "EmptyExpected"; expected ["EmptyExpected"]; actual <missing>.
        Missing claim set: claim set "Gone"; expected ["Gone"]; actual <missing>.
        Unexpected claim set: claim set "New"; expected <missing>; actual ["New"].
        Missing claim: claim set "Policy", claim "urn:Z-missing"; expected ["urn:Z-missing"]; actual <missing>.
        Missing action: claim set "Policy", claim "urn:common", action "Create"; expected ["Create"]; actual <missing>.
        Unexpected action: claim set "Policy", claim "urn:common", action "Delete"; expected <missing>; actual ["Delete"].
        Different strategies: claim set "Policy", claim "urn:common", action "Read"; expected ["Namespace", "Custom"]; actual ["Custom", "Namespace"].
        Different strategies: claim set "Policy", claim "urn:common", action "Update"; expected ["Old"]; actual ["New"].
        Unexpected claim: claim set "Policy", claim "urn:new"; expected <missing>; actual ["urn:new"].
        Unexpected claim set: claim set "emptyActual"; expected <missing>; actual ["emptyActual"].
        """;

    [SetUp]
    public void Setup()
    {
        _originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        ClaimSetMetadata expectedPolicy = new(
            "Policy",
            [new("urn:common", 1), new("urn:Z-missing", 1)],
            [
                new(
                    1,
                    [
                        EffectivePermissionsFixtures.Action("Update", "Old"),
                        EffectivePermissionsFixtures.Action("Read", "Namespace", "Custom"),
                        EffectivePermissionsFixtures.Action("Create"),
                    ]
                ),
            ]
        );
        ClaimSetMetadata actualPolicy = new(
            "POLICY",
            [new("urn:new", 2), new("urn:common", 2)],
            [
                new(
                    2,
                    [
                        EffectivePermissionsFixtures.Action("Delete"),
                        EffectivePermissionsFixtures.Action("Read", "Custom", "Namespace"),
                        EffectivePermissionsFixtures.Action("Update", "New"),
                    ]
                ),
            ]
        );
        _comparison = EffectivePermissionsComparer.Compare(
            EffectivePermissionsFixtures.Validate([
                expectedPolicy,
                new("EmptyExpected", [], []),
                EffectivePermissionsFixtures.Set(
                    "Gone",
                    "urn:child",
                    EffectivePermissionsFixtures.Action("Read")
                ),
            ]),
            EffectivePermissionsFixtures.Validate([
                new("emptyActual", [], []),
                actualPolicy,
                EffectivePermissionsFixtures.Set(
                    "New",
                    "urn:child",
                    EffectivePermissionsFixtures.Action("Read")
                ),
            ])
        );
        _report = VerificationReportFormatter.Format(_comparison);
    }

    [TearDown]
    public void TearDown() => CultureInfo.CurrentCulture = _originalCulture;

    [Test]
    public void It_reports_exact_expected_actual_direction_and_ordinal_context_order() =>
        _report.Should().Be(ExpectedReport);

    [Test]
    public void It_sorts_independently_of_difference_discovery_order() =>
        VerificationReportFormatter
            .Format(new(_comparison.Differences.Reverse().ToArray()))
            .Should()
            .Be(ExpectedReport);

    [Test]
    public void It_reports_missing_containers_once_without_descendant_noise() =>
        _comparison.Differences.Should().HaveCount(10);
}

[TestFixture]
public class Given_VerificationReportFormatter_empty_strategy_sequence
{
    private string _report = string.Empty;

    [SetUp]
    public void Setup()
    {
        _report = VerificationReportFormatter.Format(
            EffectivePermissionsFixtures.Compare(
                EffectivePermissionsFixtures.Set(
                    "Policy",
                    "urn:claim",
                    EffectivePermissionsFixtures.Action("Read"),
                    EffectivePermissionsFixtures.Action("Update", "Namespace")
                ),
                EffectivePermissionsFixtures.Set(
                    "Policy",
                    "urn:claim",
                    EffectivePermissionsFixtures.Action("Read", "Namespace"),
                    EffectivePermissionsFixtures.Action("Update")
                )
            )
        );
    }

    [Test]
    public void It_distinguishes_an_empty_sequence_from_a_missing_action() =>
        _report
            .Should()
            .Be(
                """
                CMS authorization metadata differs from the expected effective permissions.
                Different strategies: claim set "Policy", claim "urn:claim", action "Read"; expected []; actual ["Namespace"].
                Different strategies: claim set "Policy", claim "urn:claim", action "Update"; expected ["Namespace"]; actual [].
                """
            );
}

[TestFixture]
public class Given_VerificationReportFormatter_untrusted_names
{
    private EffectivePermissionsComparison _comparison = null!;
    private string _report = string.Empty;

    [SetUp]
    public void Setup()
    {
        _comparison = EffectivePermissionsFixtures.Compare(
            EffectivePermissionsFixtures.Set(
                "École\n",
                "urn:\"claim\"\u2028",
                EffectivePermissionsFixtures.Action("Read\t", "Old\u202E", @"literal\u000A")
            ),
            EffectivePermissionsFixtures.Set(
                "École\n",
                "urn:\"claim\"\u2028",
                EffectivePermissionsFixtures.Action("Read\t", "New\r", "東京\U000E0001")
            )
        );
        _report = VerificationReportFormatter.Format(_comparison);
    }

    [Test]
    public void It_escapes_control_format_and_quote_characters_without_losing_readable_unicode() =>
        _report
            .Should()
            .Be(
                """
                CMS authorization metadata differs from the expected effective permissions.
                Different strategies: claim set "École\u000A", claim "urn:\"claim\"\u2028", action "Read\u0009"; expected ["Old\u202E", "literal\\u000A"]; actual ["New\u000D", "東京\U000E0001"].
                """
            );

    [Test]
    public void It_keeps_original_values_in_structured_differences()
    {
        var difference = _comparison.Differences.Single();
        difference.ClaimSetName.Should().Be("École\n");
        difference.ResourceClaimName.Should().Be("urn:\"claim\"\u2028");
        difference.ActionName.Should().Be("Read\t");
        difference.Expected.Should().Equal("Old\u202E", @"literal\u000A");
        difference.Actual.Should().Equal("New\r", "東京\U000E0001");
    }
}

[TestFixture]
public class Given_VerificationReportFormatter_matching_metadata
{
    private string _report = string.Empty;

    [SetUp]
    public void Setup() =>
        _report = VerificationReportFormatter.Format(EffectivePermissionsComparer.Compare(new([]), new([])));

    [Test]
    public void It_limits_the_match_statement_to_authorization_metadata() =>
        _report.Should().Be("CMS authorization metadata matches the expected effective permissions.");
}
