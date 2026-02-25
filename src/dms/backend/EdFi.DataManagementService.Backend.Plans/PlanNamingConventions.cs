// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Deterministic naming utilities used by plan compilation.
/// </summary>
public static class PlanNamingConventions
{
    private const string FallbackParameterName = "p";

    /// <summary>
    /// Derives deterministic write-parameter names from ordered write bindings.
    /// </summary>
    /// <param name="columnsInAuthoritativeOrder">
    /// Stored/writable columns in authoritative binding order.
    /// </param>
    public static IReadOnlyList<string> DeriveWriteParameterNamesInOrder(
        IReadOnlyList<DbColumnName> columnsInAuthoritativeOrder
    )
    {
        ArgumentNullException.ThrowIfNull(columnsInAuthoritativeOrder);

        var candidates = new string[columnsInAuthoritativeOrder.Count];

        for (var index = 0; index < columnsInAuthoritativeOrder.Count; index++)
        {
            candidates[index] = SanitizeBareParameterName(
                DeriveWriteParameterBaseName(columnsInAuthoritativeOrder[index])
            );
        }

        return DeduplicateCaseInsensitive(candidates);
    }

    /// <summary>
    /// Derives a write-parameter base name from a physical column name using a first-character camel transform.
    /// </summary>
    /// <param name="columnName">Physical column name.</param>
    public static string DeriveWriteParameterBaseName(DbColumnName columnName)
    {
        return CamelCaseFirstCharacter(columnName.Value);
    }

    /// <summary>
    /// Applies first-character camel casing while preserving all other characters (including underscores).
    /// </summary>
    /// <param name="value">Input value.</param>
    public static string CamelCaseFirstCharacter(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (!char.IsUpper(value[0]))
        {
            return value;
        }

        return string.Create(
            value.Length,
            value,
            static (buffer, state) =>
            {
                buffer[0] = char.ToLowerInvariant(state[0]);
                state.AsSpan(1).CopyTo(buffer[1..]);
            }
        );
    }

    /// <summary>
    /// Sanitizes a candidate bare parameter name to <c>^[a-zA-Z_][a-zA-Z0-9_]*$</c>.
    /// </summary>
    /// <param name="candidateName">Candidate bare parameter name.</param>
    /// <returns>A deterministic safe bare parameter name.</returns>
    public static string SanitizeBareParameterName(string candidateName)
    {
        ArgumentNullException.ThrowIfNull(candidateName);

        var builder = new StringBuilder(candidateName.Length);
        var previousWasUnderscore = false;

        foreach (var character in candidateName)
        {
            if (IsAsciiIdentifierPart(character))
            {
                if (character == '_')
                {
                    if (previousWasUnderscore)
                    {
                        continue;
                    }

                    previousWasUnderscore = true;
                    builder.Append(character);
                    continue;
                }

                previousWasUnderscore = false;
                builder.Append(character);
                continue;
            }

            if (previousWasUnderscore)
            {
                continue;
            }

            previousWasUnderscore = true;
            builder.Append('_');
        }

        if (builder.Length == 0)
        {
            return FallbackParameterName;
        }

        if (!IsAsciiIdentifierStart(builder[0]))
        {
            builder.Insert(0, '_');
        }

        var sanitized = builder.ToString();
        PlanSqlWriterExtensions.ValidateBareParameterName(sanitized, nameof(candidateName));

        return sanitized;
    }

    /// <summary>
    /// Applies deterministic case-insensitive de-duplication to ordered parameter names.
    /// </summary>
    /// <param name="orderedNames">Ordered names in authoritative enumeration order.</param>
    public static IReadOnlyList<string> DeduplicateCaseInsensitive(IReadOnlyList<string> orderedNames)
    {
        ArgumentNullException.ThrowIfNull(orderedNames);

        var nextSuffixByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var deduplicatedNames = new string[orderedNames.Count];

        for (var index = 0; index < orderedNames.Count; index++)
        {
            var name = orderedNames[index];

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(
                    $"Parameter name at index {index} cannot be null or whitespace.",
                    nameof(orderedNames)
                );
            }

            if (!nextSuffixByName.TryGetValue(name, out var nextSuffix))
            {
                deduplicatedNames[index] = name;
                nextSuffixByName[name] = 2;
                continue;
            }

            deduplicatedNames[index] = $"{name}_{nextSuffix}";
            nextSuffixByName[name] = nextSuffix + 1;
        }

        return deduplicatedNames;
    }

    /// <summary>
    /// Returns a deterministic fixed alias for a well-known SQL role.
    /// </summary>
    /// <param name="role">Alias role.</param>
    public static string GetFixedAlias(PlanSqlAliasRole role)
    {
        return role switch
        {
            PlanSqlAliasRole.Root => "r",
            PlanSqlAliasRole.Keyset => "k",
            PlanSqlAliasRole.Table => "t",
            PlanSqlAliasRole.Document => "doc",
            PlanSqlAliasRole.Descriptor => "d",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown SQL alias role."),
        };
    }

    /// <summary>
    /// Creates a deterministic allocator for repeated table aliases (<c>t0</c>, <c>t1</c>, ...).
    /// </summary>
    public static PlanSqlTableAliasAllocator CreateTableAliasAllocator()
    {
        return new PlanSqlTableAliasAllocator();
    }

    private static bool IsAsciiIdentifierStart(char character)
    {
        return character == '_'
            || (character >= 'a' && character <= 'z')
            || (character >= 'A' && character <= 'Z');
    }

    private static bool IsAsciiIdentifierPart(char character)
    {
        return IsAsciiIdentifierStart(character) || (character >= '0' && character <= '9');
    }
}

/// <summary>
/// Fixed SQL alias roles used by plan compilers.
/// </summary>
public enum PlanSqlAliasRole
{
    /// <summary>
    /// Root resource table alias.
    /// </summary>
    Root,

    /// <summary>
    /// Keyset table alias.
    /// </summary>
    Keyset,

    /// <summary>
    /// Generic single-table alias.
    /// </summary>
    Table,

    /// <summary>
    /// <c>dms.Document</c> table alias.
    /// </summary>
    Document,

    /// <summary>
    /// Descriptor projection/join alias.
    /// </summary>
    Descriptor,
}

/// <summary>
/// Deterministic allocator for repeated table aliases.
/// </summary>
public sealed class PlanSqlTableAliasAllocator
{
    private int _nextTableAliasOrdinal;

    /// <summary>
    /// Allocates the next table alias in deterministic sequence.
    /// </summary>
    public string AllocateNext()
    {
        var alias = $"{PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Table)}{_nextTableAliasOrdinal}";
        _nextTableAliasOrdinal++;

        return alias;
    }
}
