// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Builds the PostgreSQL natural-key reference-lookup command: one command per batch, one statement (and
/// therefore one result set) per target group, in group order.
/// </summary>
/// <remarks>
/// Each group's input is a set of parallel arrays — one array parameter per probe column — expanded with
/// <c>unnest(...) WITH ORDINALITY</c>. That keeps the SQL text independent of the entry count (so it
/// compiles once and caches), keeps the parameter count independent of the entry count, and supplies the
/// request ordinal from the array position rather than a fabricated row number.
/// </remarks>
internal static class PostgresqlNaturalKeyLookupCommandBuilder
{
    private static readonly ConditionalWeakTable<
        MappingSet,
        ConcurrentDictionary<string, string>
    > CommandTextByShapeByMappingSet = new();

    public static RelationalCommand Build(NaturalKeyLookupBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        NaturalKeyLookupCommandSupport.ValidateBatch(batch);

        return new RelationalCommand(BuildCommandText(batch), BuildParameters(batch));
    }

    private static string BuildCommandText(NaturalKeyLookupBatch batch)
    {
        var shapeKey = NaturalKeyLookupCommandSupport.BuildShapeKey(batch, includeEntryCounts: false);
        var commandTextByShape = CommandTextByShapeByMappingSet.GetValue(
            batch.MappingSet,
            static _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal)
        );

