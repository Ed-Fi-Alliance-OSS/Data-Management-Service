// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The stderr reader of D-10b under <c>VERBOSITY=sqlstate</c>: the SQLSTATE of the last message when it is a whole
/// line shaped <c>&lt;severity&gt;:  XXXXX</c> in any server language, with or without the
/// <c>psql:&lt;file&gt;:&lt;line&gt;:</c> prefix (whose file may itself contain colons); a last message that is
/// <c>psql</c>'s own, a notice or warning class, or an invalid token yields nothing; a line with anything after the
/// code is never a message; retention is bounded at 8 KiB per line, and extraction clears the buffer.
/// </summary>
public class PsqlStderrSqlStateTests
{
    private const string PrefixedErrorLine =
        "psql:/tmp/dms-provision-0123456789ab/artifact.sql:12: ERROR:  42P01";

    private const string BareErrorLine = "ERROR:  22P02";

    private static string? Extract(string text)
    {
        PsqlStderrSqlState buffer = new();
        buffer.Append(text);
        return buffer.ExtractAndClear();
    }

    /// <summary>
    /// An earlier notice of an error class (<c>CREATE EXTENSION IF NOT EXISTS</c> reports <c>42710</c>) is
    /// followed by the error that stopped the replay: the error's code is the one kept, which the English-only rule
    /// found by spelling and this rule finds by position.
    /// </summary>
    [TestFixture]
    public class Given_the_output_of_a_stopped_replay
    {
        private string? _first;
        private string? _second;

        [SetUp]
        public void Setup()
        {
            PsqlStderrSqlState buffer = new();
            buffer.Append("psql:/tmp/dms-provision-0123456789ab/artifact.sql:2: NOTICE:  42710\n");
            buffer.Append(PrefixedErrorLine + "\r\n");
            _first = buffer.ExtractAndClear();
            _second = buffer.ExtractAndClear();
        }

        [Test]
        public void It_extracts_the_stopping_error_not_the_earlier_notice() => _first.Should().Be("42P01");

        [Test]
        public void It_is_empty_after_extraction() => _second.Should().BeNull();
    }

    /// <summary>
    /// Review finding 1: the severity word is the server's, in its <c>lc_messages</c> language, so a German,
    /// French, Russian, or Japanese server still yields the code; <c>FATAL</c> is a severity like any other.
    /// </summary>
    [TestFixture]
    public class Given_server_messages_in_other_languages
    {
        private Dictionary<string, string?> _extracted = null!;

        [SetUp]
        public void Setup()
        {
            string[] lines =
            [
                "psql:/tmp/a.sql:12: FEHLER:  42501",
                "psql:/tmp/a.sql:12: ERREUR:  42P01",
                "psql:/tmp/a.sql:12: ОШИБКА:  23505",
                "psql:/tmp/a.sql:12: エラー:  42601",
                "psql:/tmp/a.sql:12: FATAL:  57P01",
            ];
            _extracted = lines.ToDictionary(line => line, line => Extract(line + "\n"));
        }

        [Test]
        public void It_extracts_each_code_whatever_the_severity_word() =>
            _extracted.Values.Should().Equal("42501", "42P01", "23505", "42601", "57P01");
    }

    /// <summary>
    /// The last message is <c>psql</c>'s own, which carries no SQLSTATE: a connection lost after an earlier
    /// notice, or a connection that could not be made (its server text has no code, because <c>psql</c> sets
    /// its reporting verbosity only once connected). Nothing is extracted, rather than the earlier notice's code.
    /// </summary>
    [TestFixture]
    public class Given_psql_reports_a_connection_failure_last
    {
        private string? _lostAfterNotice;
        private string? _neverConnected;

        [SetUp]
        public void Setup()
        {
            _lostAfterNotice = Extract(
                "psql:/tmp/a.sql:2: NOTICE:  42710\n"
                    + "psql:/tmp/a.sql:9: error: connection to server was lost\n"
            );
            _neverConnected = Extract(
                "psql: error: connection to server at \"db\" (10.0.0.5), port 5432 failed: FEHLER:  Passwort-Authentifizierung für Benutzer »dms« fehlgeschlagen\n"
            );
        }

        [Test]
        public void It_extracts_nothing_after_a_lost_connection() => _lostAfterNotice.Should().BeNull();

        [Test]
        public void It_extracts_nothing_from_a_failed_connection_attempt() =>
            _neverConnected.Should().BeNull();
    }

    [TestFixture]
    public class Given_a_bare_error_line_with_windows_line_endings
    {
        private string? _sqlState;

