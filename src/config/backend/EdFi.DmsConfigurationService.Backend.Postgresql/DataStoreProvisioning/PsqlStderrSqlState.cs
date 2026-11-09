// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The only consumer of <c>psql</c> standard error (D-10b): a bounded ring buffer that keeps the most recent
/// 8 KiB of text solely so a SQLSTATE can be extracted from it. The text is never logged, stored, formatted,
/// or placed in an exception; this type exposes nothing but the extracted code, and extraction clears the
/// buffer. With <c>VERBOSITY=verbose</c> and <c>ON_ERROR_STOP=1</c> the error that stopped the replay is the
/// last message <c>psql</c> writes, so it survives any amount of earlier output. The severity word is the
/// server's, translated into its <c>lc_messages</c> language (<c>LC_ALL=C</c> fixes only <c>psql</c>'s own
/// text), so the stopping error is told from notices by being the last message, never by its spelling.
/// </summary>
internal sealed partial class PsqlStderrSqlState
{
    /// <summary>The number of characters retained; older text is overwritten.</summary>
    public const int Capacity = 8 * 1024;

    private readonly char[] _buffer = new char[Capacity];
    private int _next;
    private int _count;

    /// <summary>Appends text, keeping only the most recent <see cref="Capacity"/> characters.</summary>
    public void Append(ReadOnlySpan<char> text)
    {
        if (text.Length >= Capacity)
        {
            text = text[^Capacity..];
        }

        int firstPart = Math.Min(text.Length, Capacity - _next);
        text[..firstPart].CopyTo(_buffer.AsSpan(_next));
        text[firstPart..].CopyTo(_buffer);
        _next = (_next + text.Length) % Capacity;
        _count = Math.Min(Capacity, _count + text.Length);
    }

    /// <summary>
    /// The SQLSTATE of the last retained message, when its first line is shaped
    /// <c>[psql:&lt;file&gt;:&lt;line&gt;: ]&lt;severity&gt;:  XXXXX:</c> in any language and the code is of an
    /// error class; null when that message is <c>psql</c>'s own (a lost connection, a failed connection
    /// attempt), a notice or warning (classes <c>00</c>, <c>01</c>, <c>02</c>), or carries no valid code. A
    /// message starts on a line that begins with <c>psql:</c> or has that shape; the server's detail lines that
    /// follow it start with neither. The buffer is cleared either way.
    /// </summary>
    public string? ExtractAndClear()
    {
        string retained = Retained();
        Clear();

        string? lastMessage = null;
        foreach (string line in retained.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("psql:", StringComparison.Ordinal) || ServerMessageLine().IsMatch(trimmed))
            {
                lastMessage = trimmed;
            }
        }

        Match match = ServerMessageLine().Match(lastMessage ?? string.Empty);
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

    private string Retained()
    {
        if (_count < Capacity)
        {
            return new string(_buffer, 0, _count);
        }

        return string.Concat(_buffer.AsSpan(_next), _buffer.AsSpan(0, _next));
    }

    private void Clear()
    {
        Array.Clear(_buffer);
        _next = 0;
        _count = 0;
    }

    // The optional prefix is psql:<file>:<line>: and the file may itself contain colons (a Windows drive, a
    // Unix directory named with one), so the file part is any text and the line number anchors the prefix. The
    // severity is one word in the server's language (FEHLER, ERREUR, ОШИБКА), so any word without a space or a
    // colon is accepted.
    [GeneratedRegex(@"^(?:psql:.*:\d+: )?[^\s:]+:\s{2}([0-9A-Z]{5}):")]
    private static partial Regex ServerMessageLine();
}
