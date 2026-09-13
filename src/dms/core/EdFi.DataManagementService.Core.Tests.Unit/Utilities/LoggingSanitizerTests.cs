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
