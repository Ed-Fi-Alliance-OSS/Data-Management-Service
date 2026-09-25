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
public class IdentityRequestTokenRuleTests
{
    private const string DefaultPrefix = "/identity/v2/identities/results";
    private const int GenerousMaxRequestLineSize = int.MaxValue;

    // ---------------------------------------------------------------- content rules: accepted

    [TestFixture]
    public class Given_A_Token_Containing_An_Already_Percent_Encoded_Sequence
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            _result = IdentityRequestTokenRule.Evaluate("50%25", DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_usable()
        {
            _result.IsUsable.Should().BeTrue();
        }

        [Test]
        public void It_escapes_to_the_expected_value()
        {
            _result.EscapedToken.Should().Be("50%2525");
        }

        [Test]
        public void It_round_trips_back_to_the_original_via_unescape()
        {
            Uri.UnescapeDataString(_result.EscapedToken).Should().Be("50%25");
        }

        [Test]
        public void It_reports_no_rejection_reason()
        {
            _result.Reason.Should().Be(IdentityRequestTokenRejectionReason.None);
        }
    }

    [TestFixture]
    public class Given_Three_Dots
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            _result = IdentityRequestTokenRule.Evaluate("...", DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_usable_because_it_is_not_exactly_a_dot_segment()
        {
            _result.IsUsable.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_A_Token_With_An_Interior_Dot
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            _result = IdentityRequestTokenRule.Evaluate("a.b", DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_usable()
        {
            _result.IsUsable.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_A_Token_Starting_With_A_Dot
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            _result = IdentityRequestTokenRule.Evaluate(".hidden", DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_usable()
        {
            _result.IsUsable.Should().BeTrue();
        }
    }

    // ---------------------------------------------------------------- content rules: rejected

    [TestFixture]
    public class Given_A_Token_Containing_A_Forward_Slash
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            _result = IdentityRequestTokenRule.Evaluate("a/b", DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_not_usable()
        {
            _result.IsUsable.Should().BeFalse();
        }

        [Test]
        public void It_reports_ContainsPathSeparator()
        {
            _result.Reason.Should().Be(IdentityRequestTokenRejectionReason.ContainsPathSeparator);
        }
    }

    [TestFixture]
    public class Given_A_Token_Containing_A_Backslash
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            _result = IdentityRequestTokenRule.Evaluate("a\\b", DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_not_usable()
        {
            _result.IsUsable.Should().BeFalse();
        }

        [Test]
        public void It_reports_ContainsPathSeparator()
        {
            _result.Reason.Should().Be(IdentityRequestTokenRejectionReason.ContainsPathSeparator);
        }
    }

    [TestFixture]
    public class Given_A_Token_Containing_A_Control_Character
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            _result = IdentityRequestTokenRule.Evaluate(
                "abc" + '\u0007' + "def",
                DefaultPrefix,
                GenerousMaxRequestLineSize
            );
        }

        [Test]
        public void It_is_not_usable()
        {
            _result.IsUsable.Should().BeFalse();
        }

        [Test]
        public void It_reports_ContainsControlCharacter()
        {
            _result.Reason.Should().Be(IdentityRequestTokenRejectionReason.ContainsControlCharacter);
        }
    }

    [TestFixture]
    public class Given_A_Single_Dot
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            _result = IdentityRequestTokenRule.Evaluate(".", DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_not_usable()
        {
            _result.IsUsable.Should().BeFalse();
        }

        [Test]
        public void It_reports_DotSegment()
        {
            _result.Reason.Should().Be(IdentityRequestTokenRejectionReason.DotSegment);
        }
    }

    [TestFixture]
    public class Given_A_Double_Dot
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            _result = IdentityRequestTokenRule.Evaluate("..", DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_not_usable()
        {
            _result.IsUsable.Should().BeFalse();
        }

        [Test]
        public void It_reports_DotSegment()
        {
            _result.Reason.Should().Be(IdentityRequestTokenRejectionReason.DotSegment);
        }
    }

    [TestFixture]
    public class Given_A_Null_Token
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            _result = IdentityRequestTokenRule.Evaluate(null, DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_not_usable()
        {
            _result.IsUsable.Should().BeFalse();
        }

        [Test]
        public void It_reports_Blank()
        {
            _result.Reason.Should().Be(IdentityRequestTokenRejectionReason.Blank);
        }
    }

    [TestFixture]
    public class Given_An_Empty_Token
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            _result = IdentityRequestTokenRule.Evaluate("", DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_not_usable()
        {
            _result.IsUsable.Should().BeFalse();
        }

        [Test]
        public void It_reports_Blank()
        {
            _result.Reason.Should().Be(IdentityRequestTokenRejectionReason.Blank);
        }
    }

    [TestFixture]
    public class Given_A_Whitespace_Only_Token
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            _result = IdentityRequestTokenRule.Evaluate("  ", DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_not_usable()
        {
            _result.IsUsable.Should().BeFalse();
        }

        [Test]
        public void It_reports_Blank()
        {
            _result.Reason.Should().Be(IdentityRequestTokenRejectionReason.Blank);
        }
    }

    [TestFixture]
    public class Given_A_Token_Containing_A_Lone_Surrogate
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            // Uri.EscapeDataString encodes a lone (unpaired) UTF-16 surrogate as the UTF-8 replacement
            // character's escape sequence, so unescaping the result never reproduces the original token.
            _result = IdentityRequestTokenRule.Evaluate(
                "abc\uD800def",
                DefaultPrefix,
                GenerousMaxRequestLineSize
            );
        }

        [Test]
        public void It_is_not_usable()
        {
            _result.IsUsable.Should().BeFalse();
        }

        [Test]
        public void It_reports_NotRoundTrippable()
        {
            _result.Reason.Should().Be(IdentityRequestTokenRejectionReason.NotRoundTrippable);
        }
    }

