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
/// last thing <c>psql</c> writes, so it survives any amount of earlier output.
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
    /// The SQLSTATE of the first retained line shaped <c>[psql:&lt;file&gt;:&lt;line&gt;: ]ERROR:  XXXXX:</c>, or
    /// null when no retained line carries a valid code. The buffer is cleared either way.
    /// </summary>
    public string? ExtractAndClear()
    {
        string retained = Retained();
        Clear();

        foreach (string line in retained.Split('\n'))
        {
            Match match = ErrorLine().Match(line.TrimEnd('\r'));
            if (match.Success && PostgresqlErrorClassifier.IsSqlState(match.Groups[1].Value))
            {
                return match.Groups[1].Value;
            }
        }

        return null;
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
    // Unix directory named with one), so the file part is any text and the line number anchors the prefix.
    [GeneratedRegex(@"^(?:psql:.*:\d+: )?ERROR:\s{2}([0-9A-Z]{5}):")]
    private static partial Regex ErrorLine();
}
