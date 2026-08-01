// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// Builds the SQL Server natural-key reference-lookup command: one command per batch, one statement (and
/// therefore one result set) per target group, in group order.
/// </summary>
/// <remarks>
/// Each group's input is a typed <c>VALUES</c> derived table — one row per entry, one parameter per probe
/// value, with the request ordinal written as an inline literal rather than a fabricated
/// <c>ROW_NUMBER()</c>. No <c>OPENJSON</c> and no table-valued parameter: both would require either a
/// server-side type dependency or a JSON round trip for what is a small, index-seekable key list.
/// </remarks>
internal static class MssqlNaturalKeyLookupCommandBuilder
{
    /// <summary>
    /// The largest number of parameters a single <c>VALUES</c> chunk may bind, with headroom under SQL
    /// Server's 2100-parameter ceiling.
    /// </summary>
    /// <remarks>
    /// A group wider than this is split into additional <c>VALUES</c> clauses, <c>UNION ALL</c>-ed inside
    /// the same statement so the group still yields exactly one result set and the batch still costs one
    /// round trip. Ordinals continue across the chunks.
    ///
    /// Note what this does and does not buy: SQL Server's 2100-parameter limit applies to the whole
    /// command, not to each statement or chunk, so chunking bounds the size of any one <c>VALUES</c>
    /// clause (SQL Server also caps a <c>VALUES</c> clause at 1000 rows) but cannot by itself keep a very
    /// large batch under the driver ceiling. Bounding the batch is the caller's job — the resolver groups
    /// at most ~100 references per target.
    /// </remarks>
    internal const int MssqlParameterBudget = 2000;

    /// <summary>
    /// The largest number of parameters one command may bind: SQL Server's 2100-parameter ceiling less the
    /// two that <c>sp_executesql</c> consumes for the statement text and the parameter declaration.
    /// </summary>
    /// <remarks>
    /// Chunking cannot enforce this — the ceiling applies to the command, not to each statement — so the
    /// batch itself has to be small enough. The caller sizes batches with
    /// <see cref="TotalParameterCount(NaturalKeyLookupBatch)"/>; this guard is the backstop that turns an
    /// oversized batch into a diagnosable build-time failure instead of a driver error at execution.
    /// </remarks>
    internal const int MssqlMaxCommandParameters = 2098;

    private static readonly ConditionalWeakTable<
        MappingSet,
        ConcurrentDictionary<string, string>
    > CommandTextByShapeByMappingSet = new();

    public static RelationalCommand Build(NaturalKeyLookupBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        NaturalKeyLookupCommandSupport.ValidateBatch(batch);
        EnsureSupportedParameterCount(batch);

        return new RelationalCommand(BuildCommandText(batch), BuildParameters(batch));
    }

    /// <summary>
    /// The number of entries one <c>VALUES</c> chunk holds for a probe of the given width.
    /// </summary>
    internal static int ChunkEntryCount(int probeValueCount) =>
        Math.Max(1, MssqlParameterBudget / probeValueCount);

    /// <summary>
    /// The number of parameters <paramref name="batch"/> would bind — the sum, over every group, of its
    /// entry count times its probe width. Chunking does not change this total.
    /// </summary>
    /// <remarks>
    /// Callers building a batch should keep this at or below <see cref="MssqlMaxCommandParameters"/>,
    /// splitting into additional batches when a group set would exceed it.
    /// </remarks>
    internal static int TotalParameterCount(NaturalKeyLookupBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        long totalParameterCount = 0;

        foreach (var group in batch.Groups)
        {
            totalParameterCount +=
                (long)group.Entries.Count * NaturalKeyLookupCommandSupport.ProbeValueCount(group);
        }

        return (int)Math.Min(totalParameterCount, int.MaxValue);
    }

    private static void EnsureSupportedParameterCount(NaturalKeyLookupBatch batch)
    {
        var totalParameterCount = TotalParameterCount(batch);

        if (totalParameterCount <= MssqlMaxCommandParameters)
        {
            return;
        }

        throw new ArgumentOutOfRangeException(
            nameof(batch),
            totalParameterCount,
            $"SQL Server natural-key lookup supports at most {MssqlMaxCommandParameters} bound parameters per command. Split the batch into smaller batches."
        );
    }

