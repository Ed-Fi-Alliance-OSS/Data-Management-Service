// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

internal static class CommandDiagnostics
{
    // Escape once, preserving readable Unicode and already escaped diagnostic values.
    internal static string Normalize(string value)
    {
        StringBuilder normalized = new();
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (
                Rune.GetUnicodeCategory(rune)
                is UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator
            )
            {
                normalized.Append(rune.IsBmp ? $"\\u{rune.Value:X4}" : $"\\U{rune.Value:X8}");
            }
            else
            {
                normalized.Append(rune.ToString());
            }
        }

        return normalized.ToString();
    }

    /// <summary>Report a structured execution failure. Policy mismatches are handled separately.</summary>
    internal static int ExecutionFailure(TextWriter error, string operation, string message)
    {
        error.WriteLine($"{Normalize(operation)} failed: {Normalize(message)}");
        return 1;
    }

    /// <summary>
    /// Map local input, processing, serialization, and file errors to stderr and exit 1.
    /// Remote/credential-bearing errors must be translated to safe messages by their caller.
    /// </summary>
    internal static int Execute(TextWriter error, string operation, Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException
                        or InvalidOperationException
                        or JsonException
            )
        {
            return ExecutionFailure(error, operation, exception.Message);
        }
    }
}