        [SetUp]
        public void Setup() => _sqlState = Extract("some earlier notice\r\n" + BareErrorLine + "\r\n");

        [Test]
        public void It_extracts_the_sqlstate() => _sqlState.Should().Be("22P02");
    }

    [TestFixture]
    public class Given_prefixes_whose_file_paths_contain_colons
    {
        private Dictionary<string, string?> _extracted = null!;

        [SetUp]
        public void Setup()
        {
            string[] lines =
            [
                "psql:C:/Users/sam/dms-provision-0123456789ab/artifact.sql:3: ERROR:  42P01",
                "psql:C:\\Users\\sam\\dms-provision-0123456789ab\\artifact.sql:3: ERROR:  22P02",
                "psql:/tmp/dms:provision/artifact.sql:7: ERROR:  23505",
                "psql:C:/Users/sam/artifact.sql:3: ERROR:  42p01",
                "psql:C:/Users/sam/artifact.sql: ERROR:  42P01",
            ];
            _extracted = lines.ToDictionary(line => line, line => Extract(line + "\n"));
        }

        [Test]
        public void It_extracts_through_a_windows_drive_path_with_forward_slashes() =>
            _extracted.Values.ElementAt(0).Should().Be("42P01");

        [Test]
        public void It_extracts_through_a_windows_drive_path_with_backslashes() =>
            _extracted.Values.ElementAt(1).Should().Be("22P02");

        [Test]
        public void It_extracts_through_a_unix_path_containing_a_colon() =>
            _extracted.Values.ElementAt(2).Should().Be("23505");

        [Test]
        public void It_still_rejects_an_invalid_token_and_a_prefix_without_a_line_number()
        {
            _extracted.Values.ElementAt(3).Should().BeNull();
            _extracted.Values.ElementAt(4).Should().BeNull();
        }
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
                "ERROR:  4260",
                "ERROR:  42p01",
                "ERROR:  42P011",
                "WARNING:  01000",
                "HINWEIS:  00000",
                "psql:/tmp/a.sql:4: NOTICE:  02000",
                "ERROR: 42P01",
                "  ERROR:  42P01",
                "psql:artifact.sql:12: ERROR:  42P01: text after the code, as verbose reporting writes it",
                "ERROR:  42P01 text after the code",
                "psql:artifact.sql: ERROR:  42P01",
            ];
            _extracted = lines.ToDictionary(line => line, line => Extract(line + "\n"));
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

        [Test]
        public void It_keeps_the_error_that_came_last() => _errorLast.Should().Be("42P01");

        [Test]
        public void It_reports_nothing_when_later_notices_are_the_last_message() =>
            _errorPushedOut.Should().BeNull();

        [Test]
        public void It_keeps_the_tail_of_one_append_larger_than_the_buffer() =>
            _singleOversizedAppend.Should().Be("22P02");

        private static string Filler(int lines) =>
            string.Concat(Enumerable.Repeat("psql:/tmp/a.sql:1: NOTICE:  00000\n", lines));
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
    public class Given_a_stopping_error_followed_by_more_output_than_one_line_may_hold
    {
        private const string Header = "psql:/tmp/dms-provision-0123456789ab/artifact.sql:4120: ERROR:  42501";

        private string? _longDetail;
        private string? _longDetailInPieces;
        private string? _headerShapedOverflow;
        private string? _longDetailThenLostConnection;
        private string? _longDetailThenNotice;

        /// <summary>
        /// Text that is no message line, more than <see cref="PsqlStderrSqlState.Capacity"/>: what verbose
        /// reporting would print after an error raised inside a PL/pgSQL body.
        /// </summary>
        private static string Fields()
        {
            string query = string.Concat(
                Enumerable.Repeat(
                    "    INSERT INTO dms.Document (DocumentUuid) VALUES (gen_random_uuid());\n",
                    200
                )
            );
            return "DETAIL:  The role lacks USAGE on the schema.\n"
                + "HINT:  Grant USAGE on the schema to the role.\n"
                + "QUERY:  DO $$\nBEGIN\n"
                + query
                + "END $$\n"
                + "CONTEXT:  PL/pgSQL function inline_code_block line 3 at SQL statement\n"
                + "LOCATION:  aclcheck_error, aclchk.c:2822\n";
        }

        private static string? ExtractFrom(IEnumerable<string> chunks)
        {
            PsqlStderrSqlState buffer = new();
            foreach (string chunk in chunks)
            {
                buffer.Append(chunk);
            }

            return buffer.ExtractAndClear();
        }

