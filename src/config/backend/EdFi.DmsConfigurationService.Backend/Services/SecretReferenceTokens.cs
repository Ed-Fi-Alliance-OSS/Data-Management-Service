// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// One <c>${secret:&lt;name&gt;}</c> reference found in a connection string value: where it starts,
/// how many characters it spans including its delimiters, and the name it carries.
/// </summary>
public readonly record struct SecretReferenceToken(int Index, int Length, string Name);

/// <summary>
/// The secret reference grammar. A token is <c>${secret:</c>, one or more name characters, and
/// <c>}</c>, matched ordinally and case sensitively because vault names are. Anything else that opens
/// with <c>${</c> is not a token and is left alone, which keeps a password that happens to contain
/// <c>${</c> from being reinterpreted. There is no escape sequence.
///
/// The tokens are returned with their positions in the original value so the caller substitutes
/// each segment once, and a resolved value is never rescanned.
/// </summary>
public static class SecretReferenceTokens
{
    private const string Opening = "${secret:";

    private const char Closing = '}';

    /// <summary>
    /// Every well-formed token in the value, in order. Tokens never overlap.
    /// </summary>
    public static IReadOnlyList<SecretReferenceToken> Find(string value)
    {
        List<SecretReferenceToken> tokens = [];
        int searchFrom = 0;

        while (searchFrom < value.Length)
        {
            int index = value.IndexOf(Opening, searchFrom, StringComparison.Ordinal);

            if (index < 0)
            {
                break;
            }

            int nameStart = index + Opening.Length;
            int nameEnd = nameStart;

            while (nameEnd < value.Length && IsNameCharacter(value[nameEnd]))
            {
                nameEnd++;
            }

            if (nameEnd > nameStart && nameEnd < value.Length && value[nameEnd] == Closing)
            {
                tokens.Add(new(index, nameEnd + 1 - index, value[nameStart..nameEnd]));
                searchFrom = nameEnd + 1;
            }
            else
            {
                // Not a token. The scan resumes just past this opening, so a malformed fragment
                // cannot swallow a well-formed token that follows it.
                searchFrom = index + 1;
            }
        }

        return tokens;
    }

    /// <summary>
    /// ASCII letters, digits, and <c>_ - . / : @ +</c>, which covers the vault naming rules in scope
    /// and excludes every character that would make a token ambiguous inside a connection string.
    /// </summary>
    private static bool IsNameCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or ':' or '@' or '+';
}
