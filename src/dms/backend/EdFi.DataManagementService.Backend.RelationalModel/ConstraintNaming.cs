// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.Backend.RelationalModel;

internal static class ConstraintNaming
{
    internal const string NaturalKeyToken = "NK";
    internal const string ReferenceKeyToken = "RefKey";
    internal const string AllNoneToken = "AllNone";
    internal const string DocumentToken = "Document";

    private const int HashSegmentLength = 10;
    private const string DescriptorIdSuffix = "_DescriptorId";

    internal static string BuildNaturalKeyUniqueName(DbTableName table)
    {
        return BuildName("UX", table, [NaturalKeyToken]);
    }

    internal static string BuildReferenceKeyUniqueName(DbTableName table)
    {
        return BuildName("UX", table, [ReferenceKeyToken]);
    }

    internal static string BuildArrayUniquenessName(DbTableName table, IReadOnlyList<DbColumnName> columns)
    {
        var tokens = BuildColumnTokens(columns);
        return BuildName("UX", table, tokens);
    }

    internal static string BuildForeignKeyName(DbTableName table, params string[] tokens)
    {
        return BuildName("FK", table, tokens);
    }

    internal static string BuildReferenceForeignKeyName(
        DbTableName table,
        string referenceBaseName,
        bool isComposite
    )
    {
        return isComposite
            ? BuildName("FK", table, [referenceBaseName, ReferenceKeyToken])
            : BuildName("FK", table, [referenceBaseName]);
    }

    internal static string BuildDescriptorForeignKeyName(DbTableName table, DbColumnName descriptorColumn)
    {
        var baseName = TrimSuffix(descriptorColumn.Value, DescriptorIdSuffix);
        return BuildName("FK", table, [baseName]);
    }

    internal static string BuildAllOrNoneName(DbTableName table, string referenceBaseName)
    {
        return BuildName("CK", table, [referenceBaseName, AllNoneToken]);
    }

    internal static string ApplyDialectLimit(
        string baseName,
        ConstraintIdentity identity,
        ISqlDialectRules dialectRules
    )
    {
        ArgumentNullException.ThrowIfNull(baseName);
        ArgumentNullException.ThrowIfNull(dialectRules);

        var maxLength = dialectRules.MaxIdentifierLength;

        if (GetIdentifierLength(baseName, dialectRules) <= maxLength)
        {
            return baseName;
        }

        var hash = ComputeSha256Hex(BuildSignature(identity));
        var suffix = $"_{hash[..HashSegmentLength]}";
        var prefixLength = maxLength - suffix.Length;

        if (prefixLength <= 0)
        {
            return suffix.Length > maxLength ? suffix[..maxLength] : suffix;
        }

        var prefix = TruncateToLength(baseName, prefixLength, dialectRules);
        return $"{prefix}{suffix}";
    }

    internal static string BuildSignature(ConstraintIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        StringBuilder builder = new();
        builder.Append(identity.Kind);
        builder.Append('|');
        builder.Append(identity.Table.ToString());
        builder.Append('|');
        AppendColumns(builder, identity.Columns);

        if (identity.Kind == ConstraintIdentityKind.ForeignKey)
        {
            builder.Append('|');
            builder.Append(identity.TargetTable.GetValueOrDefault().ToString());
            builder.Append('|');
            AppendColumns(builder, identity.TargetColumns);
            builder.Append('|');
            builder.Append(identity.OnDelete);
            builder.Append('|');
            builder.Append(identity.OnUpdate);
        }

        if (identity.Kind == ConstraintIdentityKind.AllOrNone)
        {
            builder.Append('|');
            AppendColumns(builder, identity.DependentColumns);
        }

        return builder.ToString();
    }

    private static string BuildName(string prefix, DbTableName table, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException("Constraint prefix must be non-empty.", nameof(prefix));
        }

        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (tokens.Count == 0)
        {
            return $"{prefix}_{table.Name}";
        }

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ArgumentException("Constraint tokens must be non-empty.", nameof(tokens));
            }
        }

        return $"{prefix}_{table.Name}_{string.Join("_", tokens)}";
    }

    private static IReadOnlyList<string> BuildColumnTokens(IReadOnlyList<DbColumnName> columns)
    {
        if (columns.Count == 0)
        {
            throw new InvalidOperationException("Unique constraint must include at least one column.");
        }

        Dictionary<string, List<string>> suffixesByPrefix = new(StringComparer.Ordinal);
        List<string> prefixOrder = [];

        foreach (var column in columns)
        {
            var (prefix, suffix) = SplitColumnName(column.Value);

            if (!suffixesByPrefix.TryGetValue(prefix, out var suffixes))
            {
                suffixes = [];
                suffixesByPrefix[prefix] = suffixes;
                prefixOrder.Add(prefix);
            }

            suffixes.Add(suffix);
        }

        List<string> tokens = [];

        foreach (var prefix in prefixOrder)
        {
            var suffixes = suffixesByPrefix[prefix];

            if (string.IsNullOrEmpty(prefix))
            {
                tokens.AddRange(suffixes);
                continue;
            }

            tokens.Add(prefix);
            tokens.AddRange(suffixes);
        }

        return tokens;
    }

    private static (string Prefix, string Suffix) SplitColumnName(string columnName)
    {
        var splitIndex = columnName.LastIndexOf('_');

        if (splitIndex <= 0 || splitIndex == columnName.Length - 1)
        {
            return (string.Empty, columnName);
        }

        return (columnName[..splitIndex], columnName[(splitIndex + 1)..]);
    }

    private static string TrimSuffix(string value, string suffix)
    {
        if (!value.EndsWith(suffix, StringComparison.Ordinal))
        {
            return value;
        }

        return value[..^suffix.Length];
    }

    private static void AppendColumns(StringBuilder builder, IReadOnlyList<DbColumnName> columns)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(columns[index].Value);
        }
    }

    private static int GetIdentifierLength(string value, ISqlDialectRules dialectRules)
    {
        return dialectRules.Dialect switch
        {
            SqlDialect.Pgsql => Encoding.UTF8.GetByteCount(value),
            SqlDialect.Mssql => value.Length,
            _ => value.Length,
        };
    }

    private static string TruncateToLength(string value, int maxLength, ISqlDialectRules dialectRules)
    {
        if (value.Length == 0 || maxLength <= 0)
        {
            return string.Empty;
        }

        return dialectRules.Dialect == SqlDialect.Pgsql ? TruncateToUtf8Bytes(value, maxLength)
            : value.Length <= maxLength ? value
            : value[..maxLength];
    }

    private static string ComputeSha256Hex(string signature)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(signature);
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
