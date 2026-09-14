// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Utilities;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Utilities;

[TestFixture]
public class LoggingSanitizerTests
{
    [TestFixture]
    public class Given_SanitizeForLogging_With_Line_Endings : LoggingSanitizerTests
    {
        [Test]
        public void It_removes_line_endings_before_returning_log_values()
        {
            LoggingSanitizer.SanitizeForLogging("trace\r\nid\nwith\runsafe").Should().Be("traceidwithunsafe");
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationId_With_Control_Characters : LoggingSanitizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = LoggingSanitizer.SanitizeCorrelationId("tr\race\nid\twith\0nulls");
        }

        [Test]
        public void It_removes_every_control_character()
        {
            _result.Should().Be("traceidwithnulls");
        }

        [Test]
        public void It_leaves_no_line_ending_that_could_forge_a_log_line()
        {
            _result.Should().NotContain("\r").And.NotContain("\n");
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationId_With_Upstream_Punctuation : LoggingSanitizerTests
    {
        // The single most important assertion for FR-LOG-3: these characters are common in
        // upstream identifier schemes (base64, W3C traceparent, JSON-ish keys, RFC 5322
        // addresses) and the stricter Method/Path allowlist would strip all of them.
        private const string UpstreamId = "a+b=c{d}e@f|g,h#i(j)k[l]m<n>o\"p'q";

        [Test]
        public void It_preserves_printable_punctuation()
        {
            LoggingSanitizer.SanitizeCorrelationId(UpstreamId).Should().Be(UpstreamId);
        }

        [Test]
        public void It_is_broader_than_the_strict_method_and_path_allowlist()
        {
            LoggingSanitizer.SanitizeForLogging(UpstreamId).Should().NotBe(UpstreamId);
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationId_With_Unicode_Line_Separators : LoggingSanitizerTests
    {
        [Test]
        public void It_removes_line_and_paragraph_separators_that_are_not_control_characters()
        {
            // U+2028 and U+2029 are Unicode categories Zl and Zp, so char.IsControl is false
            // for both and the allowlist predicate admits them. They are removed by the
            // ReplaceLineEndings call that opens LogSanitizer's shared Sanitize helper,
            // which is therefore load-bearing rather than redundant: deleting it as dead work
            // would re-admit two characters that break a line-oriented log consumer. This
            // test is what pins that call in place.
            LoggingSanitizer.SanitizeCorrelationId("trace\u2028id\u2029end").Should().Be("traceidend");
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationId_With_Bidirectional_Format_Characters : LoggingSanitizerTests
    {
        // U+202A-U+202E and U+2066-U+2069 are Unicode category Cf, so char.IsControl is false
        // for all of them and they were preserved before this rule. They are removed now: a
        // bidirectional override renders the remainder of a log line right-to-left in a viewer,
        // so the ID an operator reads is not the ID that is stored, which defeats the single
        // guarantee a correlation ID carries.
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            // RIGHT-TO-LEFT OVERRIDE planted mid-ID, the classic display-spoofing shape.
            _result = LoggingSanitizer.SanitizeCorrelationId("req-\u202E12345-tenantA");
        }

        [Test]
        public void It_removes_the_right_to_left_override()
        {
            _result.Should().Be("req-12345-tenantA");
        }

        [Test]
        public void It_removes_every_bidirectional_embedding_override_and_isolate()
        {
            LoggingSanitizer
                .SanitizeCorrelationId("a\u202Ab\u202Bc\u202Cd\u202De\u202Ef\u2066g\u2067h\u2068i\u2069j")
                .Should()
                .Be("abcdefghij");
        }

        [Test]
        public void It_returns_empty_string_when_the_value_is_only_bidirectional_controls()
        {
            LoggingSanitizer.SanitizeCorrelationId("\u202E\u202D\u2066\u2069").Should().Be(string.Empty);
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationId_With_Zero_Width_Characters : LoggingSanitizerTests
    {
        // Zero-width and invisible format characters make two visually identical correlation IDs
        // distinct strings, so an ID copied out of a response body silently fails to match the
        // one in the logs. All are category Cf.
        [Test]
        public void It_removes_zero_width_and_invisible_format_characters()
        {
            LoggingSanitizer
                .SanitizeCorrelationId("tr\u200Ba\u200Cc\u200De\uFEFFi\u00ADd\u2060X\u200E\u200F")
                .Should()
                .Be("traceidX");
        }

        [Test]
        public void It_makes_two_visually_identical_ids_the_same_string()
        {
            LoggingSanitizer
                .SanitizeCorrelationId("trace-\u200B123")
                .Should()
                .Be(LoggingSanitizer.SanitizeCorrelationId("trace-123"));
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationId_With_Non_Ascii_Characters : LoggingSanitizerTests
    {
        [Test]
        public void It_preserves_non_ascii_printable_characters()
        {
            LoggingSanitizer
                .SanitizeCorrelationId("trace-Ωμέγα-日本語-ñ")
                .Should()
                .Be("trace-Ωμέγα-日本語-ñ");
        }

        [Test]
        public void It_preserves_internal_whitespace_including_no_break_space()
        {
            // SPACE and U+00A0 are category Zs, not Cf, so the format-character rule must not
            // reach them. FR-LOG-3 forbids narrowing this allowlist toward alphanumerics.
            LoggingSanitizer
                .SanitizeCorrelationId("trace id\u00A0with spaces")
                .Should()
                .Be("trace id\u00A0with spaces");
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationId_Applied_Twice : LoggingSanitizerTests
    {
        [Test]
        public void It_is_idempotent_for_a_value_mixing_removed_and_retained_characters()
        {
            const string Raw = "req\r-\u202E12 3\u200B-Ωμέγα+a\u2028b";

            string once = LoggingSanitizer.SanitizeCorrelationId(Raw);

            once.Should().Be("req-12 3-Ωμέγα+ab");
            LoggingSanitizer.SanitizeCorrelationId(once).Should().Be(once);
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationId_With_A_Clean_Value : LoggingSanitizerTests
    {
        [Test]
        public void It_returns_the_value_unchanged()
        {
            LoggingSanitizer.SanitizeCorrelationId("test-correlationId").Should().Be("test-correlationId");
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationId_With_Null_Or_Empty_Input : LoggingSanitizerTests
    {
        [Test]
        public void It_returns_empty_string_for_null()
        {
            LoggingSanitizer.SanitizeCorrelationId(null).Should().Be(string.Empty);
        }

        [Test]
        public void It_returns_empty_string_for_empty()
        {
            LoggingSanitizer.SanitizeCorrelationId(string.Empty).Should().Be(string.Empty);
        }

        [Test]
        public void It_returns_empty_string_when_every_character_is_a_control_character()
        {
            LoggingSanitizer.SanitizeCorrelationId("\r\n\t\0").Should().Be(string.Empty);
        }

        [Test]
        public void It_preserves_whitespace_that_is_not_a_control_character()
        {
            LoggingSanitizer.SanitizeCorrelationId("   ").Should().Be("   ");
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationId_With_Supplementary_Plane_Format_Characters
        : LoggingSanitizerTests
    {
        // The Cf category is not confined to the BMP, and the allowlist is a category test, so
        // these must be removed for exactly the reason the zero-width characters are. They are
        // pinned separately because a per-UTF-16-code-unit implementation of the same category
        // test silently misses every one of them: both halves of a non-BMP code point are
        // surrogates, and char.GetUnicodeCategory reports Cs for a surrogate, never Cf. These
        // tests fail against such an implementation and pass against a code-point-wise one.

        [Test]
        public void It_removes_the_tag_block_characters()
        {
            // U+E0020 TAG SPACE and U+E007F CANCEL TAG.
            LoggingSanitizer.SanitizeCorrelationId("trace\U000E0020\U000E007Fid").Should().Be("traceid");
        }

        [Test]
        public void It_removes_the_language_tag_character()
        {
            // U+E0001 LANGUAGE TAG.
            LoggingSanitizer.SanitizeCorrelationId("req-\U000E0001123").Should().Be("req-123");
        }

        [Test]
        public void It_removes_the_musical_notation_format_characters()
        {
            // U+1D173 MUSICAL SYMBOL BEGIN BEAM and U+1D17A MUSICAL SYMBOL END PHRASE.
            LoggingSanitizer.SanitizeCorrelationId("a\U0001D173b\U0001D17Ac").Should().Be("abc");
        }

        [Test]
        public void It_removes_the_remaining_astral_format_characters()
        {
            // U+110BD KAITHI NUMBER SIGN, U+13430 EGYPTIAN HIEROGLYPH VERTICAL JOINER and
            // U+1BCA0 SHORTHAND FORMAT LETTER OVERLAP.
            LoggingSanitizer
                .SanitizeCorrelationId("x\U000110BDy\U00013430z\U0001BCA0w")
                .Should()
                .Be("xyzw");
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationId_With_A_Tag_Smuggled_Payload : LoggingSanitizerTests
    {
        // The full invisible-text-smuggling shape. Each ASCII character of "ADMIN" is encoded as
        // the TAG-block code point U+E0000 + its code, which renders as nothing at all, so the
        // value below displays as the bare "trace-1234" while carrying ten extra UTF-16 code
        // units. A per-code-unit filter round-trips it fully intact, which is precisely the
        // stored-value/displayed-value divergence the Cf rule exists to prevent.
        private const string SmuggledId =
            "trace-1234\U000E0041\U000E0044\U000E004D\U000E0049\U000E004E";

        [Test]
        public void It_strips_the_smuggled_payload_down_to_the_visible_id()
        {
            LoggingSanitizer.SanitizeCorrelationId(SmuggledId).Should().Be("trace-1234");
        }

        [Test]
        public void It_makes_the_smuggled_id_and_the_visible_id_the_same_string()
        {
            LoggingSanitizer
                .SanitizeCorrelationId(SmuggledId)
                .Should()
                .Be(LoggingSanitizer.SanitizeCorrelationId("trace-1234"));
        }

        [Test]
        public void It_returns_empty_string_when_the_value_is_only_a_smuggled_payload()
        {
            LoggingSanitizer
                .SanitizeCorrelationId("\U000E0041\U000E0044\U000E004D\U000E0049\U000E004E")
                .Should()
                .Be(string.Empty);
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationId_With_Supplementary_Plane_Characters_Outside_Cc_And_Cf
        : LoggingSanitizerTests
    {
        // The guard against the Cf fix over-reaching. Filtering by code point rather than by code
        // unit must not start removing astral characters that are not Cc or Cf, and it must not
        // split a surrogate pair on the way through.

        [Test]
        public void It_preserves_an_astral_symbol()
        {
            // U+1F600 GRINNING FACE, category So.
            LoggingSanitizer
                .SanitizeCorrelationId("trace-\U0001F600-id")
                .Should()
                .Be("trace-\U0001F600-id");
        }

        [Test]
        public void It_preserves_an_astral_letter()
        {
            // U+1D400 MATHEMATICAL BOLD CAPITAL A, category Lu.
            LoggingSanitizer.SanitizeCorrelationId("id-\U0001D400").Should().Be("id-\U0001D400");
        }

        [Test]
        public void It_preserves_a_surrogate_pair_while_removing_neighbouring_format_characters()
        {
            LoggingSanitizer
                .SanitizeCorrelationId("a​\U0001F600‮b")
                .Should()
                .Be("a\U0001F600b");
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationId_With_A_Lone_Surrogate : LoggingSanitizerTests
    {
        // A surrogate is category Cs, which is neither Cc nor Cf, so the allowlist keeps it.
        // CorrelationIdNormalizer.Normalize documents that a pre-existing lone surrogate is
        // preserved rather than repaired; this pins the sanitizer half of that promise, which
        // code-point iteration could otherwise break by substituting U+FFFD for it.
        [Test]
        public void It_preserves_a_lone_high_surrogate()
        {
            LoggingSanitizer.SanitizeCorrelationId("ab\uD83Dcd").Should().Be("ab\uD83Dcd");
        }

        [Test]
        public void It_preserves_a_lone_low_surrogate()
        {
            LoggingSanitizer.SanitizeCorrelationId("ab\uDC00cd").Should().Be("ab\uDC00cd");
        }

        [Test]
        public void It_preserves_a_trailing_lone_high_surrogate_while_removing_format_characters()
        {
            LoggingSanitizer.SanitizeCorrelationId("a​b\uD83D").Should().Be("ab\uD83D");
        }
    }

    [TestFixture]
    public class Given_SanitizeForLogging_With_Supplementary_Plane_Characters : LoggingSanitizerTests
    {
        // The strict Method/Path allowlist is deliberately still applied per UTF-16 code unit, so
        // every astral character is stripped: each half of the pair is a surrogate, and a
        // surrogate is neither a letter nor a digit. Rune.IsLetterOrDigit(U+1D400) is true, so
        // reunifying the two sanitizers on code-point iteration would silently start admitting
        // these. This test is what pins the strict path's behavior against that change.
        [Test]
        public void It_strips_an_astral_letter_from_a_method_or_path_value()
        {
            LoggingSanitizer.SanitizeForLogging("Method-\U0001D400-Path").Should().Be("Method--Path");
        }

        [Test]
        public void It_strips_an_astral_symbol_from_a_method_or_path_value()
        {
            LoggingSanitizer.SanitizeForLogging("GET /x\U0001F600y").Should().Be("GET /xy");
        }

        [Test]
        public void It_strips_a_lone_surrogate_from_a_method_or_path_value()
        {
            LoggingSanitizer.SanitizeForLogging("ab\uD83Dcd").Should().Be("abcd");
        }
    }

    [TestFixture]
    public class Given_SanitizeForConsole_With_Newlines : LoggingSanitizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = LoggingSanitizer.SanitizeForConsole("line1\nline2\r\nline3");
        }

        [Test]
        public void It_preserves_newline_characters()
        {
            _result.Should().Be("line1\nline2\r\nline3");
        }
    }

    [TestFixture]
    public class Given_SanitizeForConsole_With_Control_Characters_And_Newlines : LoggingSanitizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = LoggingSanitizer.SanitizeForConsole("hello\tworld\nnext\0line");
        }

        [Test]
        public void It_strips_tab_and_null_but_preserves_newline()
        {
            _result.Should().Be("helloworld\nnextline");
        }
    }

    [TestFixture]
    public class Given_SanitizeForConsole_With_No_Control_Characters : LoggingSanitizerTests
    {
        [Test]
        public void It_returns_original_string()
        {
            LoggingSanitizer.SanitizeForConsole("plain text").Should().Be("plain text");
        }
    }

    [TestFixture]
    public class Given_SanitizeForConsole_With_Null_Input : LoggingSanitizerTests
    {
        [Test]
        public void It_returns_empty_string()
        {
            LoggingSanitizer.SanitizeForConsole(null).Should().Be(string.Empty);
        }
    }

    [TestFixture]
    public class Given_SanitizeForConsole_With_Only_Newlines : LoggingSanitizerTests
    {
        [Test]
        public void It_returns_original_string()
        {
            LoggingSanitizer.SanitizeForConsole("\n\r\n").Should().Be("\n\r\n");
        }
    }
}
