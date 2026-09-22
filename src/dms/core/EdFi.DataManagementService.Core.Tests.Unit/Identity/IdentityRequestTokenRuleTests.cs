// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Identity;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Identity;

/// <summary>
/// Pins <see cref="IdentityRequestTokenRule.Evaluate" /> (design.md:722-768, story B5 rule half):
/// content rules, the 1024-character escaped ceiling, and the composed-path check against a supplied
/// <c>maxRequestLineSize</c>.
/// </summary>
[TestFixture]
public class IdentityRequestTokenRuleTests
{
    private const string DefaultPrefix = "/identity/v2/identities/results";
    private const int GenerousMaxRequestLineSize = int.MaxValue;

    // ---------------------------------------------------------------- content rules: accepted

    [Test]
    public void A_token_containing_an_already_percent_encoded_sequence_round_trips_unchanged()
    {
        var result = IdentityRequestTokenRule.Evaluate("50%25", DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeTrue();
        result.EscapedToken.Should().Be("50%2525");
        Uri.UnescapeDataString(result.EscapedToken).Should().Be("50%25");
        result.Reason.Should().Be(IdentityRequestTokenRejectionReason.None);
    }

    [Test]
    public void Three_dots_is_accepted_because_it_is_not_exactly_a_dot_segment()
    {
        var result = IdentityRequestTokenRule.Evaluate("...", DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeTrue();
    }

    [Test]
    public void A_token_containing_an_interior_dot_is_accepted()
    {
        var result = IdentityRequestTokenRule.Evaluate("a.b", DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeTrue();
    }

    [Test]
    public void A_token_starting_with_a_dot_is_accepted()
    {
        var result = IdentityRequestTokenRule.Evaluate(".hidden", DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeTrue();
    }

    // ---------------------------------------------------------------- content rules: rejected

    [Test]
    public void A_token_containing_a_forward_slash_is_rejected()
    {
        var result = IdentityRequestTokenRule.Evaluate("a/b", DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeFalse();
        result.Reason.Should().Be(IdentityRequestTokenRejectionReason.ContainsPathSeparator);
    }

    [Test]
    public void A_token_containing_a_backslash_is_rejected()
    {
        var result = IdentityRequestTokenRule.Evaluate("a\\b", DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeFalse();
        result.Reason.Should().Be(IdentityRequestTokenRejectionReason.ContainsPathSeparator);
    }

    [Test]
    public void A_token_containing_a_control_character_is_rejected()
    {
        var result = IdentityRequestTokenRule.Evaluate(
            "abc" + '\u0007' + "def",
            DefaultPrefix,
            GenerousMaxRequestLineSize
        );

        result.IsUsable.Should().BeFalse();
        result.Reason.Should().Be(IdentityRequestTokenRejectionReason.ContainsControlCharacter);
    }

    [Test]
    public void A_single_dot_is_rejected_as_a_dot_segment()
    {
        var result = IdentityRequestTokenRule.Evaluate(".", DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeFalse();
        result.Reason.Should().Be(IdentityRequestTokenRejectionReason.DotSegment);
    }

    [Test]
    public void A_double_dot_is_rejected_as_a_dot_segment()
    {
        var result = IdentityRequestTokenRule.Evaluate("..", DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeFalse();
        result.Reason.Should().Be(IdentityRequestTokenRejectionReason.DotSegment);
    }

    [Test]
    public void A_null_token_is_rejected_as_blank()
    {
        var result = IdentityRequestTokenRule.Evaluate(null, DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeFalse();
        result.Reason.Should().Be(IdentityRequestTokenRejectionReason.Blank);
    }

    [Test]
    public void An_empty_token_is_rejected_as_blank()
    {
        var result = IdentityRequestTokenRule.Evaluate("", DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeFalse();
        result.Reason.Should().Be(IdentityRequestTokenRejectionReason.Blank);
    }

    [Test]
    public void A_whitespace_only_token_is_rejected_as_blank()
    {
        var result = IdentityRequestTokenRule.Evaluate("  ", DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeFalse();
        result.Reason.Should().Be(IdentityRequestTokenRejectionReason.Blank);
    }

    [Test]
    public void A_token_containing_a_lone_surrogate_is_rejected_as_not_round_trippable()
    {
        // Uri.EscapeDataString encodes a lone (unpaired) UTF-16 surrogate as the UTF-8 replacement
        // character's escape sequence, so unescaping the result never reproduces the original token.
        var result = IdentityRequestTokenRule.Evaluate(
            "abc\uD800def",
            DefaultPrefix,
            GenerousMaxRequestLineSize
        );

        result.IsUsable.Should().BeFalse();
        result.Reason.Should().Be(IdentityRequestTokenRejectionReason.NotRoundTrippable);
    }

    // ---------------------------------------------------------------- 1024-character escaped ceiling

    [Test]
    public void A_token_that_escapes_to_exactly_1024_characters_is_accepted()
    {
        string token = new('a', 1024);

        var result = IdentityRequestTokenRule.Evaluate(token, DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeTrue();
        result.EscapedToken.Length.Should().Be(1024);
    }

    [Test]
    public void A_token_that_escapes_to_1025_characters_is_rejected()
    {
        string token = new('a', 1025);

        var result = IdentityRequestTokenRule.Evaluate(token, DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeFalse();
        result.Reason.Should().Be(IdentityRequestTokenRejectionReason.EscapedTokenTooLong);
    }

    [Test]
    public void A_token_whose_raw_length_is_under_1024_but_whose_escaped_length_exceeds_it_is_rejected()
    {
        // '%' escapes to "%25" (3x growth), so 700 raw characters escape to 2100.
        string token = new('%', 700);
        token.Length.Should().BeLessThan(1024);

        var result = IdentityRequestTokenRule.Evaluate(token, DefaultPrefix, GenerousMaxRequestLineSize);

        result.IsUsable.Should().BeFalse();
        result.EscapedToken.Length.Should().Be(2100);
        result.Reason.Should().Be(IdentityRequestTokenRejectionReason.EscapedTokenTooLong);
    }

    // ---------------------------------------------------------------- composed-path check

    [Test]
    public void A_token_that_fits_under_a_short_route_prefix_no_longer_fits_under_a_longer_tenant_qualified_prefix()
    {
        const string token = "tok1";
        const string shortPrefix = "/identity/v2/identities/results";
        const string longPrefix = "/tenant-a/255901/2026/identity/v2/identities/results";

        int shortRequestLineLength =
            shortPrefix.Length + 1 + token.Length + "GET ".Length + " HTTP/1.1".Length;

        // Comfortably above the short prefix's request line, but below the longer prefix's, because
        // longPrefix adds well over 5 characters versus shortPrefix.
        int maxRequestLineSize = shortRequestLineLength + 5;

        var acceptedUnderShortPrefix = IdentityRequestTokenRule.Evaluate(
            token,
            shortPrefix,
            maxRequestLineSize
        );
        var rejectedUnderLongPrefix = IdentityRequestTokenRule.Evaluate(
            token,
            longPrefix,
            maxRequestLineSize
        );

        acceptedUnderShortPrefix.IsUsable.Should().BeTrue();
        rejectedUnderLongPrefix.IsUsable.Should().BeFalse();
        rejectedUnderLongPrefix.Reason.Should().Be(IdentityRequestTokenRejectionReason.ComposedPathTooLong);
    }

    [Test]
    public void The_composed_request_line_length_includes_the_prefix_separator_and_method_and_version_overhead()
    {
        const string token = "tok";
        const string prefix = "/identity/v2/identities/results";

        int exactRequestLineLength = prefix.Length + 1 + token.Length + "GET ".Length + " HTTP/1.1".Length;

        var exactlyAtLimit = IdentityRequestTokenRule.Evaluate(token, prefix, exactRequestLineLength);
        var oneUnderLimit = IdentityRequestTokenRule.Evaluate(token, prefix, exactRequestLineLength - 1);

        exactlyAtLimit.IsUsable.Should().BeTrue();
        oneUnderLimit.IsUsable.Should().BeFalse();
        oneUnderLimit.Reason.Should().Be(IdentityRequestTokenRejectionReason.ComposedPathTooLong);
    }
}
