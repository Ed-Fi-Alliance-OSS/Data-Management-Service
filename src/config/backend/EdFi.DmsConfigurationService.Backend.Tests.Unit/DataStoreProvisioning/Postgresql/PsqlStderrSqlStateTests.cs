// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The stderr ring buffer of D-10b: the SQLSTATE of the first retained <c>ERROR:  XXXXX:</c> line, with or
/// without the <c>psql:&lt;file&gt;:&lt;line&gt;:</c> prefix, invalid tokens discarded, retention bounded at 8 KiB
/// with the oldest text overwritten, and the buffer cleared by extraction.
/// </summary>
public class PsqlStderrSqlStateTests
{
    private const string PrefixedErrorLine =
        "psql:/tmp/dms-provision-0123456789ab/artifact.sql:12: ERROR:  42P01: relation \"edfi.student\" does not exist";

    private const string BareErrorLine = "ERROR:  22P02: invalid input syntax for type integer";

    [TestFixture]
    public class Given_the_verbose_output_of_a_stopped_replay
    {
        private string? _first;
        private string? _second;

        [SetUp]
        public void Setup()
        {
            PsqlStderrSqlState buffer = new();
            buffer.Append(PrefixedErrorLine + "\n");
            buffer.Append("LOCATION:  parserOpenTable, parse_relation.c:1449\n");
            buffer.Append(BareErrorLine + "\r\n");
            _first = buffer.ExtractAndClear();
            _second = buffer.ExtractAndClear();
        }

        [Test]
        public void It_extracts_the_first_sqlstate() => _first.Should().Be("42P01");

        [Test]
        public void It_is_empty_after_extraction() => _second.Should().BeNull();
    }

    [TestFixture]
    public class Given_a_bare_error_line_with_windows_line_endings
    {
        private string? _sqlState;

        [SetUp]
        public void Setup()
        {
            PsqlStderrSqlState buffer = new();
            buffer.Append("some earlier notice\r\n" + BareErrorLine + "\r\n");
            _sqlState = buffer.ExtractAndClear();
        }

        [Test]
        public void It_extracts_the_sqlstate() => _sqlState.Should().Be("22P02");
    }

    [TestFixture]
    public class Given_lines_that_carry_no_valid_sqlstate
    {
        private Dictionary<string, string?> _extracted = null!;

        [SetUp]
        public void Setup()
        {
            string[] lines =
            [
                "ERROR:  4260: four characters",
                "ERROR:  42p01: lower case",
                "ERROR:  42P011: six characters",
                "WARNING:  42P01: not an error",
                "ERROR: 42P01: one space",
                "  ERROR:  42P01: indented",
                "psql:artifact.sql:12: ERROR:  42P01 no trailing colon",
                "psql:artifact.sql: ERROR:  42P01: no line number",
                "FATAL:  28P01: password authentication failed",
            ];
            _extracted = lines.ToDictionary(
                line => line,
                line =>
                {
                    PsqlStderrSqlState buffer = new();
                    buffer.Append(line + "\n");
                    return buffer.ExtractAndClear();
                }
            );
        }

        [Test]
        public void It_discards_every_one_of_them() =>
            _extracted.Values.Should().AllSatisfy(value => value.Should().BeNull());
    }

    [TestFixture]
    public class Given_more_output_than_the_buffer_retains
    {
        private string? _errorLast;
        private string? _errorPushedOut;
        private string? _singleOversizedAppend;

        [SetUp]
        public void Setup()
        {
            PsqlStderrSqlState errorLast = new();
            errorLast.Append(Filler(400));
            errorLast.Append(PrefixedErrorLine + "\n");
            _errorLast = errorLast.ExtractAndClear();

            PsqlStderrSqlState errorPushedOut = new();
            errorPushedOut.Append(PrefixedErrorLine + "\n");
            errorPushedOut.Append(Filler(200));
            _errorPushedOut = errorPushedOut.ExtractAndClear();

            PsqlStderrSqlState singleOversizedAppend = new();
            singleOversizedAppend.Append(Filler(400) + BareErrorLine + "\n");
            _singleOversizedAppend = singleOversizedAppend.ExtractAndClear();
        }

        private static string Filler(int lines) =>
            string.Concat(
                Enumerable.Repeat("NOTICE:  00000: nothing to see on this line of output\n", lines)
            );

        [Test]
        public void It_keeps_the_error_that_came_last() => _errorLast.Should().Be("42P01");

        [Test]
        public void It_loses_an_error_that_later_output_overwrote() => _errorPushedOut.Should().BeNull();

        [Test]
        public void It_keeps_the_tail_of_one_append_larger_than_the_buffer() =>
            _singleOversizedAppend.Should().Be("22P02");
    }

    [TestFixture]
    public class Given_an_error_line_appended_in_pieces_across_the_wrap_point
    {
        private string? _sqlState;

        [SetUp]
        public void Setup()
        {
            PsqlStderrSqlState buffer = new();
            buffer.Append(new string('x', PsqlStderrSqlState.Capacity - 10) + "\n");
            foreach (char character in PrefixedErrorLine + "\n")
            {
                buffer.Append([character]);
            }

            _sqlState = buffer.ExtractAndClear();
        }

        [Test]
        public void It_reassembles_the_line_across_the_wrap() => _sqlState.Should().Be("42P01");
    }

    [TestFixture]
    public class Given_no_output_at_all
    {
        private string? _sqlState;

        [SetUp]
        public void Setup() => _sqlState = new PsqlStderrSqlState().ExtractAndClear();

        [Test]
        public void It_extracts_nothing() => _sqlState.Should().BeNull();
    }
}
