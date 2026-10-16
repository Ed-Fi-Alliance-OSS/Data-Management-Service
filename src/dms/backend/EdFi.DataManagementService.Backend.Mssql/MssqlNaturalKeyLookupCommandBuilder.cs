// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql;

/// <summary>
/// Builds the SQL Server natural-key lookup commands. Each group binds one JSON parameter and reads it through
/// a leftmost <c>OPENJSON(...) WITH (...)</c>, so the command text depends only on the group shapes and never
/// on the number of lookups. Every statement is pinned with <c>OPTION (FORCE ORDER)</c> so that the keys drive
/// the descriptor and target seeks in the written order.
/// </summary>
internal sealed class MssqlNaturalKeyLookupCommandBuilder : INaturalKeyLookupCommandBuilder
{
    private const string KeysAlias = "keys";
    private const string TargetAlias = "target";
    private const string DescriptorTargetAlias = "d";
    private const string OrdinalKeyName = "o";
    private const string ForceOrder = "OPTION (FORCE ORDER)";

    /// <summary>
    /// The core DDL's computed, lowered descriptor URI column that <c>UX_Descriptor_UriLowered_ResourceKeyId</c>
    /// indexes.
    /// </summary>
    private const string UriLoweredColumn = "UriLowered";

    private static readonly string DescriptorUriType =
        $"nvarchar({NaturalKeyLookupLimits.MaxMssqlDescriptorUriProbeLength.ToString(CultureInfo.InvariantCulture)})";

    private static readonly string IdentityCollation =
        IdentityEqualityContract.SqlServer.IdentityTextCollation
        ?? throw new InvalidOperationException("The SQL Server identity text collation is not defined.");

    public RelationalCommand BuildCommand(NaturalKeyLookupRequest request) =>
        Build(
            request,
            statements =>
                string.Join(
                    Environment.NewLine,
                    statements.Select(s => $"{s}{Environment.NewLine}{ForceOrder};")
                )
        );

    public RelationalCommand BuildEmbeddableCommand(NaturalKeyLookupRequest request) =>
        Build(
            request,
            statements =>
                string.Join(Environment.NewLine + "UNION ALL" + Environment.NewLine, statements)
                + Environment.NewLine
                + ForceOrder
        );

    private static RelationalCommand Build(
        NaturalKeyLookupRequest request,
        Func<IReadOnlyList<string>, string> combineStatements
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Groups.Count == 0)
        {
            throw new ArgumentException(
                "A natural-key lookup request must contain at least one group.",
                nameof(request)
            );
        }

        // Each group binds exactly one JSON parameter.
        if (request.Groups.Count > MssqlCommandLimits.MaxUserParametersPerCommand)
        {
            throw new InvalidOperationException(
                $"SQL Server natural-key lookup requires {request.Groups.Count} parameters, which exceeds the "
                    + $"{MssqlCommandLimits.MaxUserParametersPerCommand}-parameter command limit."
            );
        }

        List<string> statements = [];
        List<RelationalParameter> parameters = [];

        foreach (var group in request.Groups)
        {
            statements.Add(BuildGroup(request.MappingSet, group, parameters));
        }

