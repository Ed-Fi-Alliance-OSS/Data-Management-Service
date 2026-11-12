// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using EdFi.DataManagementService.Backend.External;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Builds the PostgreSQL natural-key lookup commands. Each group binds one typed array per key plus an
/// explicit ordinal array, and reads them through a leftmost <c>unnest(...)</c>, so the command text depends
/// only on the group shapes and never on the number of lookups.
/// </summary>
internal sealed class PostgresqlNaturalKeyLookupCommandBuilder : INaturalKeyLookupCommandBuilder
{
    private const string KeysAlias = "keys";
    private const string TargetAlias = "target";
    private const string DescriptorTargetAlias = "d";
    private const string OrdinalKeyName = "o";

    private static readonly string FoldCollation = QuoteIdentifier(
        DescriptorProbeTarget.PostgresqlUriFoldCollation
    );

    public RelationalCommand BuildCommand(NaturalKeyLookupRequest request) =>
        Build(request, statements => string.Join(Environment.NewLine, statements.Select(s => s + ";")));

    public RelationalCommand BuildEmbeddableCommand(NaturalKeyLookupRequest request) =>
        Build(
            request,
            statements => string.Join(Environment.NewLine + "UNION ALL" + Environment.NewLine, statements)
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

        var parameterPrefix = $"@g{group.GroupOrdinal.ToString(CultureInfo.InvariantCulture)}_";
        List<string> unnestArguments = [];
        List<string> keyNames = [QuoteIdentifier(OrdinalKeyName)];

        var ordinalParameterName = parameterPrefix + OrdinalKeyName;
        parameters.Add(
            CreateArrayParameter(
                ordinalParameterName,
                Enumerable.Range(1, group.Entries.Count).ToArray(),
                NpgsqlDbType.Integer
            )
        );
        unnestArguments.Add($"{ordinalParameterName}::integer[]");

        for (var keyIndex = 0; keyIndex < keyTypes.Count; keyIndex++)
        {
            var keyType = keyTypes[keyIndex];
            var parameterName = parameterPrefix + KeyName(keyIndex);

            parameters.Add(
                CreateArrayParameter(
                    parameterName,
                    keyType.CreateValues(group.Entries, keyIndex),
                    keyType.DbType
                )
            );
            unnestArguments.Add($"{parameterName}::{keyType.SqlType}[]");
            keyNames.Add(QuoteIdentifier(KeyName(keyIndex)));
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
            AppendUnnest(sql, unnestArguments, keyNames);
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
                descriptorId: "CAST(NULL AS integer)",
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
        AppendUnnest(sql, unnestArguments, keyNames);

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
                targetPredicates.Add($"{targetColumn} = {KeyColumn(keyIndex)}");
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
        sql.Append($"INNER JOIN {QuoteTable(probe.Table)} {TargetAlias}");
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
        $"SELECT CAST({groupOrdinal.ToString(CultureInfo.InvariantCulture)} AS integer) AS {QuoteIdentifier(NaturalKeyLookupColumns.GroupOrdinal)}, "
        + $"{KeysAlias}.{QuoteIdentifier(OrdinalKeyName)} AS {QuoteIdentifier(NaturalKeyLookupColumns.Ordinal)}, "
        + $"{documentId} AS {QuoteIdentifier(NaturalKeyLookupColumns.DocumentId)}, "
        + $"{descriptorId} AS {QuoteIdentifier(NaturalKeyLookupColumns.DescriptorId)}, "
        + $"{resourceKeyId} AS {QuoteIdentifier(NaturalKeyLookupColumns.ResourceKeyId)}";

    private static void AppendUnnest(
        StringBuilder sql,
        IReadOnlyList<string> unnestArguments,
        IReadOnlyList<string> keyNames
    )
    {
        sql.AppendLine();
        sql.Append(
            $"FROM unnest({string.Join(", ", unnestArguments)}) AS {KeysAlias}({string.Join(", ", keyNames)})"
        );
    }

    private static void AppendDescriptorJoin(
        StringBuilder sql,
        DescriptorProbeTarget descriptorProbe,
        string alias,
        string keyOperand,
        short resourceKeyId
    )
    {
        sql.AppendLine();
        sql.Append($"INNER JOIN {QuoteTable(descriptorProbe.DescriptorTable)} {alias}");
        sql.AppendLine();
        sql.Append(
            $"    ON lower(({Column(alias, descriptorProbe.NamespaceColumn)} || '#' || "
                + $"{Column(alias, descriptorProbe.CodeValueColumn)}) COLLATE {FoldCollation}) "
                + $"= lower({keyOperand} COLLATE {FoldCollation})"
        );
        sql.AppendLine();
        sql.Append(
            $"   AND {Column(alias, descriptorProbe.ResourceKeyIdColumn)} = {SmallintLiteral(resourceKeyId)}"
        );
    }

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
                    NaturalKeyProbeKeyBinding.Scalar scalar => KeyType.ForScalar(scalar.ScalarType.Kind),
                    NaturalKeyProbeKeyBinding.Descriptor => KeyType.DescriptorUri,
                    _ => throw new InvalidOperationException(
                        $"Natural-key probe for '{RelationalWriteSupport.FormatResource(probe.Resource)}' has an unsupported binding "
                            + $"'{keyColumn.Binding.GetType().Name}' on '{keyColumn.IdentityJsonPath.Canonical}'."
                    ),
                }
            ),
        ];
    }

    private static RelationalParameter CreateArrayParameter(
        string name,
        Array values,
        NpgsqlDbType elementType
    )
    {
        var arrayType = (NpgsqlDbType)((int)NpgsqlDbType.Array | (int)elementType);

        return new RelationalParameter(
            name,
            values,
            parameter =>
            {
                if (parameter is not NpgsqlParameter npgsqlParameter)
                {
                    throw new InvalidOperationException(
                        "PostgreSQL natural-key lookup parameter configuration requires an NpgsqlParameter instance."
                    );
                }

                npgsqlParameter.NpgsqlDbType = arrayType;
            }
        );
    }

    private static string KeyName(int keyIndex) =>
        "k" + (keyIndex + 1).ToString(CultureInfo.InvariantCulture);

    private static string KeyColumn(int keyIndex) => $"{KeysAlias}.{QuoteIdentifier(KeyName(keyIndex))}";

    private static string Column(string alias, DbColumnName column) =>
        $"{alias}.{QuoteIdentifier(column.Value)}";

    private static string SmallintLiteral(short value) =>
        $"CAST({value.ToString(CultureInfo.InvariantCulture)} AS smallint)";

    private static string QuoteTable(DbTableName table) =>
        $"{QuoteIdentifier(table.Schema.Value)}.{QuoteIdentifier(table.Name)}";

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// The PostgreSQL array element type bound for one key, and how entry values become a typed CLR array.
    /// </summary>
    private sealed record KeyType(
        string SqlType,
        NpgsqlDbType DbType,
        Func<IReadOnlyList<NaturalKeyLookupEntry>, int, Array> CreateValues
    )
    {
        public static KeyType DescriptorUri { get; } = Of<string>("text", NpgsqlDbType.Text);

        public static KeyType ForScalar(ScalarKind kind) =>
            kind switch
            {
                ScalarKind.String => Of<string>("text", NpgsqlDbType.Text),
                ScalarKind.Int16 => Of<short>("smallint", NpgsqlDbType.Smallint),
                ScalarKind.Int32 => Of<int>("integer", NpgsqlDbType.Integer),
                ScalarKind.Int64 => Of<long>("bigint", NpgsqlDbType.Bigint),
                ScalarKind.Decimal => Of<decimal>("numeric", NpgsqlDbType.Numeric),
                ScalarKind.Boolean => Of<bool>("boolean", NpgsqlDbType.Boolean),
                ScalarKind.Date => Of<DateOnly>("date", NpgsqlDbType.Date),
                ScalarKind.DateTime => Of<DateTime>("timestamp with time zone", NpgsqlDbType.TimestampTz),
                ScalarKind.Time => Of<TimeOnly>("time", NpgsqlDbType.Time),
                _ => throw new InvalidOperationException(
                    $"Scalar kind '{kind}' is not supported by the PostgreSQL natural-key lookup."
                ),
            };

        private static KeyType Of<T>(string sqlType, NpgsqlDbType dbType) =>
            new(
                sqlType,
                dbType,
                static (entries, keyIndex) => entries.Select(entry => (T)entry.KeyValues[keyIndex]).ToArray()
            );
    }
}
