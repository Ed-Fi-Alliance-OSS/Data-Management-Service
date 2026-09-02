// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Dialect-specific runtime shape for the ownership-token list bound to the
/// <c>dms.Document.CreatedByOwnershipTokenId</c> membership predicate.
/// </summary>
public enum OwnershipTokenParameterizationKind
{
    /// <summary>One PostgreSQL array parameter carrying every ownership token.</summary>
    PgsqlArray,

    /// <summary>One SQL Server scalar parameter per ownership token, joined by <c>IN</c> at the SQL site.</summary>
    MssqlScalar,
}

public enum OwnershipTokenParameterizationFailureKind
{
    /// <summary>The token list reached the provider-independent defensive limit.</summary>
    TokenCapExceeded,
}

/// <summary>
/// Dialect-specific ownership-token parameterization.
/// </summary>
/// <param name="Kind">The emitted SQL/binding shape for the token list.</param>
/// <param name="BaseParameterName">The logical base parameter name.</param>
/// <param name="TokensInOrder">
/// The caller's ownership tokens, deduplicated and ascending. These are the SQL-bound values. They are never
/// rendered into a client response — an ownership denial discloses no token value.
/// </param>
/// <param name="ParameterNamesInOrder">Concrete SQL parameter names in deterministic binding order.</param>
/// <remarks>
/// An empty token list is a valid parameterization, not an error. A client configured for
/// <c>OwnershipBased</c> with no tokens still executes the stored-row check so the response can distinguish a
/// stored null (§2.14) from a non-matching stored value (§2.13); the compiler renders the membership
/// predicate as a constant false in that case. This is why there is no minimum-count guard here, unlike
/// <see cref="NamespacePrefixParameterization"/>, whose empty case is a §2.9 preflight denial instead.
/// </remarks>
public sealed record OwnershipTokenParameterization(
    OwnershipTokenParameterizationKind Kind,
    string BaseParameterName,
    IReadOnlyList<short> TokensInOrder,
    IReadOnlyList<string> ParameterNamesInOrder
)
{
    /// <summary>
    /// Whether the emitted membership predicate can ever match a row. False for an empty token list, which
    /// the SQL compiler renders as a constant-false predicate rather than an empty <c>IN ()</c> or an
    /// untyped empty array.
    /// </summary>
    public bool MatchesNoToken => TokensInOrder.Count == 0;
}

/// <summary>
/// Builds the ownership-token parameterization for a SQL dialect, failing closed at the defensive token
/// limit on every provider.
/// </summary>
public static class OwnershipTokenParameterizationFactory
{
    public static OwnershipTokenParameterization Create(
        SqlDialect dialect,
        IReadOnlyList<short> ownershipTokenIds,
        string baseParameterName
    )
    {
        ArgumentNullException.ThrowIfNull(ownershipTokenIds);
        PlanSqlWriterExtensions.ValidateBareParameterName(baseParameterName, nameof(baseParameterName));

        // Guarded before deduplication, and deliberately so. A limit applied to the deduplicated count would
        // be data-dependent: 2,500 configured tokens containing 600 duplicates would slip through, which is
        // the opposite of what a defensive bound is for. The count that matters is what the client was
        // configured with.
        if (ownershipTokenIds.Count >= OwnershipTokenLimitExceededException.OwnershipTokenLimit)
        {
            throw new OwnershipTokenLimitExceededException(ownershipTokenIds.Count);
        }

        var tokens = ownershipTokenIds.Distinct().Order().ToArray();

        return dialect switch
        {
            SqlDialect.Pgsql => new OwnershipTokenParameterization(
                OwnershipTokenParameterizationKind.PgsqlArray,
                baseParameterName,
                tokens,
                [baseParameterName]
            ),
            SqlDialect.Mssql => new OwnershipTokenParameterization(
                OwnershipTokenParameterizationKind.MssqlScalar,
                baseParameterName,
                tokens,
                [
                    .. Enumerable
                        .Range(0, tokens.Length)
                        .Select(index => CreateScalarParameterName(baseParameterName, index)),
                ]
            ),
            _ => throw new NotSupportedException(
                $"Ownership token parameterization does not support SQL dialect '{dialect}'."
            ),
        };
    }

    public static bool TryCreate(
        SqlDialect dialect,
        IReadOnlyList<short> ownershipTokenIds,
        string baseParameterName,
        out OwnershipTokenParameterization parameterization,
        out string securityConfigurationMessage,
        out OwnershipTokenParameterizationFailureKind? failureKind
    )
    {
        try
        {
            parameterization = Create(dialect, ownershipTokenIds, baseParameterName);
            securityConfigurationMessage = string.Empty;
            failureKind = null;
            return true;
        }
        catch (OwnershipTokenLimitExceededException ex)
        {
            parameterization = null!;
            securityConfigurationMessage =
                OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(ex.OwnershipTokenCount);
            failureKind = OwnershipTokenParameterizationFailureKind.TokenCapExceeded;
            return false;
        }
    }

    private static string CreateScalarParameterName(string baseParameterName, int index)
    {
        var parameterName = string.Create(CultureInfo.InvariantCulture, $"{baseParameterName}_{index}");

        PlanSqlWriterExtensions.ValidateBareParameterName(parameterName, nameof(baseParameterName));
        return parameterName;
    }
}
