// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The stderr ring buffer of D-10b: the SQLSTATE of the last retained message when it is shaped
/// <c>&lt;severity&gt;:  XXXXX:</c> in any server language, with or without the
/// <c>psql:&lt;file&gt;:&lt;line&gt;:</c> prefix (whose file may itself contain colons); a last message that is
/// <c>psql</c>'s own, a notice or warning class, or an invalid token yields nothing; retention is bounded at
/// 8 KiB with the oldest text overwritten, and extraction clears the buffer.
/// </summary>
public class PsqlStderrSqlStateTests
{
    private const string PrefixedErrorLine =
        "psql:/tmp/dms-provision-0123456789ab/artifact.sql:12: ERROR:  42P01: relation \"edfi.student\" does not exist";

    private const string BareErrorLine = "ERROR:  22P02: invalid input syntax for type integer";

    private static string? Extract(string text)
    {
        PsqlStderrSqlState buffer = new();
        buffer.Append(text);
        return buffer.ExtractAndClear();
    }

    /// <summary>
    /// An earlier notice of an error class (<c>CREATE EXTENSION IF NOT EXISTS</c> reports <c>42710</c>) is
    /// followed by the error that stopped the replay and the server's detail lines, which start with neither
    /// <c>psql:</c> nor a code: the error's code is the one kept, which the English-only rule found by spelling
    /// and this rule finds by position.
    /// </summary>
    [TestFixture]
    public class Given_the_verbose_output_of_a_stopped_replay
    {
        private string? _first;
        private string? _second;

        [SetUp]
        public void Setup()
        {
            PsqlStderrSqlState buffer = new();
            buffer.Append(
                "psql:/tmp/dms-provision-0123456789ab/artifact.sql:2: NOTICE:  42710: extension \"plpgsql\" already exists, skipping\n"
            );
            buffer.Append(PrefixedErrorLine + "\n");
            buffer.Append("DETAIL:  Key (id)=(1) already exists.\r\n");
            buffer.Append("LOCATION:  parserOpenTable, parse_relation.c:1449\n");
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
                "psql:/tmp/a.sql:12: FEHLER:  42501: keine Berechtigung für Schema dms",
                "psql:/tmp/a.sql:12: ERREUR:  42P01: la relation « edfi.student » n'existe pas",
                "psql:/tmp/a.sql:12: ОШИБКА:  23505: повторяющееся значение ключа",
                "psql:/tmp/a.sql:12: エラー:  42601: 構文エラー",
                "psql:/tmp/a.sql:12: FATAL:  57P01: terminating connection due to administrator command",
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
    /// verbose reporting only once connected). Nothing is extracted, rather than the earlier notice's code.
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
                "psql:/tmp/a.sql:2: NOTICE:  42710: extension \"plpgsql\" already exists, skipping\n"
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
                "psql:C:/Users/sam/dms-provision-0123456789ab/artifact.sql:3: ERROR:  42P01: relation does not exist",
                "psql:C:\\Users\\sam\\dms-provision-0123456789ab\\artifact.sql:3: ERROR:  22P02: invalid input",
                "psql:/tmp/dms:provision/artifact.sql:7: ERROR:  23505: duplicate key",
                "psql:C:/Users/sam/artifact.sql:3: ERROR:  42p01: lower case is still rejected",
                "psql:C:/Users/sam/artifact.sql: ERROR:  42P01: no line number is still rejected",
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
                "ERROR:  4260: four characters",
                "ERROR:  42p01: lower case",
                "ERROR:  42P011: six characters",
                "WARNING:  01000: a warning class",
                "HINWEIS:  00000: a notice class in German",
                "psql:/tmp/a.sql:4: NOTICE:  02000: a no-data class",
                "ERROR: 42P01: one space",
                "  ERROR:  42P01: indented",
                "psql:artifact.sql:12: ERROR:  42P01 no trailing colon",
                "psql:artifact.sql: ERROR:  42P01: no line number",
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
            string.Concat(
                Enumerable.Repeat("NOTICE:  00000: nothing to see on this line of output\n", lines)
            );
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
    public class Given_a_stopping_error_followed_by_more_detail_than_one_line_may_hold
    {
        private const string Header =
            "psql:/tmp/dms-provision-0123456789ab/artifact.sql:4120: ERROR:  42501: permission denied for schema dms";

        private string? _longDetail;
        private string? _longDetailInPieces;
        private string? _longHeader;
        private string? _longDetailThenLostConnection;
        private string? _longDetailThenNotice;

        /// <summary>
        /// What <c>VERBOSITY=verbose</c> prints after the first line of an error raised inside a PL/pgSQL body: the
        /// field lines and the statement's own text, here more than <see cref="PsqlStderrSqlState.Capacity"/>.
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
            _longHeader = ExtractFrom([
                Header + new string('x', 3 * PsqlStderrSqlState.Capacity) + "\n" + "DETAIL:  more\n",
            ]);
            _longDetailThenLostConnection = ExtractFrom([
                output + "psql:/tmp/a.sql:4120: error: connection to server was lost\n",
            ]);
            _longDetailThenNotice = ExtractFrom([
                output + "psql:/tmp/a.sql:4121: NOTICE:  00000: a later notice\n",
            ]);
        }

        [Test]
        public void It_keeps_the_code_of_the_stopping_error() => _longDetail.Should().Be("42501");

        [Test]
        public void It_keeps_it_whatever_the_chunk_boundaries() => _longDetailInPieces.Should().Be("42501");

        [Test]
        public void It_keeps_it_when_the_first_line_itself_is_longer_than_the_capacity() =>
            _longHeader.Should().Be("42501");

        [Test]
        public void It_still_reports_nothing_when_a_later_message_is_psqls_own() =>
            _longDetailThenLostConnection.Should().BeNull();

        [Test]
        public void It_still_reports_nothing_when_a_later_message_is_a_notice() =>
            _longDetailThenNotice.Should().BeNull();
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
