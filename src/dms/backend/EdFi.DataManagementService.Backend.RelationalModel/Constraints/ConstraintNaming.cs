// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Backend.RelationalModel.Constraints;

/// <summary>
/// Builds conventional constraint names and applies dialect length limits via stable hashing.
/// </summary>
internal static class ConstraintNaming
{
    internal const string NaturalKeyToken = "NK";
    internal const string ReferenceKeyToken = "RefKey";
    internal const string AllNoneToken = "AllNone";
    internal const string DocumentToken = "Document";

    private const string DescriptorIdSuffix = "_DescriptorId";

    /// <summary>
    /// Builds a unique constraint name for a table natural key.
    /// </summary>
    internal static string BuildNaturalKeyUniqueName(DbTableName table)
    {
        return BuildName("UX", table, [NaturalKeyToken]);
    }

    /// <summary>
    /// Builds a unique constraint name for a reference key on a table.
    /// </summary>
    internal static string BuildReferenceKeyUniqueName(DbTableName table)
    {
        return BuildName("UX", table, [ReferenceKeyToken]);
    }

    /// <summary>
    /// Builds a unique constraint name for an array uniqueness constraint using the participating columns.
    /// </summary>
    internal static string BuildArrayUniquenessName(DbTableName table, IReadOnlyList<DbColumnName> columns)
    {
        var tokens = BuildColumnTokens(columns);
        return BuildName("UX", table, tokens);
    }

    /// <summary>
    /// Builds the primary key constraint name for a table.
    /// </summary>
    internal static string BuildPrimaryKeyName(DbTableName table)
    {
        return BuildName("PK", table, []);
    }

    /// <summary>
    /// Builds a foreign key constraint name using the supplied tokens.
    /// </summary>
    internal static string BuildForeignKeyName(DbTableName table, params string[] tokens)
    {
        return BuildName("FK", table, tokens);
    }

    /// <summary>
    /// Builds a foreign key constraint name for a reference binding, appending a reference-key token when
    /// the relationship is composite.
    /// </summary>
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

    /// <summary>
    /// Builds a foreign key constraint name for a descriptor reference by trimming the <c>_DescriptorId</c>
    /// suffix from the column name.
    /// </summary>
    internal static string BuildDescriptorForeignKeyName(DbTableName table, DbColumnName descriptorColumn)
    {
        var baseName = TrimSuffix(descriptorColumn.Value, DescriptorIdSuffix);
        return BuildName("FK", table, [baseName]);
    }

    /// <summary>
    /// Builds the all-or-none check constraint name for a reference binding.
    /// </summary>
    internal static string BuildAllOrNoneName(DbTableName table, string referenceBaseName)
    {
        return BuildName("CK", table, [referenceBaseName, AllNoneToken]);
    }

    /// <summary>
    /// Builds an FK-support index name following the <c>IX_{TableName}_{Col1}_{Col2}_...</c> convention,
    /// with columns listed in index key order.
    /// </summary>
    internal static string BuildForeignKeySupportIndexName(
        DbTableName table,
        IReadOnlyList<DbColumnName> keyColumns
    )
    {
        ArgumentNullException.ThrowIfNull(keyColumns);

        if (keyColumns.Count == 0)
        {
            throw new ArgumentException(
                "FK-support index must include at least one key column.",
                nameof(keyColumns)
            );
        }

        string[] columnTokens = [.. keyColumns.Select(c => c.Value)];
        return BuildName("IX", table, columnTokens);
    }

    /// <summary>
    /// Applies dialect identifier limits to a constraint name by shortening it with a signature hash.
    /// </summary>
    internal static string ApplyDialectLimit(
        string baseName,
        ConstraintIdentity identity,
        ISqlDialectRules dialectRules
    )
    {
        ArgumentNullException.ThrowIfNull(baseName);
        ArgumentNullException.ThrowIfNull(dialectRules);

        return SqlIdentifierShortening.ApplyDialectLimit(baseName, BuildSignature(identity), dialectRules);
    }

    /// <summary>
    /// Builds a stable signature string for a constraint identity to use as hash input.
    /// </summary>
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

    /// <summary>
    /// Builds a constraint name using a prefix and the table name, with optional token segments.
    /// </summary>
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

    /// <summary>
    /// Builds token segments for column-based unique constraints by grouping on the final underscore.
    /// </summary>
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

        prefixOrder.Sort(StringComparer.Ordinal);

        foreach (var suffixes in suffixesByPrefix.Values)
        {
            suffixes.Sort(StringComparer.Ordinal);
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

    /// <summary>
    /// Splits a column name into a prefix and suffix based on the final underscore.
    /// </summary>
    private static (string Prefix, string Suffix) SplitColumnName(string columnName)
    {
        var splitIndex = columnName.LastIndexOf('_');

        if (splitIndex <= 0 || splitIndex == columnName.Length - 1)
        {
            return (string.Empty, columnName);
        }

        return (columnName[..splitIndex], columnName[(splitIndex + 1)..]);
    }

    /// <summary>
    /// Trims a suffix from the supplied string when present.
    /// </summary>
    private static string TrimSuffix(string value, string suffix)
    {
        if (!value.EndsWith(suffix, StringComparison.Ordinal))
        {
            return value;
        }

        return value[..^suffix.Length];
    }

    /// <summary>
    /// Appends a comma-separated list of column values into a signature buffer.
    /// </summary>
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
}