    private static string BuildCommandText(NaturalKeyLookupBatch batch)
    {
        var shapeKey = NaturalKeyLookupCommandSupport.BuildShapeKey(batch, includeEntryCounts: true);
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
            statements.Add(BuildGroupSql(batch.MappingSet, batch.Groups[groupIndex], groupIndex));
        }

        return string.Join(";" + Environment.NewLine + Environment.NewLine, statements);
    }

    private static string BuildGroupSql(MappingSet mappingSet, NaturalKeyLookupGroup group, int groupIndex)
    {
        if (group.Entries.Count == 0)
        {
            return BuildEmptyGroupSql(group);
        }

        var valueCount = NaturalKeyLookupCommandSupport.ProbeValueCount(group);
        var chunkEntryCount = ChunkEntryCount(valueCount);
        List<string> chunks = [];

        for (var chunkStart = 0; chunkStart < group.Entries.Count; chunkStart += chunkEntryCount)
        {
            var chunkEnd = Math.Min(chunkStart + chunkEntryCount, group.Entries.Count);
            chunks.Add(BuildChunkSql(mappingSet, group, groupIndex, chunkStart, chunkEnd));
        }

        return string.Join(Environment.NewLine + "UNION ALL" + Environment.NewLine, chunks);
    }

    private static string BuildChunkSql(
        MappingSet mappingSet,
        NaturalKeyLookupGroup group,
        int groupIndex,
        int chunkStart,
        int chunkEnd
    )
    {
        StringBuilder builder = new();

        AppendProjection(builder, mappingSet, group);
        AppendValuesInput(
            builder,
            group,
            groupIndex,
            chunkStart,
            chunkEnd,
            NaturalKeyLookupCommandSupport.ProbeValueCount(group)
        );

        switch (group)
        {
            case NaturalKeyProbeLookupGroup probeGroup:
                AppendDescriptorPartJoins(builder, mappingSet, probeGroup.Probe);
                AppendTargetJoin(builder, probeGroup.Probe);
                break;
            case DescriptorLookupGroup:
                AppendDescriptorTargetJoin(builder, mappingSet);
                break;
            default:
                throw new NotSupportedException(
                    $"Unsupported natural-key lookup group kind '{group.GetType().Name}'."
                );
        }

        return builder.ToString();
    }

    private static void AppendProjection(
        StringBuilder builder,
        MappingSet mappingSet,
        NaturalKeyLookupGroup group
    )
    {
        builder.Append(
            $"SELECT input.{Quote(NaturalKeyLookupColumns.Ordinal)} AS {Quote(NaturalKeyLookupColumns.Ordinal)}"
        );

        switch (group)
        {
            case NaturalKeyProbeLookupGroup probeGroup:
                builder.Append(
                    $", {NaturalKeyLookupCommandSupport.TargetAlias}.{Quote(probeGroup.Probe.DocumentIdColumn.Value)} AS {Quote(NaturalKeyLookupColumns.DocumentId)}"
                );

                if (probeGroup.Probe.IsAbstract)
                {
                    builder.Append(
                        $", {NaturalKeyLookupCommandSupport.TargetAlias}.{Quote(NaturalKeyLookupColumns.Discriminator)} AS {Quote(NaturalKeyLookupColumns.Discriminator)}"
                    );
                }

                break;
            case DescriptorLookupGroup:
                var alias = NaturalKeyLookupCommandSupport.DescriptorTargetAlias;
                builder.Append(
                    $", {alias}.{Quote(NaturalKeyLookupColumns.DocumentId)} AS {Quote(NaturalKeyLookupColumns.DocumentId)}"
                );
                builder.Append(
                    $", {alias}.{Quote(mappingSet.DescriptorProbeTarget.DiscriminatorColumn.Value)} AS {Quote(NaturalKeyLookupColumns.Discriminator)}"
                );
                builder.Append(
                    $", {alias}.{Quote(NaturalKeyLookupColumns.ResourceKeyId)} AS {Quote(NaturalKeyLookupColumns.ResourceKeyId)}"
                );
                break;
            default:
                throw new NotSupportedException(
                    $"Unsupported natural-key lookup group kind '{group.GetType().Name}'."
                );
        }

        builder.AppendLine();
    }

    private static void AppendValuesInput(
        StringBuilder builder,
        NaturalKeyLookupGroup group,
        int groupIndex,
        int chunkStart,
        int chunkEnd,
        int valueCount
    )
    {
        builder.AppendLine("FROM (VALUES");

        for (var entryIndex = chunkStart; entryIndex < chunkEnd; entryIndex++)
        {
            builder.Append("    (");
            builder.Append(group.Entries[entryIndex].Ordinal.ToString(CultureInfo.InvariantCulture));

            for (var columnIndex = 0; columnIndex < valueCount; columnIndex++)
            {
                builder.Append(", ").Append(ParameterName(groupIndex, entryIndex, columnIndex));
            }

            builder.Append(')');
            builder.AppendLine(entryIndex < chunkEnd - 1 ? "," : string.Empty);
        }

        builder.Append(") AS input(").Append(Quote(NaturalKeyLookupColumns.Ordinal));

        for (var columnIndex = 0; columnIndex < valueCount; columnIndex++)
        {
            builder.Append(", ").Append(Quote(NaturalKeyLookupCommandSupport.InputColumnAlias(columnIndex)));
        }

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
                $"    AND {alias}.{Quote(descriptorProbe.DiscriminatorColumn.Value)} = N'{EscapeSqlLiteral(discriminatorLiteral)}'"
            );
        }
    }

    private static void AppendTargetJoin(StringBuilder builder, NaturalKeyProbeTarget probe)
    {
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
    }

    private static void AppendDescriptorTargetJoin(StringBuilder builder, MappingSet mappingSet)
    {
        var descriptorProbe = mappingSet.DescriptorProbeTarget;
        var alias = NaturalKeyLookupCommandSupport.DescriptorTargetAlias;

        builder.AppendLine($"INNER JOIN {QuoteTableName(descriptorProbe.Table)} {alias}");

        // Deliberately no discriminator predicate: seeking UriLowered alone is a prefix seek of
        // UX_Descriptor_UriLowered_Discriminator and still returns the row for a URI that resolves to the
        // wrong descriptor type, which is what lets the caller report DescriptorTypeMismatch rather than
        // a bare Missing.
        builder.Append(
            $"    ON {alias}.{Quote(descriptorProbe.UriLoweredColumn.Value)} = input.{Quote(NaturalKeyLookupCommandSupport.InputColumnAlias(0))}"
        );
    }

    /// <summary>
    /// SQL Server has no empty <c>VALUES</c> clause, but an empty group still owes the reader a result set
    /// in group order, so it becomes a typed no-row projection.
    /// </summary>
    private static string BuildEmptyGroupSql(NaturalKeyLookupGroup group)
    {
        StringBuilder builder = new();

        builder.Append(
            $"SELECT CAST(NULL AS int) AS {Quote(NaturalKeyLookupColumns.Ordinal)}, CAST(NULL AS bigint) AS {Quote(NaturalKeyLookupColumns.DocumentId)}"
        );

        switch (group)
        {
            case NaturalKeyProbeLookupGroup { Probe.IsAbstract: true }:
                builder.Append(
                    $", CAST(NULL AS nvarchar(256)) AS {Quote(NaturalKeyLookupColumns.Discriminator)}"
                );
                break;
            case DescriptorLookupGroup:
                builder.Append(
                    $", CAST(NULL AS nvarchar(128)) AS {Quote(NaturalKeyLookupColumns.Discriminator)}"
                );
                builder.Append($", CAST(NULL AS smallint) AS {Quote(NaturalKeyLookupColumns.ResourceKeyId)}");
                break;
            default:
                break;
        }

        builder.AppendLine();
        builder.Append("WHERE 1 = 0");

        return builder.ToString();
    }

    private static IReadOnlyList<RelationalParameter> BuildParameters(NaturalKeyLookupBatch batch)
    {
        List<RelationalParameter> parameters = [];

        for (var groupIndex = 0; groupIndex < batch.Groups.Count; groupIndex++)
        {
            var group = batch.Groups[groupIndex];
            var parameterTypes = ResolveParameterTypes(group);

            for (var entryIndex = 0; entryIndex < group.Entries.Count; entryIndex++)
            {
                var entry = group.Entries[entryIndex];

                for (var columnIndex = 0; columnIndex < parameterTypes.Count; columnIndex++)
                {
                    var parameterType = parameterTypes[columnIndex];

                    parameters.Add(
                        new RelationalParameter(
                            ParameterName(groupIndex, entryIndex, columnIndex),
                            ConvertValue(entry.Values[columnIndex], columnIndex, parameterType.SqlDbType),
                            parameter => ConfigureParameter(parameter, parameterType)
                        )
                    );
                }
            }
        }

        return parameters;
    }

    private static IReadOnlyList<MssqlParameterType> ResolveParameterTypes(NaturalKeyLookupGroup group) =>
        group switch
        {
            NaturalKeyProbeLookupGroup probeGroup =>
            [
                .. probeGroup.Probe.Columns.Select(ResolveParameterType),
            ],
            DescriptorLookupGroup => [DescriptorUriParameterType],
            _ => throw new NotSupportedException(
                $"Unsupported natural-key lookup group kind '{group.GetType().Name}'."
            ),
        };

    private static readonly MssqlParameterType DescriptorUriParameterType = new(
        SqlDbType.NVarChar,
        NaturalKeyLookupCommandSupport.DescriptorUriMaxLength,
        null,
        null
    );

    private static MssqlParameterType ResolveParameterType(NaturalKeyProbeColumn column) =>
        column.DescriptorResource is not null
            ? DescriptorUriParameterType
            : column.ScalarType.Kind switch
            {
                ScalarKind.String => new MssqlParameterType(
                    SqlDbType.NVarChar,
                    column.ScalarType.MaxLength ?? -1,
                    null,
                    null
                ),
                ScalarKind.Int32 => new MssqlParameterType(SqlDbType.Int, null, null, null),
                ScalarKind.Int64 => new MssqlParameterType(SqlDbType.BigInt, null, null, null),
                ScalarKind.Decimal => new MssqlParameterType(
                    SqlDbType.Decimal,
                    null,
                    (byte?)column.ScalarType.Decimal?.Precision,
                    (byte?)column.ScalarType.Decimal?.Scale
                ),
                ScalarKind.Boolean => new MssqlParameterType(SqlDbType.Bit, null, null, null),
                ScalarKind.Date => new MssqlParameterType(SqlDbType.Date, null, null, null),
                ScalarKind.DateTime => new MssqlParameterType(SqlDbType.DateTime2, null, null, null),
                ScalarKind.Time => new MssqlParameterType(SqlDbType.Time, null, null, null),
                var kind => throw new NotSupportedException(
                    $"SQL Server natural-key lookup does not support scalar kind '{kind}'."
                ),
            };

    private static object ConvertValue(object value, int columnIndex, SqlDbType sqlDbType) =>
        sqlDbType switch
        {
            SqlDbType.NVarChar => RelationalProbeValue.ToStringValue(value, columnIndex),
            SqlDbType.Int => RelationalProbeValue.ToInt32(value, columnIndex),
            SqlDbType.BigInt => RelationalProbeValue.ToInt64(value, columnIndex),
            SqlDbType.Decimal => RelationalProbeValue.ToDecimal(value, columnIndex),
            SqlDbType.Bit => RelationalProbeValue.ToBoolean(value, columnIndex),
            SqlDbType.Date => RelationalProbeValue.ToDate(value, columnIndex),
            SqlDbType.DateTime2 => RelationalProbeValue.ToDateTime(value, columnIndex),
            SqlDbType.Time => RelationalProbeValue.ToTime(value, columnIndex),
            _ => throw new NotSupportedException(
                $"SQL Server natural-key lookup does not support parameter type '{sqlDbType}'."
            ),
        };

    private static void ConfigureParameter(DbParameter parameter, MssqlParameterType parameterType)
    {
        if (parameter is not SqlParameter sqlParameter)
        {
            throw new InvalidOperationException(
                "SQL Server natural-key lookup parameter configuration requires a SqlParameter instance."
            );
        }

        sqlParameter.SqlDbType = parameterType.SqlDbType;

        if (parameterType.Size is { } size)
        {
            sqlParameter.Size = size;
        }

        if (parameterType.Precision is { } precision)
        {
            sqlParameter.Precision = precision;
        }

        if (parameterType.Scale is { } scale)
        {
            sqlParameter.Scale = scale;
        }
    }

    private static string ParameterName(int groupIndex, int entryIndex, int columnIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"@g{groupIndex}p{entryIndex}_{columnIndex}");

    private static string QuoteTableName(DbTableName tableName) =>
        $"{Quote(tableName.Schema.Value)}.{Quote(tableName.Name)}";

    private static string Quote(string identifier) =>
        $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string EscapeSqlLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private readonly record struct MssqlParameterType(
        SqlDbType SqlDbType,
        int? Size,
        byte? Precision,
        byte? Scale
    );
}