        private static IEnumerable<string> Pieces(string text, int size)
        {
            for (int start = 0; start < text.Length; start += size)
            {
                yield return text.Substring(start, Math.Min(size, text.Length - start));
            }
        }

        [SetUp]
        public void Setup()
        {
            Fields().Length.Should().BeGreaterThan(PsqlStderrSqlState.Capacity);
            string output = Header + "\n" + Fields();
            _longDetail = ExtractFrom([output]);
            _longDetailInPieces = ExtractFrom(Pieces(output, 1021));

            // A line whose retained start is exactly a message line's shape, but which goes on past the capacity.
            _headerShapedOverflow = ExtractFrom([
                output
                    + new string('A', PsqlStderrSqlState.Capacity - ":  23505".Length)
                    + ":  23505"
                    + new string('x', PsqlStderrSqlState.Capacity)
                    + "\n",
            ]);
            _longDetailThenLostConnection = ExtractFrom([
                output + "psql:/tmp/a.sql:4120: error: connection to server was lost\n",
            ]);
            _longDetailThenNotice = ExtractFrom([output + "psql:/tmp/a.sql:4121: NOTICE:  00000\n"]);
        }

        [Test]
        public void It_keeps_the_code_of_the_stopping_error() => _longDetail.Should().Be("42501");

        [Test]
        public void It_keeps_it_whatever_the_chunk_boundaries() => _longDetailInPieces.Should().Be("42501");

        [Test]
        public void It_never_reads_a_line_longer_than_the_capacity_as_a_message() =>
            _headerShapedOverflow.Should().Be("42501");

        [Test]
        public void It_still_reports_nothing_when_a_later_message_is_psqls_own() =>
            _longDetailThenLostConnection.Should().BeNull();

        [Test]
        public void It_still_reports_nothing_when_a_later_message_is_a_notice() =>
            _longDetailThenNotice.Should().BeNull();
    }

    /// <summary>
    /// A retryable error stopped the replay, and multi-line diagnostic text follows it whose labelled and unlabelled
    /// lines imitate a verbose message's first line (<c>ERROR:  23505: diagnostic text</c>). Under
    /// <c>VERBOSITY=sqlstate</c> a message line ends at its code, so none of them is a message: the stopping error's
    /// code stays authoritative, in one piece or in any chunks, under a translated severity too, and a genuine later
    /// message still replaces it.
    /// </summary>
    [TestFixture]
    public class Given_multiline_diagnostic_text_that_imitates_a_message_after_the_stopping_error
    {
        private const string Diagnostic =
            "DETAIL:  23505: Key (id)=(1) already exists.\n"
            + "ERROR:  23505: diagnostic text\n"
            + "QUERY:  SELECT 1\n"
            + "FEHLER:  42501: an unlabelled line of a multi-line value\n"
            + "psql-looking:1: ERROR:  22P02: more diagnostic text\n"
            + "CONTEXT:  22P02: PL/pgSQL function inline_code_block line 3 at SQL statement\n";

        private const string Stopped =
            "psql:/tmp/dms-provision-0123456789ab/artifact.sql:9: ERROR:  40001\n" + Diagnostic;

        private string? _stopped;
        private string? _stoppedInPieces;
        private string? _translated;
        private string? _laterPrefixedError;
        private string? _laterBareError;

        private static string? ExtractFrom(string text, int size)
        {
            PsqlStderrSqlState buffer = new();
            for (int start = 0; start < text.Length; start += size)
            {
                buffer.Append(text.AsSpan(start, Math.Min(size, text.Length - start)));
            }

            return buffer.ExtractAndClear();
        }

        [SetUp]
        public void Setup()
        {
            _stopped = Extract(Stopped);
            _stoppedInPieces = ExtractFrom(Stopped, 7);
            _translated = Extract("psql:/tmp/a.sql:9: FEHLER:  40001\n" + Diagnostic);
            _laterPrefixedError = Extract(Stopped + PrefixedErrorLine + "\n");
            _laterBareError = Extract(Stopped + BareErrorLine + "\n");
        }

        [Test]
        public void It_keeps_the_code_of_the_stopping_error() => _stopped.Should().Be("40001");

        [Test]
        public void It_keeps_it_whatever_the_chunk_boundaries() => _stoppedInPieces.Should().Be("40001");

        [Test]
        public void It_keeps_it_under_a_translated_severity() => _translated.Should().Be("40001");

        [Test]
        public void It_still_lets_a_later_message_replace_it() =>
            (_laterPrefixedError, _laterBareError).Should().Be(("42P01", "22P02"));
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
