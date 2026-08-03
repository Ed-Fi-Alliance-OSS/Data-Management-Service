// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// Builds the SQL Server natural-key reference-lookup command: one command per batch, one statement (and
/// therefore one result set) per target group, in group order.
/// </summary>
/// <remarks>
/// Each group's input is a single <c>nvarchar(max)</c> JSON document shredded by <c>OPENJSON … WITH</c>
/// into a typed relation — one row per entry, the request ordinal carried inside the JSON as <c>$.o</c>
/// rather than fabricated by <c>ROW_NUMBER()</c>. One bound parameter per group, whatever the batch size,
/// so the emitted text is identical for one entry and for five thousand.
///
/// The first implementation bound one parameter per probe <em>value</em> in a typed <c>VALUES</c> derived
/// table. Task 7's benchmark measured that at 2.65×–3.76× the hash resolver it replaces on SQL Server:
/// SqlClient costs roughly 17 µs per bound parameter and the cost grows faster than linearly, so
/// <c>references × probe width</c> parameters dominated everything else. The hash resolver escaped to a
/// table-valued parameter above 2000 ids for exactly that reason. <c>OPENJSON</c> buys the same
/// set-valued input with no server-side type dependency; it needs database compatibility level 130 or
/// higher, which is SQL Server 2016 and below the floor DMS already targets.
///
/// The JSON is written with <see cref="Utf8JsonWriter" />, never by string concatenation: identity values
/// are data, so a value carrying a quote or a bracket is escaped by construction and can never alter the
/// SQL text (which is cached per shape and holds no value at all).
///
/// Every statement closes with <c>OPTION (FORCE ORDER)</c> — see
/// <see cref="AppendJoinOrderHint(StringBuilder)" /> for the measurement that makes it load bearing.
/// </remarks>
internal static class MssqlNaturalKeyLookupCommandBuilder
{
    /// <summary>
    /// The largest number of parameters one command may bind: SQL Server's 2100-parameter ceiling less the
    /// two that <c>sp_executesql</c> consumes for the statement text and the parameter declaration.
    /// </summary>
    /// <remarks>
    /// A batch binds exactly one parameter per group, and a group is one target resource, so reaching this
    /// ceiling would take a single request naming 2099 distinct targets. The guard is kept as a cheap
    /// invariant that turns that impossibility into a diagnosable build-time failure rather than a driver
    /// error mid-request; it costs one comparison per command.
    /// </remarks>
    internal const int MssqlMaxCommandParameters = 2098;

    /// <summary>
    /// The JSON property carrying an entry's one-based ordinal within its group.
    /// </summary>
    private const string OrdinalJsonProperty = "o";

    private static readonly JsonEncodedText OrdinalJsonPropertyName = JsonEncodedText.Encode(
        OrdinalJsonProperty
    );

    /// <summary>
    /// Pre-encoded <c>v{k}</c> property names. Sized well past the widest DS 5.2 RefKey (seven columns);
    /// a wider probe falls back to encoding on the fly.
    /// </summary>
    private static readonly JsonEncodedText[] ValueJsonPropertyNames =
    [
        .. Enumerable
            .Range(0, 32)
            .Select(columnIndex => JsonEncodedText.Encode(ValueJsonProperty(columnIndex))),
    ];

    private static readonly SqlScalarTypeDefaults ScalarTypeDefaults =
        new MssqlDialectRules().ScalarTypeDefaults;

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

    private static void EnsureSupportedParameterCount(NaturalKeyLookupBatch batch)
    {
        if (batch.Groups.Count <= MssqlMaxCommandParameters)
        {
            return;
        }

        throw new ArgumentOutOfRangeException(
            nameof(batch),
            batch.Groups.Count,
            $"SQL Server natural-key lookup supports at most {MssqlMaxCommandParameters} bound parameters per command, one per target group. Split the batch into smaller batches."
        );
    }

    // ── Command text ────────────────────────────────────────────────────

    private static string BuildCommandText(NaturalKeyLookupBatch batch)
    {
        // Entry counts are deliberately excluded: the entries live in the JSON payload, not in the SQL,
        // so one cached statement serves every batch size of a given shape.
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
            statements.Add(BuildGroupSql(batch.MappingSet, batch.Groups[groupIndex], groupIndex));
        }