    // ---------------------------------------------------------------- 1024-character escaped ceiling

    [TestFixture]
    public class Given_A_Token_That_Escapes_To_Exactly_1024_Characters
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            string token = new('a', 1024);

            _result = IdentityRequestTokenRule.Evaluate(token, DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_usable()
        {
            _result.IsUsable.Should().BeTrue();
        }

        [Test]
        public void It_escapes_to_1024_characters()
        {
            _result.EscapedToken.Length.Should().Be(1024);
        }
    }

    [TestFixture]
    public class Given_A_Token_That_Escapes_To_1025_Characters
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            string token = new('a', 1025);

            _result = IdentityRequestTokenRule.Evaluate(token, DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_not_usable()
        {
            _result.IsUsable.Should().BeFalse();
        }

        [Test]
        public void It_reports_EscapedTokenTooLong()
        {
            _result.Reason.Should().Be(IdentityRequestTokenRejectionReason.EscapedTokenTooLong);
        }
    }

    [TestFixture]
    public class Given_A_Token_Whose_Raw_Length_Is_Under_1024_But_Whose_Escaped_Length_Exceeds_It
    {
        private IdentityRequestTokenEvaluation _result;

        [SetUp]
        public void Setup()
        {
            // '%' escapes to "%25" (3x growth), so 700 raw characters escape to 2100.
            string token = new('%', 700);
            token.Length.Should().BeLessThan(1024);

            _result = IdentityRequestTokenRule.Evaluate(token, DefaultPrefix, GenerousMaxRequestLineSize);
        }

        [Test]
        public void It_is_not_usable()
        {
            _result.IsUsable.Should().BeFalse();
        }

        [Test]
        public void It_escapes_to_2100_characters()
        {
            _result.EscapedToken.Length.Should().Be(2100);
        }

        [Test]
        public void It_reports_EscapedTokenTooLong()
        {
            _result.Reason.Should().Be(IdentityRequestTokenRejectionReason.EscapedTokenTooLong);
        }
    }

    // ---------------------------------------------------------------- composed-path check

    [TestFixture]
    public class Given_A_Token_That_Fits_Under_A_Short_Prefix_But_Not_A_Longer_Tenant_Qualified_Prefix
    {
        private IdentityRequestTokenEvaluation _acceptedUnderShortPrefix;
        private IdentityRequestTokenEvaluation _rejectedUnderLongPrefix;

        [SetUp]
        public void Setup()
        {
            const string token = "tok1";
            const string shortPrefix = "/identity/v2/identities/results";
            const string longPrefix = "/tenant-a/255901/2026/identity/v2/identities/results";

            int shortRequestLineLength =
                shortPrefix.Length + 1 + token.Length + "GET ".Length + " HTTP/1.1".Length;

            // Comfortably above the short prefix's request line, but below the longer prefix's, because
            // longPrefix adds well over 5 characters versus shortPrefix.
            int maxRequestLineSize = shortRequestLineLength + 5;

            _acceptedUnderShortPrefix = IdentityRequestTokenRule.Evaluate(
                token,
                shortPrefix,
                maxRequestLineSize
            );
            _rejectedUnderLongPrefix = IdentityRequestTokenRule.Evaluate(
                token,
                longPrefix,
                maxRequestLineSize
            );
        }

        [Test]
        public void It_is_accepted_under_the_short_prefix()
        {
            _acceptedUnderShortPrefix.IsUsable.Should().BeTrue();
        }

        [Test]
        public void It_is_rejected_under_the_long_prefix()
        {
            _rejectedUnderLongPrefix.IsUsable.Should().BeFalse();
        }

        [Test]
        public void It_reports_ComposedPathTooLong_under_the_long_prefix()
        {
            _rejectedUnderLongPrefix
                .Reason.Should()
                .Be(IdentityRequestTokenRejectionReason.ComposedPathTooLong);
        }
    }

    [TestFixture]
    public class Given_The_Composed_Request_Line_Length_Boundary
    {
        private IdentityRequestTokenEvaluation _exactlyAtLimit;
        private IdentityRequestTokenEvaluation _oneUnderLimit;

        [SetUp]
        public void Setup()
        {
            const string token = "tok";
            const string prefix = "/identity/v2/identities/results";

            int exactRequestLineLength =
                prefix.Length + 1 + token.Length + "GET ".Length + " HTTP/1.1".Length;

            _exactlyAtLimit = IdentityRequestTokenRule.Evaluate(token, prefix, exactRequestLineLength);
            _oneUnderLimit = IdentityRequestTokenRule.Evaluate(token, prefix, exactRequestLineLength - 1);
        }

        [Test]
        public void It_is_accepted_exactly_at_the_limit()
        {
            _exactlyAtLimit.IsUsable.Should().BeTrue();
        }

        [Test]
        public void It_is_rejected_one_under_the_limit()
        {
            _oneUnderLimit.IsUsable.Should().BeFalse();
        }

        [Test]
        public void It_reports_ComposedPathTooLong_when_rejected()
        {
            _oneUnderLimit.Reason.Should().Be(IdentityRequestTokenRejectionReason.ComposedPathTooLong);
        }
    }
}
