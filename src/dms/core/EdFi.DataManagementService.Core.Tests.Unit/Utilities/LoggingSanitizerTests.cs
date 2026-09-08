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

        [Test]
        public void It_keeps_using_the_stricter_allowlist_than_correlation_ids()
        {
            LoggingSanitizer.SanitizeForLogging("trace+={}@|,id").Should().Be("traceid");
        }
    }

    [TestFixture]
    public class Given_SanitizeForCorrelationId_With_Printable_Characters : LoggingSanitizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = LoggingSanitizer.SanitizeForCorrelationId("trace+={}@|,#()[]<>\"'ß");
        }

        [Test]
        public void It_preserves_every_printable_non_control_character()
        {
            _result.Should().Be("trace+={}@|,#()[]<>\"'ß");
        }
    }

    [TestFixture]
    public class Given_SanitizeForCorrelationId_With_Control_Characters : LoggingSanitizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = LoggingSanitizer.SanitizeForCorrelationId("trace\r\nid\twith\0unsafe");
        }

        [Test]
        public void It_removes_every_control_character()
        {
            _result.Should().Be("traceidwithunsafe");
        }

        [Test]
        public void It_removes_unicode_line_separator_characters()
        {
            LoggingSanitizer
                .SanitizeForCorrelationId("trace\u2028id\u2029unsafe")
                .Should()
                .Be("traceidunsafe");
        }
    }

    [TestFixture]
    public class Given_SanitizeForCorrelationId_With_Representative_TraceId_Punctuation
        : LoggingSanitizerTests
    {
        [Test]
        public void It_preserves_characters_that_the_strict_log_allowlist_would_strip()
        {
            LoggingSanitizer.SanitizeForCorrelationId("3f2a{b}+1").Should().Be("3f2a{b}+1");
            LoggingSanitizer.SanitizeForLogging("3f2a{b}+1").Should().Be("3f2ab1");
        }
    }

    [TestFixture]
    public class Given_SanitizeForCorrelationId_With_Null_Input : LoggingSanitizerTests
    {
        [Test]
        public void It_returns_empty_string()
        {
            LoggingSanitizer.SanitizeForCorrelationId(null).Should().BeEmpty();
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
