// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The only consumer of <c>psql</c> standard error (D-10b), read as it arrives solely so a SQLSTATE can be
/// extracted from it. The text is never logged, stored, formatted, or placed in an exception; this type exposes
/// nothing but the extracted code, and extraction clears it. With <c>VERBOSITY=verbose</c> and
/// <c>ON_ERROR_STOP=1</c> the error that stopped the replay is the last message <c>psql</c> writes, but its
/// detail, hint, query and context lines follow its first line and can be longer than any bound, so the code is
/// taken from each message's first line as that line completes and kept until a later message replaces it.
/// Memory is bounded: only the line being read is held, at most <see cref="Capacity"/> characters of its start,
/// which is where a message's prefix, severity and code are. The severity word is the server's, translated into
/// its <c>lc_messages</c> language (<c>LC_ALL=C</c> fixes only <c>psql</c>'s own text), so the stopping error is
/// told from notices by being the last message, never by its spelling.
/// </summary>
internal sealed partial class PsqlStderrSqlState
{
    /// <summary>The number of characters retained of the line being read; the rest of a longer line is discarded.</summary>
    public const int Capacity = 8 * 1024;

    private readonly char[] _line = new char[Capacity];
    private int _length;
    private string? _lastMessageSqlState;

    /// <summary>Appends text, in pieces of any size; each line is judged when its line break arrives.</summary>
    public void Append(ReadOnlySpan<char> text)
    {
        while (!text.IsEmpty)
        {
            int lineBreak = text.IndexOf('\n');
            ReadOnlySpan<char> part = lineBreak < 0 ? text : text[..lineBreak];
            int kept = Math.Min(part.Length, Capacity - _length);
            part[..kept].CopyTo(_line.AsSpan(_length));
            _length += kept;
            if (lineBreak < 0)
            {
                return;
            }

            EndLine();
            text = text[(lineBreak + 1)..];
        }
    }

    /// <summary>
    /// The SQLSTATE of the last message, when its first line is shaped
    /// <c>[psql:&lt;file&gt;:&lt;line&gt;: ]&lt;severity&gt;:  XXXXX:</c> in any language and the code is of an
    /// error class; null when that message is <c>psql</c>'s own (a lost connection, a failed connection
    /// attempt), a notice or warning (classes <c>00</c>, <c>01</c>, <c>02</c>), or carries no valid code. A
    /// message starts on a line that begins with <c>psql:</c> or has that shape; the server's detail lines that
    /// follow it start with neither. A last line without a line break is judged too. Everything is cleared either way.
    /// </summary>
    public string? ExtractAndClear()
    {
        if (_length > 0)
        {
            EndLine();
        }

        string? sqlState = _lastMessageSqlState;
        _lastMessageSqlState = null;
        return sqlState;
    }

    /// <summary>Judges the completed line: a message's first line replaces the kept code; any other line is ignored.</summary>
    private void EndLine()
    {
        ReadOnlySpan<char> line = _line.AsSpan(0, _length).TrimEnd('\r');
        if (line.StartsWith("psql:", StringComparison.Ordinal) || ServerMessageLine().IsMatch(line))
        {
            _lastMessageSqlState = ErrorSqlStateOf(line.ToString());
        }

        _line.AsSpan(0, _length).Clear();
        _length = 0;
    }

    private static string? ErrorSqlStateOf(string firstLine)
    {
        Match match = ServerMessageLine().Match(firstLine);
        string sqlState = match.Groups[1].Value;
        return
            match.Success
            && PostgresqlErrorClassifier.IsSqlState(sqlState)
            && !sqlState.StartsWith("00", StringComparison.Ordinal)
            && !sqlState.StartsWith("01", StringComparison.Ordinal)
            && !sqlState.StartsWith("02", StringComparison.Ordinal)
            ? sqlState
            : null;
    }

    // The optional prefix is psql:<file>:<line>: and the file may itself contain colons (a Windows drive, a
    // Unix directory named with one), so the file part is any text and the line number anchors the prefix. The
    // severity is one word in the server's language (FEHLER, ERREUR, ОШИБКА), so any word without a space or a
    // colon is accepted.
    [GeneratedRegex(@"^(?:psql:.*:\d+: )?[^\s:]+:\s{2}([0-9A-Z]{5}):")]
    private static partial Regex ServerMessageLine();
}