        return commandTextByShape.GetOrAdd(
            shapeKey,
            static (_, staticBatch) => BuildStatements(staticBatch),
            batch
        );
    }

    private static string BuildStatements(NaturalKeyLookupBatch batch)
    {
        List<string> statements = new(batch.Groups.Count);

        for (var groupIndex = 0; groupIndex < batch.Groups.Count; groupIndex++)
        {
            statements.Add(
                batch.Groups[groupIndex] switch
                {
                    NaturalKeyProbeLookupGroup probeGroup => BuildProbeGroupSql(
                        batch.MappingSet,
                        probeGroup,
                        groupIndex
                    ),
                    DescriptorLookupGroup => BuildDescriptorGroupSql(batch.MappingSet, groupIndex),
                    var group => throw new NotSupportedException(
                        $"Unsupported natural-key lookup group kind '{group.GetType().Name}'."
                    ),
                }
            );
        }

        return string.Join(";" + Environment.NewLine + Environment.NewLine, statements);
    }

    private static string BuildProbeGroupSql(
        MappingSet mappingSet,
        NaturalKeyProbeLookupGroup group,
        int groupIndex
    )
    {
        var probe = group.Probe;
        StringBuilder builder = new();

        builder.Append(
            $"SELECT input.{Quote(NaturalKeyLookupColumns.Ordinal)} AS {Quote(NaturalKeyLookupColumns.Ordinal)}"
        );
        builder.Append(
            $", {NaturalKeyLookupCommandSupport.TargetAlias}.{Quote(probe.DocumentIdColumn.Value)} AS {Quote(NaturalKeyLookupColumns.DocumentId)}"
        );

        if (probe.IsAbstract)
        {
            // The abstract identity table names the concrete subtype in its discriminator column, in
            // project-colon-resource form; the caller needs it to validate the reference target.
            builder.Append(
                $", {NaturalKeyLookupCommandSupport.TargetAlias}.{Quote(NaturalKeyLookupColumns.Discriminator)} AS {Quote(NaturalKeyLookupColumns.Discriminator)}"
            );
        }

        builder.AppendLine();
        AppendUnnestInput(builder, groupIndex, [.. probe.Columns.Select(ResolveArrayType)]);
        AppendDescriptorPartJoins(builder, mappingSet, probe);

        builder.AppendLine(
            $"INNER JOIN {QuoteTableName(probe.ProbeTable)} {NaturalKeyLookupCommandSupport.TargetAlias}"
        );

        for (var columnIndex = 0; columnIndex < probe.Columns.Count; columnIndex++)
        {
            var column = probe.Columns[columnIndex];
            var keyword = columnIndex == 0 ? "    ON " : "    AND ";
            var right = column.DescriptorResource is null
                ? $"input.{Quote(NaturalKeyLookupCommandSupport.InputColumnAlias(columnIndex))}"
                : $"{NaturalKeyLookupCommandSupport.DescriptorPartAlias(columnIndex)}.{Quote(NaturalKeyLookupColumns.DocumentId)}";

            builder.Append(keyword);
            builder.Append(
                $"{NaturalKeyLookupCommandSupport.TargetAlias}.{Quote(column.StorageColumn.Value)} = {right}"
            );

            if (columnIndex < probe.Columns.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string BuildDescriptorGroupSql(MappingSet mappingSet, int groupIndex)
    {
        var descriptorProbe = mappingSet.DescriptorProbeTarget;
        var alias = NaturalKeyLookupCommandSupport.DescriptorTargetAlias;
        StringBuilder builder = new();

        builder.Append(
            $"SELECT input.{Quote(NaturalKeyLookupColumns.Ordinal)} AS {Quote(NaturalKeyLookupColumns.Ordinal)}"
        );
        builder.Append(
            $", {alias}.{Quote(NaturalKeyLookupColumns.DocumentId)} AS {Quote(NaturalKeyLookupColumns.DocumentId)}"
        );
        builder.Append(
            $", {alias}.{Quote(descriptorProbe.DiscriminatorColumn.Value)} AS {Quote(NaturalKeyLookupColumns.Discriminator)}"
        );
        builder.AppendLine(
            $", {alias}.{Quote(NaturalKeyLookupColumns.ResourceKeyId)} AS {Quote(NaturalKeyLookupColumns.ResourceKeyId)}"
        );

        AppendUnnestInput(builder, groupIndex, [DescriptorUriArrayType]);

        builder.AppendLine($"INNER JOIN {QuoteTableName(descriptorProbe.Table)} {alias}");

        // Deliberately no discriminator predicate: seeking UriLowered alone is a prefix seek of
        // UX_Descriptor_UriLowered_Discriminator and still returns the row for a URI that resolves to the
        // wrong descriptor type, which is what lets the caller report DescriptorTypeMismatch rather than
        // a bare Missing.
        builder.Append(
            $"    ON {alias}.{Quote(descriptorProbe.UriLoweredColumn.Value)} = input.{Quote(NaturalKeyLookupCommandSupport.InputColumnAlias(0))}"
        );

        return builder.ToString();
    }

    private static void AppendUnnestInput(
        StringBuilder builder,
        int groupIndex,
        IReadOnlyList<PostgresqlArrayType> arrayTypes
    )
    {
        builder.Append("FROM unnest(");
        builder.AppendJoin(
            ", ",
            arrayTypes.Select(
                (arrayType, columnIndex) =>
                    $"{ParameterName(groupIndex, columnIndex)}::{arrayType.SqlTypeName}[]"
            )
        );
        builder.Append(") WITH ORDINALITY AS input(");
        builder.AppendJoin(
            ", ",
            Enumerable
                .Range(0, arrayTypes.Count)
                .Select(columnIndex => Quote(NaturalKeyLookupCommandSupport.InputColumnAlias(columnIndex)))
                .Append(Quote(NaturalKeyLookupColumns.Ordinal))
        );
        builder.AppendLine(")");
    }

    private static void AppendDescriptorPartJoins(
        StringBuilder builder,
        MappingSet mappingSet,
        NaturalKeyProbeTarget probe
    )
    {
        var descriptorProbe = mappingSet.DescriptorProbeTarget;

        for (var columnIndex = 0; columnIndex < probe.Columns.Count; columnIndex++)
        {
            if (probe.Columns[columnIndex].DescriptorResource is not { } descriptorResource)
            {
                continue;
            }

            var alias = NaturalKeyLookupCommandSupport.DescriptorPartAlias(columnIndex);
            var discriminatorLiteral = NaturalKeyLookupCommandSupport.DescriptorDiscriminatorLiteralOrThrow(
                mappingSet,
                descriptorResource
            );

            // Resolved before the target join so the target's ON clause carries every RefKey column and
            // can seek the index. A URI that resolves to nothing makes the whole reference a miss, which
            // is correct: an unresolvable descriptor part means an unresolvable reference.
            builder.AppendLine($"INNER JOIN {QuoteTableName(descriptorProbe.Table)} {alias}");
            builder.AppendLine(
                $"    ON {alias}.{Quote(descriptorProbe.UriLoweredColumn.Value)} = input.{Quote(NaturalKeyLookupCommandSupport.InputColumnAlias(columnIndex))}"
            );
            builder.AppendLine(
                $"    AND {alias}.{Quote(descriptorProbe.DiscriminatorColumn.Value)} = '{EscapeSqlLiteral(discriminatorLiteral)}'"
            );
        }
    }

    private static IReadOnlyList<RelationalParameter> BuildParameters(NaturalKeyLookupBatch batch)
    {
        List<RelationalParameter> parameters = [];

        for (var groupIndex = 0; groupIndex < batch.Groups.Count; groupIndex++)
        {
            var group = batch.Groups[groupIndex];
            var arrayTypes = group switch
            {
                NaturalKeyProbeLookupGroup probeGroup => (IReadOnlyList<PostgresqlArrayType>)
                    [.. probeGroup.Probe.Columns.Select(ResolveArrayType)],
                DescriptorLookupGroup => [DescriptorUriArrayType],
                _ => throw new NotSupportedException(
                    $"Unsupported natural-key lookup group kind '{group.GetType().Name}'."
                ),
            };

            for (var columnIndex = 0; columnIndex < arrayTypes.Count; columnIndex++)
            {
                var arrayDbType = (NpgsqlDbType)(
                    (int)NpgsqlDbType.Array | (int)arrayTypes[columnIndex].ElementDbType
                );

                parameters.Add(
                    new RelationalParameter(
                        ParameterName(groupIndex, columnIndex),
                        BuildValueArray(group.Entries, columnIndex, arrayTypes[columnIndex].ElementDbType),
                        parameter =>
                        {
                            if (parameter is not NpgsqlParameter npgsqlParameter)
                            {
                                throw new InvalidOperationException(
                                    "PostgreSQL natural-key lookup parameter configuration requires an NpgsqlParameter instance."
                                );
                            }

                            npgsqlParameter.NpgsqlDbType = arrayDbType;
                        }
                    )
                );
            }
        }

        return parameters;
    }

    private static Array BuildValueArray(
        IReadOnlyList<NaturalKeyLookupEntry> entries,
        int columnIndex,
        NpgsqlDbType elementDbType
    ) =>
        elementDbType switch
        {
            NpgsqlDbType.Varchar => BuildTypedArray(entries, columnIndex, RelationalProbeValue.ToStringValue),
            NpgsqlDbType.Integer => BuildTypedArray(entries, columnIndex, RelationalProbeValue.ToInt32),
            NpgsqlDbType.Bigint => BuildTypedArray(entries, columnIndex, RelationalProbeValue.ToInt64),
            NpgsqlDbType.Numeric => BuildTypedArray(entries, columnIndex, RelationalProbeValue.ToDecimal),
            NpgsqlDbType.Boolean => BuildTypedArray(entries, columnIndex, RelationalProbeValue.ToBoolean),
            NpgsqlDbType.Date => BuildTypedArray(entries, columnIndex, RelationalProbeValue.ToDate),
            NpgsqlDbType.TimestampTz => BuildTypedArray(
                entries,
                columnIndex,
                RelationalProbeValue.ToDateTime
            ),
            NpgsqlDbType.Time => BuildTypedArray(entries, columnIndex, RelationalProbeValue.ToTime),
            _ => throw new NotSupportedException(
                $"PostgreSQL natural-key lookup does not support array element type '{elementDbType}'."
            ),
        };

    private static TValue[] BuildTypedArray<TValue>(
        IReadOnlyList<NaturalKeyLookupEntry> entries,
        int columnIndex,
        Func<object, int, TValue> convert
    )
    {
        var values = new TValue[entries.Count];

        for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
        {
            values[entryIndex] = convert(entries[entryIndex].Values[columnIndex], columnIndex);
        }

        return values;
    }

    private static readonly PostgresqlArrayType DescriptorUriArrayType = new("varchar", NpgsqlDbType.Varchar);

    private static PostgresqlArrayType ResolveArrayType(NaturalKeyProbeColumn column) =>
        column.DescriptorResource is not null
            ? DescriptorUriArrayType
            : column.ScalarType.Kind switch
            {
                ScalarKind.String => new PostgresqlArrayType("varchar", NpgsqlDbType.Varchar),
                ScalarKind.Int32 => new PostgresqlArrayType("integer", NpgsqlDbType.Integer),
                ScalarKind.Int64 => new PostgresqlArrayType("bigint", NpgsqlDbType.Bigint),
                ScalarKind.Decimal => new PostgresqlArrayType("numeric", NpgsqlDbType.Numeric),
                ScalarKind.Boolean => new PostgresqlArrayType("boolean", NpgsqlDbType.Boolean),
                ScalarKind.Date => new PostgresqlArrayType("date", NpgsqlDbType.Date),
                // dms/edfi DateTime columns are `timestamp with time zone` (SqlDialectRules), so the probe
                // array must be timestamptz or the comparison silently reinterprets the instant.
                ScalarKind.DateTime => new PostgresqlArrayType("timestamptz", NpgsqlDbType.TimestampTz),
                ScalarKind.Time => new PostgresqlArrayType("time", NpgsqlDbType.Time),
                var kind => throw new NotSupportedException(
                    $"PostgreSQL natural-key lookup does not support scalar kind '{kind}'."
                ),
            };

    private static string ParameterName(int groupIndex, int columnIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"@g{groupIndex}c{columnIndex}");

    private static string QuoteTableName(DbTableName tableName) =>
        $"{Quote(tableName.Schema.Value)}.{Quote(tableName.Name)}";

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string EscapeSqlLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private readonly record struct PostgresqlArrayType(string SqlTypeName, NpgsqlDbType ElementDbType);
}
