// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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

/// <summary>
/// Represents the unit used when enforcing dialect identifier length limits.
/// </summary>
internal enum IdentifierLengthUnit
{
    Characters,
    Bytes,
}

/// <summary>
/// Shared utilities used to implement dialect rules consistently across derivation and emission.
/// </summary>
internal static class SqlDialectRulesUtilities
{
    /// <summary>
    /// Applies deterministic identifier shortening based on the dialect's length constraints.
    /// </summary>
    /// <param name="identifier">The identifier to shorten.</param>
    /// <param name="maxLength">The maximum allowed identifier length.</param>
    /// <param name="lengthUnit">
    /// The measurement unit for the identifier length (characters vs UTF-8 bytes).
    /// </param>
    /// <returns>
    /// The original identifier when within limits, otherwise a truncated identifier suffixed with a hash segment.
    /// </returns>
    internal static string ShortenIdentifier(
        string identifier,
        int maxLength,
        IdentifierLengthUnit lengthUnit
    )
    {
        return SqlIdentifierShortening.Apply(identifier, identifier, maxLength, lengthUnit);
    }
}
