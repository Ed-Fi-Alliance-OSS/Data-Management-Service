// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.Backend.External;

public static class SqlIdentifierShortening
{
    private const int HashSegmentLength = 10;

    public static string ApplyDialectLimit(string identifier, string hashInput, ISqlDialectRules dialectRules)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(hashInput);
        ArgumentNullException.ThrowIfNull(dialectRules);

        var lengthUnit =
            dialectRules.Dialect == SqlDialect.Pgsql
                ? IdentifierLengthUnit.Bytes
                : IdentifierLengthUnit.Characters;

        return Apply(identifier, hashInput, dialectRules.MaxIdentifierLength, lengthUnit);
    }

    internal static string Apply(
        string identifier,
        string hashInput,
        int maxLength,
        IdentifierLengthUnit lengthUnit
    )
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(hashInput);

        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxLength),
                maxLength,
                "Identifier length limit must be positive."
            );
        }

        var identifierLength = GetIdentifierLength(identifier, lengthUnit);

        if (identifierLength <= maxLength)
        {
            return identifier;
        }

        var hash = ComputeSha256Hex(hashInput);
        var suffix = $"_{hash[..HashSegmentLength]}";
        var prefixLength = maxLength - suffix.Length;

        if (prefixLength <= 0)
        {
            return suffix.Length > maxLength ? suffix[..maxLength] : suffix;
        }

        var prefix = lengthUnit switch
        {
            IdentifierLengthUnit.Characters => identifier[..prefixLength],
            IdentifierLengthUnit.Bytes => TruncateToUtf8Bytes(identifier, prefixLength),
            _ => throw new ArgumentOutOfRangeException(
                nameof(lengthUnit),
                lengthUnit,
                "Unsupported identifier length unit."
            ),
        };

        return $"{prefix}{suffix}";
    }

    private static int GetIdentifierLength(string identifier, IdentifierLengthUnit lengthUnit)
    {
        var length = lengthUnit switch
        {
            IdentifierLengthUnit.Characters => identifier.Length,
            IdentifierLengthUnit.Bytes => Encoding.UTF8.GetByteCount(identifier),
            _ => throw new ArgumentOutOfRangeException(
                nameof(lengthUnit),
                lengthUnit,
                "Unsupported identifier length unit."
            ),
        };

        return length;
    }

    private static string ComputeSha256Hex(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string TruncateToUtf8Bytes(string value, int maxBytes)
    {
        if (value.Length == 0 || maxBytes <= 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new(value.Length);
        var currentBytes = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            var runeString = rune.ToString();
            var runeBytes = Encoding.UTF8.GetByteCount(runeString);

            if (currentBytes + runeBytes > maxBytes)
            {
                break;
            }

            builder.Append(runeString);
            currentBytes += runeBytes;
        }

        return builder.ToString();
    }
}
