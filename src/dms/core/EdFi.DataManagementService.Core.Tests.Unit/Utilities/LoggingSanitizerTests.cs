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
