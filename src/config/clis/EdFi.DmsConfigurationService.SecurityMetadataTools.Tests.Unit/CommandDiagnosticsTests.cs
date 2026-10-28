// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

[TestFixture]
public class Given_CommandDiagnostics_text
{
    [TestCase("plain", "École 東京 👋 /claims/Read", "École 東京 👋 /claims/Read")]
    [TestCase("control", "one\0\t\r\n\u007F\u0085two", "one\\u0000\\u0009\\u000D\\u000A\\u007F\\u0085two")]
    [TestCase("format", "one\u200B\u202Etwo", "one\\u200B\\u202Etwo")]
    [TestCase("separator", "one\u2028\u2029two", "one\\u2028\\u2029two")]
    [TestCase("supplementary-format", "one\U000E0001two", "one\\U000E0001two")]
    [TestCase("already-escaped", @"one\u000Atwo", @"one\u000Atwo")]
    public void It_escapes_unsafe_characters_once(string label, string input, string expected)
    {
        CommandDiagnostics.Normalize(input).Should().Be(expected, label);
        CommandDiagnostics.Normalize(CommandDiagnostics.Normalize(input)).Should().Be(expected, label);
    }

    [Test]
    public void It_renders_structured_failures_with_safe_context_and_exit_one()
    {
        using var error = new StringWriter();
        int exitCode = CommandDiagnostics.ExecutionFailure(error, "Merge\n", "École\u2028claim\u200B");

        exitCode.Should().Be(1);
        error.ToString().Should().Be("Merge\\u000A failed: École\\u2028claim\\u200B" + Environment.NewLine);
    }
}

[TestFixture("io")]
[TestFixture("access")]
[TestFixture("argument")]
[TestFixture("unsupported")]
[TestFixture("processing")]
[TestFixture("json")]
public class Given_CommandDiagnostics_execution_failure(string kind)
{
    private int _exitCode;
    private string _error = string.Empty;

    [SetUp]
    public void Setup()
    {
        const string message = "input\nÉcole\u2029";
        Exception failure = kind switch
        {
            "io" => new IOException(message),
            "access" => new UnauthorizedAccessException(message),
            "argument" => new ArgumentException(message),
            "unsupported" => new NotSupportedException(message),
            "processing" => new InvalidOperationException(message),
            "json" => new JsonException(message),
            _ => throw new InvalidOperationException(kind),
        };
        using var error = new StringWriter();
        _exitCode = CommandDiagnostics.Execute(error, "Conversion", () => throw failure);
        _error = error.ToString();
    }

    [Test]
    public void It_returns_execution_failure_not_policy_mismatch() => _exitCode.Should().Be(1);

    [Test]
    public void It_preserves_the_conversion_prefix_and_normalizes_the_message() =>
        _error.Should().Be("Conversion failed: input\\u000AÉcole\\u2029" + Environment.NewLine);
}

[TestFixture]
public class Given_CommandDiagnostics_success
{
    private int _exitCode;
    private int _calls;
    private string _error = string.Empty;

    [SetUp]
    public void Setup()
    {
        _calls = 0;
        using var error = new StringWriter();
        _exitCode = CommandDiagnostics.Execute(error, "Conversion", () => _calls++);
        _error = error.ToString();
    }

    [Test]
    public void It_executes_the_operation_once_and_returns_success()
    {
        _calls.Should().Be(1);
        _exitCode.Should().Be(0);
    }

    [Test]
    public void It_writes_no_diagnostics() => _error.Should().BeEmpty();
}
