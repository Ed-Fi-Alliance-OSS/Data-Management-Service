// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Dialect-specific runtime shape for the namespace prefix list bound to a SQL <c>LIKE</c> filter.
/// </summary>
public enum NamespacePrefixParameterizationKind
{
    /// <summary>One PostgreSQL array parameter carrying every prefix-with-trailing-percent value.</summary>
    PgsqlArray,

    /// <summary>One SQL Server scalar parameter per prefix, OR-chained at the SQL site.</summary>
    MssqlScalar,
}

public enum NamespacePrefixParameterizationFailureKind
{
    InvalidNamespacePrefix,
    PrefixCapExceeded,
}

/// <summary>
/// Dialect-specific namespace prefix parameterization. Carries both the raw configured prefixes (for
/// user-facing ProblemDetails) and the prefix values already shaped for <c>LIKE</c> with a trailing
/// <c>%</c> wildcard (for SQL binding) so the SQL writer never has to append one.
/// </summary>
/// <param name="Kind">The emitted SQL/binding shape for the prefix list.</param>
/// <param name="BaseParameterName">The logical base parameter name.</param>
/// <param name="ConfiguredPrefixesInOrder">
/// The caller's raw namespace prefixes, deduplicated and ordinal-sorted, with no escaping or trailing
/// wildcard. These are what authorization-failure ProblemDetails present to the client.
/// </param>
/// <param name="LikePatternsInOrder">
/// Prefix values escaped for <c>LIKE</c> and normalized to <c>prefix%</c>, aligned 1:1 with
/// <paramref name="ConfiguredPrefixesInOrder"/>. These are the SQL-bound values.
/// </param>
/// <param name="ParameterNamesInOrder">Concrete SQL parameter names in deterministic binding order.</param>
public sealed record NamespacePrefixParameterization(
    NamespacePrefixParameterizationKind Kind,
    string BaseParameterName,
    IReadOnlyList<string> ConfiguredPrefixesInOrder,
    IReadOnlyList<string> LikePatternsInOrder,
    IReadOnlyList<string> ParameterNamesInOrder
)
{
    /// <summary>
    /// Evaluates in memory whether <paramref name="storedNamespace"/> starts with any configured
    /// prefix, mirroring the emitted SQL <c>LIKE</c> prefix filter so a single-record check accepts and
    /// rejects exactly the namespaces the <c>LIKE</c>-based GET-many and write paths do.
    /// </summary>
    /// <remarks>
    /// The match is an ordinal starts-with over <see cref="ConfiguredPrefixesInOrder"/> (the raw,
    /// unescaped prefixes), not over <see cref="LikePatternsInOrder"/>. Escaping in the LIKE patterns
    /// only neutralizes the wildcard metacharacters (<c>%</c>, <c>_</c>, and <c>[</c> on SQL Server) so
    /// each prefix matches literally; an ordinal starts-with over the raw prefix is exactly that literal
    /// <c>prefix%</c> match, so the two are equivalent without re-deriving the escaped form here. Case
    /// sensitivity follows the dialect's default Namespace column collation that the <c>LIKE</c> runs
    /// under: PostgreSQL's deterministic default is case-sensitive (<see cref="StringComparison.Ordinal"/>),
    /// the SQL Server default is case-insensitive (<see cref="StringComparison.OrdinalIgnoreCase"/>).
    /// </remarks>
    public bool MatchesAnyPrefix(string storedNamespace)
    {
        ArgumentNullException.ThrowIfNull(storedNamespace);

        var prefixComparison = Kind switch
        {
            NamespacePrefixParameterizationKind.PgsqlArray => StringComparison.Ordinal,
            NamespacePrefixParameterizationKind.MssqlScalar => StringComparison.OrdinalIgnoreCase,
            _ => throw new InvalidOperationException(
                $"Unsupported namespace prefix parameterization kind '{Kind}'."
            ),
        };

        return ConfiguredPrefixesInOrder.Any(configuredPrefix =>
            storedNamespace.StartsWith(configuredPrefix, prefixComparison)
        );
    }
}

/// <summary>
/// Builds the namespace prefix parameterization for a SQL dialect. Each configured prefix is meant to be
/// a literal "starts-with" match, so C# escapes any <c>LIKE</c> metacharacters in the prefix
/// (using <see cref="LikeEscapeCharacter"/> as the escape character) before appending the trailing
/// <c>%</c> wildcard. The SQL emitter therefore sees only <c>LIKE @param</c> / <c>LIKE ANY(...)</c>
/// over pre-escaped patterns. SQL Server fails closed with <see cref="NamespacePrefixLimitExceededException"/>
/// when the prefix count would exceed the parameterized OR-chain limit.
/// </summary>
public static class NamespacePrefixParameterizationFactory
{
    /// <summary>
    /// The escape character used in the emitted <c>LIKE</c> patterns. PostgreSQL treats backslash as the
    /// default <c>LIKE</c> escape; SQL Server requires an explicit <c>ESCAPE '\'</c> clause at the SQL site.
    /// </summary>
    public const char LikeEscapeCharacter = '\\';

