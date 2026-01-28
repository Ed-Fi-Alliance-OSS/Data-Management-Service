// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Shared dialect rules used by model derivation and SQL emission.
/// </summary>
public interface ISqlDialectRules
{
    /// <summary>
    /// Gets the dialect these rules apply to.
    /// </summary>
    SqlDialect Dialect { get; }

    /// <summary>
    /// Gets the maximum identifier length. For PostgreSQL this is measured in UTF-8 bytes,
    /// while SQL Server uses character count.
    /// </summary>
    int MaxIdentifierLength { get; }

    /// <summary>
    /// Gets the default scalar type mappings for the dialect.
    /// </summary>
    SqlScalarTypeDefaults ScalarTypeDefaults { get; }

    /// <summary>
    /// Applies deterministic shortening (truncate + hash) when identifiers exceed limits.
    /// </summary>
    /// <param name="identifier">The identifier to shorten.</param>
    /// <returns>The shortened identifier, or the original identifier when no shortening is needed.</returns>
    string ShortenIdentifier(string identifier);
}

/// <summary>
/// Default scalar type mappings for a dialect.
/// </summary>
/// <param name="StringType">The base string type (length applied separately).</param>
/// <param name="Int32Type">The 32-bit integer type name.</param>
/// <param name="Int64Type">The 64-bit integer type name.</param>
/// <param name="DecimalType">The decimal type name (precision/scale applied separately).</param>
/// <param name="BooleanType">The boolean type name.</param>
/// <param name="DateType">The date-only type name.</param>
/// <param name="DateTimeType">The timestamp type name.</param>
/// <param name="TimeType">The time-only type name.</param>
public sealed record SqlScalarTypeDefaults(
    string StringType,
    string Int32Type,
    string Int64Type,
    string DecimalType,
    string BooleanType,
    string DateType,
    string DateTimeType,
    string TimeType
);

/// <summary>
/// PostgreSQL dialect rules for identifier limits and scalar type defaults.
/// </summary>
public sealed class PgsqlDialectRules : ISqlDialectRules
{
    private static readonly SqlScalarTypeDefaults Defaults = new(
        StringType: "varchar",
        Int32Type: "integer",
        Int64Type: "bigint",
        DecimalType: "numeric",
        BooleanType: "boolean",
        DateType: "date",
        DateTimeType: "timestamp with time zone",
        TimeType: "time"
    );

    /// <inheritdoc />
    public SqlDialect Dialect => SqlDialect.Pgsql;

    /// <inheritdoc />
    public int MaxIdentifierLength => 63;

    /// <inheritdoc />
    public SqlScalarTypeDefaults ScalarTypeDefaults => Defaults;

    /// <inheritdoc />
    public string ShortenIdentifier(string identifier)
    {
        return SqlDialectRulesUtilities.ShortenIdentifier(
            identifier,
            MaxIdentifierLength,
            IdentifierLengthUnit.Bytes
        );
    }
}

/// <summary>
/// SQL Server dialect rules for identifier limits and scalar type defaults.
/// </summary>
public sealed class MssqlDialectRules : ISqlDialectRules
{
    private static readonly SqlScalarTypeDefaults Defaults = new(
        StringType: "nvarchar",
        Int32Type: "int",
        Int64Type: "bigint",
        DecimalType: "decimal",
        BooleanType: "bit",
        DateType: "date",
        DateTimeType: "datetime2(7)",
        TimeType: "time(7)"
    );

    /// <inheritdoc />
    public SqlDialect Dialect => SqlDialect.Mssql;

    /// <inheritdoc />
    public int MaxIdentifierLength => 128;

    /// <inheritdoc />
    public SqlScalarTypeDefaults ScalarTypeDefaults => Defaults;

    /// <inheritdoc />
    public string ShortenIdentifier(string identifier)
    {
        return SqlDialectRulesUtilities.ShortenIdentifier(
            identifier,
            MaxIdentifierLength,
            IdentifierLengthUnit.Characters
        );
    }
}

internal enum IdentifierLengthUnit
{
    Characters,
    Bytes,
}

internal static class SqlDialectRulesUtilities
{
    private const int HashSegmentLength = 10;

    internal static string ShortenIdentifier(
        string identifier,
        int maxLength,
        IdentifierLengthUnit lengthUnit
    )
    {
        if (identifier is null)
        {
            throw new ArgumentNullException(nameof(identifier));
        }

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

        var hash = ComputeSha256Hex(identifier);
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

    private static string ComputeSha256Hex(string identifier)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(identifier);
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
