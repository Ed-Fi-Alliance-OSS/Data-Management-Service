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
    public class Given_SanitizeCorrelationIdForLogging_With_Control_Characters : LoggingSanitizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = LoggingSanitizer.SanitizeCorrelationIdForLogging("tr\race\nid\twith\0nulls");
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
    public class Given_SanitizeCorrelationIdForLogging_With_Upstream_Punctuation : LoggingSanitizerTests
    {
        // The single most important assertion for FR-LOG-3: these characters are common in
        // upstream identifier schemes (base64, W3C traceparent, JSON-ish keys, RFC 5322
        // addresses) and the stricter Method/Path allowlist would strip all of them.
        private const string UpstreamId = "a+b=c{d}e@f|g,h#i(j)k[l]m<n>o\"p'q";

        [Test]
        public void It_preserves_printable_punctuation()
        {
            LoggingSanitizer.SanitizeCorrelationIdForLogging(UpstreamId).Should().Be(UpstreamId);
        }

        [Test]
        public void It_is_broader_than_the_strict_method_and_path_allowlist()
        {
            LoggingSanitizer.SanitizeForLogging(UpstreamId).Should().NotBe(UpstreamId);
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationIdForLogging_With_Unicode_Line_Separators : LoggingSanitizerTests
    {
        [Test]
        public void It_removes_line_and_paragraph_separators_that_are_not_control_characters()
        {
            // U+2028 and U+2029 are Unicode categories Zl and Zp, so char.IsControl is false
            // for both and the allowlist predicate admits them. They are removed by the
            // ReplaceLineEndings call that opens LogSanitizer.SanitizeCorrelationIdForLog,
            // which is therefore load-bearing rather than redundant: deleting it as dead work
            // would re-admit two characters that break a line-oriented log consumer. This
            // test is what pins that call in place.
            LoggingSanitizer
                .SanitizeCorrelationIdForLogging("trace\u2028id\u2029end")
                .Should()
                .Be("traceidend");
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationIdForLogging_With_Non_Ascii_Characters : LoggingSanitizerTests
    {
        [Test]
        public void It_preserves_non_ascii_printable_characters()
        {
            LoggingSanitizer
                .SanitizeCorrelationIdForLogging("trace-Ωμέγα-日本語-ñ")
                .Should()
                .Be("trace-Ωμέγα-日本語-ñ");
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationIdForLogging_With_A_Clean_Value : LoggingSanitizerTests
    {
        [Test]
        public void It_returns_the_value_unchanged()
        {
            LoggingSanitizer
                .SanitizeCorrelationIdForLogging("test-correlationId")
                .Should()
                .Be("test-correlationId");
        }
    }

    [TestFixture]
    public class Given_SanitizeCorrelationIdForLogging_With_Null_Or_Empty_Input : LoggingSanitizerTests
    {
        [Test]
        public void It_returns_empty_string_for_null()
        {
            LoggingSanitizer.SanitizeCorrelationIdForLogging(null).Should().Be(string.Empty);
        }

        [Test]
        public void It_returns_empty_string_for_empty()
        {
            LoggingSanitizer.SanitizeCorrelationIdForLogging(string.Empty).Should().Be(string.Empty);
        }

        [Test]
        public void It_returns_empty_string_when_every_character_is_a_control_character()
        {
            LoggingSanitizer.SanitizeCorrelationIdForLogging("\r\n\t\0").Should().Be(string.Empty);
        }

        [Test]
        public void It_preserves_whitespace_that_is_not_a_control_character()
        {
            LoggingSanitizer.SanitizeCorrelationIdForLogging("   ").Should().Be("   ");
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