    public static NamespacePrefixParameterization Create(
        SqlDialect dialect,
        IReadOnlyList<string> namespacePrefixes,
        string baseParameterName
    )
    {
        ArgumentNullException.ThrowIfNull(namespacePrefixes);
        PlanSqlWriterExtensions.ValidateBareParameterName(baseParameterName, nameof(baseParameterName));

        if (namespacePrefixes.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException(
                "Namespace prefixes must not contain null or empty values.",
                nameof(namespacePrefixes)
            );
        }

        // Raw configured prefixes, deduplicated and ordinal-sorted, drive both the user-facing
        // ProblemDetails and the escaped SQL patterns so the two stay aligned 1:1.
        var configuredPrefixes = namespacePrefixes
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static prefix => prefix, StringComparer.Ordinal)
            .ToArray();

        if (configuredPrefixes.Length == 0)
        {
            throw new ArgumentException(
                "Namespace prefix parameterization requires at least one prefix.",
                nameof(namespacePrefixes)
            );
        }

        // Escape LIKE metacharacters per dialect so a prefix containing '%', '_', or (SQL Server)
        // '[' is matched literally instead of as a wildcard, preserving starts-with semantics.
        var escapeSquareBracket = dialect is SqlDialect.Mssql;
        var likePatterns = configuredPrefixes
            .Select(prefix => EscapeLikePrefix(prefix, escapeSquareBracket) + "%")
            .ToArray();

        if (
            dialect is SqlDialect.Mssql
            && likePatterns.Length >= NamespacePrefixLimitExceededException.MssqlScalarParameterLimit
        )
        {
            throw new NamespacePrefixLimitExceededException(likePatterns.Length);
        }

        return dialect switch
        {
            SqlDialect.Pgsql => new NamespacePrefixParameterization(
                NamespacePrefixParameterizationKind.PgsqlArray,
                baseParameterName,
                configuredPrefixes,
                likePatterns,
                [baseParameterName]
            ),
            SqlDialect.Mssql => new NamespacePrefixParameterization(
                NamespacePrefixParameterizationKind.MssqlScalar,
                baseParameterName,
                configuredPrefixes,
                likePatterns,
                [
                    .. Enumerable
                        .Range(0, likePatterns.Length)
                        .Select(index => CreateScalarParameterName(baseParameterName, index)),
                ]
            ),
            _ => throw new NotSupportedException(
                $"Namespace prefix parameterization does not support SQL dialect '{dialect}'."
            ),
        };
    }

    public static bool TryCreate(
        SqlDialect dialect,
        IReadOnlyList<string> namespacePrefixes,
        string baseParameterName,
        out NamespacePrefixParameterization parameterization,
        out string securityConfigurationMessage,
        out NamespacePrefixParameterizationFailureKind? failureKind
    )
    {
        if (namespacePrefixes.Any(string.IsNullOrEmpty))
        {
            parameterization = null!;
            securityConfigurationMessage =
                NamespaceAuthorizationSecurityConfigurationMessages.InvalidNamespacePrefix;
            failureKind = NamespacePrefixParameterizationFailureKind.InvalidNamespacePrefix;
            return false;
        }

        try
        {
            parameterization = Create(dialect, namespacePrefixes, baseParameterName);
            securityConfigurationMessage = string.Empty;
            failureKind = null;
            return true;
        }
        catch (NamespacePrefixLimitExceededException ex)
        {
            parameterization = null!;
            securityConfigurationMessage =
                NamespaceAuthorizationSecurityConfigurationMessages.PrefixCapExceeded(ex.PrefixCount);
            failureKind = NamespacePrefixParameterizationFailureKind.PrefixCapExceeded;
            return false;
        }
    }

    private static string CreateScalarParameterName(string baseParameterName, int index)
    {
        var parameterName = string.Create(CultureInfo.InvariantCulture, $"{baseParameterName}_{index}");

        PlanSqlWriterExtensions.ValidateBareParameterName(parameterName, nameof(baseParameterName));
        return parameterName;
    }

    /// <summary>
    /// Escapes <c>LIKE</c> metacharacters in a prefix so it matches literally. Always escapes the escape
    /// character itself plus <c>%</c> and <c>_</c>; also escapes <c>[</c> for SQL Server, whose <c>LIKE</c>
    /// treats it as a character-class opener.
    /// </summary>
    private static string EscapeLikePrefix(string prefix, bool escapeSquareBracket)
    {
        var builder = new StringBuilder(prefix.Length + 8);

        foreach (var character in prefix)
        {
            if (character is LikeEscapeCharacter or '%' or '_' || (escapeSquareBracket && character is '['))
            {
                builder.Append(LikeEscapeCharacter);
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