        return new RelationalCommand(combineStatements(statements), parameters);
    }

    private static string BuildGroup(
        MappingSet mappingSet,
        NaturalKeyLookupGroup group,
        List<RelationalParameter> parameters
    )
    {
        if (group.Entries.Count == 0)
        {
            throw new ArgumentException(
                $"Natural-key lookup group {group.GroupOrdinal} for '{RelationalWriteSupport.FormatResource(group.Target.Resource)}' has no entries."
            );
        }

        var keyTypes = ResolveKeyTypes(group.Target);

        if (group.Entries.FirstOrDefault(entry => entry.KeyValues.Count != keyTypes.Count) is { } mismatch)
        {
            throw new ArgumentException(
                $"Natural-key lookup group {group.GroupOrdinal} for '{RelationalWriteSupport.FormatResource(group.Target.Resource)}' "
                    + $"expects {keyTypes.Count} key values per entry but an entry carries {mismatch.KeyValues.Count}."
            );
        }

        var parameterName = $"@g{group.GroupOrdinal.ToString(CultureInfo.InvariantCulture)}_keys";
        parameters.Add(CreateJsonParameter(parameterName, SerializeKeys(group.Entries)));

        List<string> withColumns = [$"{OrdinalKeyName} int '$.{OrdinalKeyName}'"];

        for (var keyIndex = 0; keyIndex < keyTypes.Count; keyIndex++)
        {
            withColumns.Add($"{KeyName(keyIndex)} {keyTypes[keyIndex].SqlType} '$.{KeyName(keyIndex)}'");
        }

        StringBuilder sql = new();
        var descriptorProbe = mappingSet.DescriptorProbeTarget;

        if (group.Target.Probe is not { } probe)
        {
            sql.Append(
                BuildProjection(
                    group.GroupOrdinal,
                    documentId: Column(DescriptorTargetAlias, descriptorProbe.DocumentIdColumn),
                    descriptorId: Column(DescriptorTargetAlias, descriptorProbe.DescriptorIdColumn),
                    resourceKeyId: Column(DescriptorTargetAlias, descriptorProbe.ResourceKeyIdColumn)
                )
            );
            AppendOpenJson(sql, parameterName, withColumns);
            AppendDescriptorJoin(
                sql,
                descriptorProbe,
                DescriptorTargetAlias,
                KeyColumn(0),
                group.Target.ResourceKeyId
            );

            return sql.ToString();
        }

        sql.Append(
            BuildProjection(
                group.GroupOrdinal,
                documentId: Column(TargetAlias, probe.DocumentIdColumn),
                descriptorId: "CAST(NULL AS int)",
                resourceKeyId: probe.Kind switch
                {
                    NaturalKeyProbeTargetKind.Abstract => Column(
                        TargetAlias,
                        probe.ResourceKeyIdColumn
                            ?? throw new InvalidOperationException(
                                $"Abstract natural-key probe for '{RelationalWriteSupport.FormatResource(probe.Resource)}' has no ResourceKeyId column."
                            )
                    ),
                    _ => SmallintLiteral(group.Target.ResourceKeyId),
                }
            )
        );
        AppendOpenJson(sql, parameterName, withColumns);

        // Descriptor parts are joined before the target so that every target key predicate is known when the
        // target is read. Each part compares the compact DescriptorId the target stores, never DocumentId.
        List<string> targetPredicates = [];
        var descriptorPartCount = 0;

        for (var keyIndex = 0; keyIndex < probe.KeyColumns.Count; keyIndex++)
        {
            var keyColumn = probe.KeyColumns[keyIndex];
            var targetColumn = Column(TargetAlias, keyColumn.Column);

            // ResolveKeyTypes has already rejected any binding other than Scalar and Descriptor.
            if (keyColumn.Binding is not NaturalKeyProbeKeyBinding.Descriptor descriptorBinding)
            {
                targetPredicates.Add($"{targetColumn} = {KeyOperand(keyIndex, keyTypes[keyIndex])}");
                continue;
            }

            descriptorPartCount++;
            var descriptorAlias =
                DescriptorTargetAlias + descriptorPartCount.ToString(CultureInfo.InvariantCulture);
            AppendDescriptorJoin(
                sql,
                descriptorProbe,
                descriptorAlias,
                KeyColumn(keyIndex),
                RelationalWriteSupport.GetResourceKeyIdOrThrow(
                    mappingSet,
                    descriptorBinding.DescriptorResource
                )
            );
            targetPredicates.Add(
                $"{targetColumn} = {Column(descriptorAlias, descriptorProbe.DescriptorIdColumn)}"
            );
        }

        sql.AppendLine();
        sql.Append($"INNER JOIN {QuoteTable(probe.Table)} AS {TargetAlias}");
        sql.AppendLine();
        sql.Append("    ON ");
        sql.Append(string.Join(Environment.NewLine + "   AND ", targetPredicates));

        return sql.ToString();
    }

    private static string BuildProjection(
        int groupOrdinal,
        string documentId,
        string descriptorId,
        string resourceKeyId
    ) =>
        $"SELECT CAST({groupOrdinal.ToString(CultureInfo.InvariantCulture)} AS int) AS {QuoteIdentifier(NaturalKeyLookupColumns.GroupOrdinal)}, "
        + $"{KeysAlias}.{OrdinalKeyName} AS {QuoteIdentifier(NaturalKeyLookupColumns.Ordinal)}, "
        + $"{documentId} AS {QuoteIdentifier(NaturalKeyLookupColumns.DocumentId)}, "
        + $"{descriptorId} AS {QuoteIdentifier(NaturalKeyLookupColumns.DescriptorId)}, "
        + $"{resourceKeyId} AS {QuoteIdentifier(NaturalKeyLookupColumns.ResourceKeyId)}";

    private static void AppendOpenJson(
        StringBuilder sql,
        string parameterName,
        IReadOnlyList<string> withColumns
    )
    {
        sql.AppendLine();
        sql.Append($"FROM OPENJSON({parameterName}) WITH ({string.Join(", ", withColumns)}) AS {KeysAlias}");
    }

    private static void AppendDescriptorJoin(
        StringBuilder sql,
        DescriptorProbeTarget descriptorProbe,
        string alias,
        string keyColumn,
        short resourceKeyId
    )
    {
        sql.AppendLine();
        sql.Append($"INNER JOIN {QuoteTable(descriptorProbe.DescriptorTable)} AS {alias}");
        sql.AppendLine();
        sql.Append(
            $"    ON {alias}.{QuoteIdentifier(UriLoweredColumn)} = LOWER({keyColumn} COLLATE {IdentityCollation})"
        );
        sql.AppendLine();
        sql.Append(
            $"   AND {Column(alias, descriptorProbe.ResourceKeyIdColumn)} = {SmallintLiteral(resourceKeyId)}"
        );
    }

    private static string KeyOperand(int keyIndex, KeyType keyType) =>
        keyType.IsText ? $"{KeyColumn(keyIndex)} COLLATE {IdentityCollation}" : KeyColumn(keyIndex);

    private static IReadOnlyList<KeyType> ResolveKeyTypes(NaturalKeyLookupTarget target)
    {
        if (target.Probe is not { } probe)
        {
            return [KeyType.DescriptorUri];
        }

        return
        [
            .. probe.KeyColumns.Select(keyColumn =>
                keyColumn.Binding switch
                {
                    NaturalKeyProbeKeyBinding.Scalar scalar => KeyType.ForScalar(scalar.ScalarType),
                    NaturalKeyProbeKeyBinding.Descriptor => KeyType.DescriptorUri,
                    _ => throw new InvalidOperationException(
                        $"Natural-key probe for '{RelationalWriteSupport.FormatResource(probe.Resource)}' has an unsupported binding "
                            + $"'{keyColumn.Binding.GetType().Name}' on '{keyColumn.IdentityJsonPath.Canonical}'."
                    ),
                }
            ),
        ];
    }

    /// <summary>
    /// Writes the group's entries as <c>[{"o":1,"k1":...},...]</c>. Dates and times use the invariant ISO 8601
    /// forms OPENJSON converts without a style, decimals are JSON numbers and booleans JSON booleans.
    /// </summary>
    private static string SerializeKeys(IReadOnlyList<NaturalKeyLookupEntry> entries)
    {
        using MemoryStream stream = new();

        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();

            for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
            {
                writer.WriteStartObject();
                writer.WriteNumber(OrdinalKeyName, entryIndex + 1);

                var keyValues = entries[entryIndex].KeyValues;

                for (var keyIndex = 0; keyIndex < keyValues.Count; keyIndex++)
                {
                    writer.WritePropertyName(KeyName(keyIndex));
                    WriteKeyValue(writer, keyValues[keyIndex]);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteKeyValue(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case string text:
                writer.WriteStringValue(text);
                break;
            case short int16:
                writer.WriteNumberValue(int16);
                break;
            case int int32:
                writer.WriteNumberValue(int32);
                break;
            case long int64:
                writer.WriteNumberValue(int64);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case DateOnly date:
                writer.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                writer.WriteStringValue(time.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                break;
            case DateTime dateTime:
                writer.WriteStringValue(
                    dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture)
                );
                break;
            default:
                throw new InvalidOperationException(
                    $"Natural-key lookup value type '{value.GetType().Name}' is not supported by SQL Server."
                );
        }
    }

    private static RelationalParameter CreateJsonParameter(string name, string json) =>
        new(
            name,
            json,
            static parameter =>
            {
                if (parameter is not SqlParameter sqlParameter)
                {
                    throw new InvalidOperationException(
                        "SQL Server natural-key lookup parameter configuration requires a SqlParameter instance."
                    );
                }

                sqlParameter.SqlDbType = SqlDbType.NVarChar;
                sqlParameter.Size = -1;
            }
        );

    private static string KeyName(int keyIndex) =>
        "k" + (keyIndex + 1).ToString(CultureInfo.InvariantCulture);

    private static string KeyColumn(int keyIndex) => $"{KeysAlias}.{KeyName(keyIndex)}";

    private static string Column(string alias, DbColumnName column) =>
        $"{alias}.{QuoteIdentifier(column.Value)}";

    private static string SmallintLiteral(short value) =>
        $"CAST({value.ToString(CultureInfo.InvariantCulture)} AS smallint)";

    private static string QuoteTable(DbTableName table) =>
        $"{QuoteIdentifier(table.Schema.Value)}.{QuoteIdentifier(table.Name)}";

    private static string QuoteIdentifier(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>
    /// The OPENJSON column type for one key, and whether it is compared under the identity text collation.
    /// </summary>
    private sealed record KeyType(string SqlType, bool IsText)
    {
        public static KeyType DescriptorUri { get; } = new(DescriptorUriType, IsText: true);

        public static KeyType ForScalar(RelationalScalarType scalarType) =>
            scalarType.Kind switch
            {
                ScalarKind.String => new(
                    scalarType.MaxLength is { } maxLength
                        ? $"nvarchar({maxLength.ToString(CultureInfo.InvariantCulture)})"
                        : "nvarchar(max)",
                    IsText: true
                ),
                ScalarKind.Int16 => new("smallint", IsText: false),
                ScalarKind.Int32 => new("int", IsText: false),
                ScalarKind.Int64 => new("bigint", IsText: false),
                ScalarKind.Decimal when scalarType.Decimal is { } decimalType => new(
                    $"decimal({decimalType.Precision.ToString(CultureInfo.InvariantCulture)}, "
                        + $"{decimalType.Scale.ToString(CultureInfo.InvariantCulture)})",
                    IsText: false
                ),
                ScalarKind.Boolean => new("bit", IsText: false),
                ScalarKind.Date => new("date", IsText: false),
                ScalarKind.DateTime => new("datetime2(7)", IsText: false),
                ScalarKind.Time => new("time(7)", IsText: false),
                _ => throw new InvalidOperationException(
                    $"Scalar kind '{scalarType.Kind}' is not supported by the SQL Server natural-key lookup."
                ),
            };
    }
}