        return string.Join(";" + Environment.NewLine + Environment.NewLine, statements);
    }

    private static string BuildGroupSql(MappingSet mappingSet, NaturalKeyLookupGroup group, int groupIndex)
    {
        StringBuilder builder = new();

        AppendProjection(builder, mappingSet, group);
        AppendJsonInput(builder, group, groupIndex);

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

        AppendJoinOrderHint(builder);

        return builder.ToString();
    }

    /// <summary>
    /// Pins the shredded JSON input as the driving side of the join.
    /// </summary>
    /// <remarks>
    /// Load bearing, and measured. <c>OPENJSON</c> is a table-valued function: it carries no statistics and
    /// its cardinality is always guessed at 50 rows, so the optimizer is free to place it on the INNER side
    /// of a nested loop — and against <c>dms.Descriptor</c> it does, scanning the table and re-parsing the
    /// whole JSON document once per descriptor row. Measured on SQL Server 2022 against a 257-row
    /// descriptor table: 32 entries cost 5.6 ms per execution unhinted and 0.25 ms with this hint; 256
    /// entries cost 44 ms unhinted and 0.5 ms with it. The unhinted plan shredded 8224 rows for a 32-entry
    /// payload.
    ///
    /// The hint pins only the join ORDER, which the statement already writes in the only sensible
    /// sequence — shred the small key set, resolve any descriptor-valued parts, then seek the target's
    /// RefKey index. It does not pin the join ALGORITHM, so the optimizer may still hash-join a large
    /// input against a small target. Verified to leave the already-good plans untouched: a 2500-entry
    /// single-column probe and a 256-entry six-column probe measured identically with and without it.
    /// </remarks>
    private static void AppendJoinOrderHint(StringBuilder builder)
    {
        builder.AppendLine();
        builder.Append("OPTION (FORCE ORDER)");
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

    /// <summary>
    /// Emits the <c>OPENJSON … WITH</c> input relation: the ordinal as <c>int</c> from <c>$.o</c>, then one
    /// typed column per probe value from <c>$.v{k}</c>.
    /// </summary>
    /// <remarks>
    /// An empty group needs no special case — an empty JSON array shreds to zero rows, so the group still
    /// owes the reader exactly one (empty) result set and the statement text is unchanged.
    /// </remarks>
    private static void AppendJsonInput(StringBuilder builder, NaturalKeyLookupGroup group, int groupIndex)
    {
        var bindings = ResolveColumnBindings(group);

        builder.Append("FROM OPENJSON(").Append(GroupParameterName(groupIndex)).Append(") WITH (");
        builder
            .Append(Quote(NaturalKeyLookupColumns.Ordinal))
            .Append(" int '$.")
            .Append(OrdinalJsonProperty)
            .Append('\'');

        for (var columnIndex = 0; columnIndex < bindings.Count; columnIndex++)
        {
            builder
                .Append(", ")
                .Append(Quote(NaturalKeyLookupCommandSupport.InputColumnAlias(columnIndex)))
                .Append(' ')
                .Append(bindings[columnIndex].SqlType)
                .Append(" '$.")
                .Append(ValueJsonProperty(columnIndex))
                .Append('\'');
        }

        builder.Append(") AS ").AppendLine(NaturalKeyLookupCommandSupport.InputAlias);
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

    // ── Parameters ──────────────────────────────────────────────────────

    private static IReadOnlyList<RelationalParameter> BuildParameters(NaturalKeyLookupBatch batch)
    {
        List<RelationalParameter> parameters = new(batch.Groups.Count);

        for (var groupIndex = 0; groupIndex < batch.Groups.Count; groupIndex++)
        {
            var group = batch.Groups[groupIndex];

            parameters.Add(
                new RelationalParameter(
                    GroupParameterName(groupIndex),
                    BuildGroupJson(group, ResolveColumnBindings(group)),
                    ConfigureJsonParameter
                )
            );
        }

        return parameters;
    }

    private static void ConfigureJsonParameter(DbParameter parameter)
    {
        if (parameter is not SqlParameter sqlParameter)
        {
            throw new InvalidOperationException(
                "SQL Server natural-key lookup parameter configuration requires a SqlParameter instance."
            );
        }

        sqlParameter.SqlDbType = SqlDbType.NVarChar;

        // nvarchar(max): the payload grows with the batch, and a fixed size would silently truncate it.
        sqlParameter.Size = -1;
    }

    /// <summary>
    /// Serializes a group's entries as a JSON array of <c>{"o":ordinal,"v0":…,"v1":…}</c> objects, each
    /// value in the canonical text form <c>OPENJSON</c>'s typed <c>WITH</c> conversion parses back to the
    /// probe column's type.
    /// </summary>
    private static string BuildGroupJson(
        NaturalKeyLookupGroup group,
        IReadOnlyList<MssqlProbeColumnBinding> bindings
    )
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { SkipValidation = true }))
        {
            writer.WriteStartArray();

            foreach (var entry in group.Entries)
            {
                writer.WriteStartObject();
                writer.WriteNumber(OrdinalJsonPropertyName, entry.Ordinal);

                for (var columnIndex = 0; columnIndex < bindings.Count; columnIndex++)
                {
                    WriteProbeValue(
                        writer,
                        ValueJsonPropertyName(columnIndex),
                        entry.Values[columnIndex],
                        columnIndex,
                        bindings[columnIndex].ValueKind
                    );
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Writes one probe value in the form its <c>WITH</c>-clause type converts from.
    /// </summary>
    /// <remarks>
    /// Verified against SQL Server 2022: JSON numbers convert exactly to <c>int</c>, <c>bigint</c> (past
    /// 2^53) and <c>decimal(p,s)</c>; a JSON boolean converts to <c>bit</c>; ISO-8601 strings convert to
    /// <c>date</c>, <c>datetime2(7)</c> and <c>time(7)</c>. A value that fails to convert raises an error
    /// rather than becoming NULL, so a serialization mistake is loud rather than a silent miss.
    ///
    /// The <see cref="DateTime" /> form deliberately carries no <c>Z</c> and no offset: the value is
    /// already normalized to UTC, the column is <c>datetime2(7)</c> which stores no offset, and this is
    /// exactly the wall-clock component list a <see cref="SqlDbType.DateTime2" /> parameter would have
    /// sent.
    /// </remarks>
    private static void WriteProbeValue(
        Utf8JsonWriter writer,
        JsonEncodedText propertyName,
        object value,
        int columnIndex,
        MssqlProbeValueKind valueKind
    )
    {
        switch (valueKind)
        {
            case MssqlProbeValueKind.String:
                writer.WriteString(propertyName, RelationalProbeValue.ToStringValue(value, columnIndex));
                break;
            case MssqlProbeValueKind.Int32:
                writer.WriteNumber(propertyName, RelationalProbeValue.ToInt32(value, columnIndex));
                break;
            case MssqlProbeValueKind.Int64:
                writer.WriteNumber(propertyName, RelationalProbeValue.ToInt64(value, columnIndex));
                break;
            case MssqlProbeValueKind.Decimal:
                writer.WriteNumber(propertyName, RelationalProbeValue.ToDecimal(value, columnIndex));
                break;
            case MssqlProbeValueKind.Boolean:
                writer.WriteBoolean(propertyName, RelationalProbeValue.ToBoolean(value, columnIndex));
                break;
            case MssqlProbeValueKind.Date:
                writer.WriteString(
                    propertyName,
                    RelationalProbeValue
                        .ToDate(value, columnIndex)
                        .ToString(DateJsonFormat, CultureInfo.InvariantCulture)
                );
                break;
            case MssqlProbeValueKind.DateTime:
                writer.WriteString(
                    propertyName,
                    RelationalProbeValue
                        .ToDateTime(value, columnIndex)
                        .ToString(DateTimeJsonFormat, CultureInfo.InvariantCulture)
                );
                break;
            case MssqlProbeValueKind.Time:
                writer.WriteString(
                    propertyName,
                    RelationalProbeValue
                        .ToTime(value, columnIndex)
                        .ToString(TimeJsonFormat, CultureInfo.InvariantCulture)
                );
                break;
            default:
                throw new NotSupportedException(
                    $"SQL Server natural-key lookup does not support probe value kind '{valueKind}'."
                );
        }
    }

    private const string DateJsonFormat = "yyyy-MM-dd";
    private const string DateTimeJsonFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
    private const string TimeJsonFormat = "HH:mm:ss.fffffff";

    // ── Column bindings ─────────────────────────────────────────────────

    private static IReadOnlyList<MssqlProbeColumnBinding> ResolveColumnBindings(
        NaturalKeyLookupGroup group
    ) =>
        group switch
        {
            NaturalKeyProbeLookupGroup probeGroup =>
            [
                .. probeGroup.Probe.Columns.Select(ResolveColumnBinding),
            ],
            DescriptorLookupGroup => [DescriptorUriBinding],
            _ => throw new NotSupportedException(
                $"Unsupported natural-key lookup group kind '{group.GetType().Name}'."
            ),
        };

    /// <summary>
    /// The descriptor URI probe column. The explicit 306 matches <c>dms.Descriptor.UriLowered</c>: an
    /// <c>nvarchar(max)</c> input column would implicitly widen and cost the
    /// <c>UX_Descriptor_UriLowered_Discriminator</c> seek.
    /// </summary>
    private static readonly MssqlProbeColumnBinding DescriptorUriBinding = new(
        string.Create(
            CultureInfo.InvariantCulture,
            $"{ScalarTypeDefaults.StringType}({NaturalKeyLookupCommandSupport.DescriptorUriMaxLength})"
        ),
        MssqlProbeValueKind.String
    );

    /// <summary>
    /// Renders a probe column's <c>WITH</c>-clause type from the same
    /// <see cref="MssqlDialectRules" /> scalar-type defaults the DDL emitter renders storage columns from,
    /// so the shredded input column and the column it is compared against always agree.
    /// </summary>
    private static MssqlProbeColumnBinding ResolveColumnBinding(NaturalKeyProbeColumn column) =>
        column.DescriptorResource is not null
            ? DescriptorUriBinding
            : column.ScalarType.Kind switch
            {
                ScalarKind.String => new MssqlProbeColumnBinding(
                    StringWithClauseType(column.ScalarType.MaxLength),
                    MssqlProbeValueKind.String
                ),
                ScalarKind.Int32 => new MssqlProbeColumnBinding(
                    ScalarTypeDefaults.Int32Type,
                    MssqlProbeValueKind.Int32
                ),
                ScalarKind.Int64 => new MssqlProbeColumnBinding(
                    ScalarTypeDefaults.Int64Type,
                    MssqlProbeValueKind.Int64
                ),
                ScalarKind.Decimal => new MssqlProbeColumnBinding(
                    DecimalWithClauseType(column.ScalarType.Decimal),
                    MssqlProbeValueKind.Decimal
                ),
                ScalarKind.Boolean => new MssqlProbeColumnBinding(
                    ScalarTypeDefaults.BooleanType,
                    MssqlProbeValueKind.Boolean
                ),
                ScalarKind.Date => new MssqlProbeColumnBinding(
                    ScalarTypeDefaults.DateType,
                    MssqlProbeValueKind.Date
                ),
                ScalarKind.DateTime => new MssqlProbeColumnBinding(
                    ScalarTypeDefaults.DateTimeType,
                    MssqlProbeValueKind.DateTime
                ),
                ScalarKind.Time => new MssqlProbeColumnBinding(
                    ScalarTypeDefaults.TimeType,
                    MssqlProbeValueKind.Time
                ),
                var kind => throw new NotSupportedException(
                    $"SQL Server natural-key lookup does not support scalar kind '{kind}'."
                ),
            };

    private static string StringWithClauseType(int? maxLength)
    {
        if (maxLength is not { } declaredLength)
        {
            // Unbounded only for MetaEd duration/enumeration properties; SQL Server needs an explicit
            // length or (max), exactly as MssqlDialect.RenderColumnType does for the storage column.
            return $"{ScalarTypeDefaults.StringType}(max)";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ScalarTypeDefaults.StringType}({declaredLength})"
        );
    }

    private static string DecimalWithClauseType((int Precision, int Scale)? decimalType)
    {
        if (decimalType is not { } declaredDecimal)
        {
            return ScalarTypeDefaults.DecimalType;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ScalarTypeDefaults.DecimalType}({declaredDecimal.Precision},{declaredDecimal.Scale})"
        );
    }

    // ── Naming ──────────────────────────────────────────────────────────

    private static string GroupParameterName(int groupIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"@g{groupIndex}");

    private static string ValueJsonProperty(int columnIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"v{columnIndex}");

    private static JsonEncodedText ValueJsonPropertyName(int columnIndex) =>
        columnIndex < ValueJsonPropertyNames.Length
            ? ValueJsonPropertyNames[columnIndex]
            : JsonEncodedText.Encode(ValueJsonProperty(columnIndex));

    private static string QuoteTableName(DbTableName tableName) =>
        $"{Quote(tableName.Schema.Value)}.{Quote(tableName.Name)}";

    private static string Quote(string identifier) =>
        $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string EscapeSqlLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// How one probe column is shredded: the <c>WITH</c>-clause SQL type and the JSON form its values take.
    /// </summary>
    private readonly record struct MssqlProbeColumnBinding(string SqlType, MssqlProbeValueKind ValueKind);

    private enum MssqlProbeValueKind
    {
        String,
        Int32,
        Int64,
        Decimal,
        Boolean,
        Date,
        DateTime,
        Time,
    }
}
