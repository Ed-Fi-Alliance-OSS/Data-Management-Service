// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public enum MaterializedDocumentFixtureSqlDialect
{
    Postgresql = 1,
    Mssql = 2,
}

public sealed record MaterializedDocumentFixtureSqlCommand(
    string CommandText,
    IReadOnlyList<MaterializedDocumentFixtureSqlParameter> Parameters
);

public sealed record MaterializedDocumentFixtureSqlParameter(string Name, object? Value);

public sealed class MaterializedDocumentFixtureSeeder(
    MaterializedDocumentFixtureSqlDialect dialect,
    MaterializedDocumentFixtureSeederOptions? options = null
)
{
    private readonly MaterializedDocumentFixtureSeederOptions _options =
        options ?? new MaterializedDocumentFixtureSeederOptions();
    private readonly FixtureSqlDialect _dialect = dialect switch
    {
        MaterializedDocumentFixtureSqlDialect.Postgresql => new PostgresqlFixtureSqlDialect(),
        MaterializedDocumentFixtureSqlDialect.Mssql => new MssqlFixtureSqlDialect(),
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
    };

    public IReadOnlyList<MaterializedDocumentFixtureSqlCommand> BuildSetupCommands(
        MaterializedDocumentFixture fixture
    )
    {
        ArgumentNullException.ThrowIfNull(fixture);

        List<MaterializedDocumentFixtureSqlCommand> commands = [];

        if (_options.CreateSchemasAndTables)
        {
            AddSchemaCommands(commands, fixture);
            AddCoreTableCommands(commands);
            AddFixtureTableCommands(commands, fixture);
        }

        if (_options.SeedResourceKeys)
        {
            AddResourceKeyCommands(commands, fixture);
        }

        AddDocumentCommands(commands, fixture);
        AddDescriptorCommands(commands, fixture);
        AddTableRowCommands(commands, fixture.SourceSetup.ConcreteRootRows);
        AddTableRowCommands(commands, fixture.SourceSetup.ChildRows);
        AddTableRowCommands(commands, fixture.SourceSetup.ExtensionRows);
        AddReferentialIdentityCommands(commands, fixture);
        AddPreserveDocumentStampCommands(commands, fixture);

        return commands;
    }

    public async Task SeedAsync(
        DbConnection connection,
        MaterializedDocumentFixture fixture,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(fixture);

        foreach (var commandSpec in BuildSetupCommands(fixture))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandSpec.CommandText;

            foreach (var parameterSpec in commandSpec.Parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = parameterSpec.Name;
                parameter.Value = parameterSpec.Value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void AddSchemaCommands(
        List<MaterializedDocumentFixtureSqlCommand> commands,
        MaterializedDocumentFixture fixture
    )
    {
        var schemas = new SortedSet<string>(StringComparer.Ordinal) { "dms" };
        foreach (var row in fixture.SourceSetup.ConcreteRootRows)
        {
            schemas.Add(row.Schema);
        }

        foreach (var row in fixture.SourceSetup.ChildRows)
        {
            schemas.Add(row.Schema);
        }

        foreach (var row in fixture.SourceSetup.ExtensionRows)
        {
            schemas.Add(row.Schema);
        }

        foreach (var schema in schemas)
        {
            commands.Add(new(_dialect.CreateSchemaIfMissing(schema), []));
        }
    }

    private void AddCoreTableCommands(List<MaterializedDocumentFixtureSqlCommand> commands)
    {
        commands.Add(
            new(
                _dialect.CreateTableIfMissing(
                    "dms",
                    "ResourceKey",
                    $"""
                    {_dialect.Quote("ResourceKeyId")} smallint NOT NULL PRIMARY KEY,
                    {_dialect.Quote("ProjectName")} {_dialect.Text(256)} NOT NULL,
                    {_dialect.Quote("ResourceName")} {_dialect.Text(256)} NOT NULL,
                    {_dialect.Quote("ResourceVersion")} {_dialect.Text(32)} NOT NULL
                    """
                ),
                []
            )
        );
        commands.Add(
            new(
                _dialect.CreateTableIfMissing(
                    "dms",
                    "Document",
                    $"""
                    {_dialect.Quote("DocumentId")} bigint NOT NULL PRIMARY KEY,
                    {_dialect.Quote("DocumentUuid")} {_dialect.UuidColumnType} NOT NULL,
                    {_dialect.Quote("ResourceKeyId")} smallint NOT NULL,
                    {_dialect.Quote("CreatedByOwnershipTokenId")} smallint NULL,
                    {_dialect.Quote("ContentVersion")} bigint NOT NULL,
                    {_dialect.Quote("IdentityVersion")} bigint NOT NULL,
                    {_dialect.Quote(
                        "ContentLastModifiedAt"
                    )} {_dialect.TimestampWithOffsetColumnType} NOT NULL,
                    {_dialect.Quote(
                        "IdentityLastModifiedAt"
                    )} {_dialect.TimestampWithOffsetColumnType} NOT NULL,
                    {_dialect.Quote("CreatedAt")} {_dialect.TimestampWithOffsetColumnType} NOT NULL
                    """
                ),
                []
            )
        );
        commands.Add(
            new(
                _dialect.CreateTableIfMissing(
                    "dms",
                    "Descriptor",
                    $"""
                    {_dialect.Quote("DocumentId")} bigint NOT NULL PRIMARY KEY,
                    {_dialect.Quote("ResourceKeyId")} smallint NOT NULL,
                    {_dialect.Quote("Namespace")} {_dialect.Text(255)} NOT NULL,
                    {_dialect.Quote("CodeValue")} {_dialect.Text(50)} NOT NULL,
                    {_dialect.Quote("ShortDescription")} {_dialect.Text(75)} NOT NULL,
                    {_dialect.Quote("Description")} {_dialect.Text(1024)} NULL,
                    {_dialect.Quote("EffectiveBeginDate")} date NULL,
                    {_dialect.Quote("EffectiveEndDate")} date NULL,
                    {_dialect.Quote("Discriminator")} {_dialect.Text(128)} NOT NULL,
                    {_dialect.Quote("Uri")} {_dialect.Text(512)} NULL
                    """
                ),
                []
            )
        );
        commands.Add(
            new(
                _dialect.CreateTableIfMissing(
                    "dms",
                    "ReferentialIdentity",
                    $"""
                    {_dialect.Quote("ReferentialId")} {_dialect.UuidColumnType} NOT NULL PRIMARY KEY,
                    {_dialect.Quote("DocumentId")} bigint NOT NULL,
                    {_dialect.Quote("ResourceKeyId")} smallint NOT NULL
                    """
                ),
                []
            )
        );
    }

    private void AddFixtureTableCommands(
        List<MaterializedDocumentFixtureSqlCommand> commands,
        MaterializedDocumentFixture fixture
    )
    {
        foreach (var group in AllTableRows(fixture).GroupBy(row => (row.Schema, row.Table)))
        {
            List<string> columns = [$"{_dialect.Quote("DocumentId")} bigint NOT NULL"];
            var columnNames = group
                .SelectMany(row => row.Values.Select(value => value.Key))
                .Where(columnName => columnName != "DocumentId")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            foreach (var columnName in columnNames)
            {
                var values = group
                    .Select(row => row.Values.TryGetPropertyValue(columnName, out var value) ? value : null)
                    .Where(value => value is not null)
                    .Cast<JsonNode>()
                    .ToArray();
                columns.Add($"{_dialect.Quote(columnName)} {InferColumnType(columnName, values)} NULL");
            }

            commands.Add(
                new(
                    _dialect.CreateTableIfMissing(
                        group.Key.Schema,
                        group.Key.Table,
                        $"""
                        {string.Join("," + Environment.NewLine + "    ", columns)}
                        """
                    ),
                    []
                )
            );
        }
    }

    private void AddResourceKeyCommands(
        List<MaterializedDocumentFixtureSqlCommand> commands,
        MaterializedDocumentFixture fixture
    )
    {
        foreach (var resourceKey in ResourceKeys(fixture).OrderBy(row => row.ResourceKeyId))
        {
            var parameters = new List<MaterializedDocumentFixtureSqlParameter>
            {
                new(_dialect.ParameterName(0), resourceKey.ResourceKeyId),
                new(_dialect.ParameterName(1), resourceKey.ProjectName),
                new(_dialect.ParameterName(2), resourceKey.ResourceName),
                new(_dialect.ParameterName(3), resourceKey.ResourceVersion),
            };

            commands.Add(
                new(
                    _dialect.InsertIfMissing(
                        "dms",
                        "ResourceKey",
                        ["ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion"],
                        parameters.Select(parameter => parameter.Name).ToArray(),
                        ["ResourceKeyId"]
                    ),
                    parameters
                )
            );
        }
    }

    private void AddDocumentCommands(
        List<MaterializedDocumentFixtureSqlCommand> commands,
        MaterializedDocumentFixture fixture
    )
    {
        foreach (var document in fixture.SourceSetup.Documents.OrderBy(document => document.DocumentId))
        {
            var parameters = new List<MaterializedDocumentFixtureSqlParameter>
            {
                new(_dialect.ParameterName(0), document.DocumentId),
                new(_dialect.ParameterName(1), Guid.Parse(document.DocumentUuid)),
                new(_dialect.ParameterName(2), document.ResourceKeyId),
                new(_dialect.ParameterName(3), document.ContentVersion),
                new(_dialect.ParameterName(4), DocumentTimestampValue(document.ContentLastModifiedAt)),
            };

            commands.Add(
                new(
                    _dialect.Insert(
                        "dms",
                        "Document",
                        [
                            "DocumentId",
                            "DocumentUuid",
                            "ResourceKeyId",
                            "CreatedByOwnershipTokenId",
                            "ContentVersion",
                            "IdentityVersion",
                            "ContentLastModifiedAt",
                            "IdentityLastModifiedAt",
                            "CreatedAt",
                        ],
                        [
                            parameters[0].Name,
                            parameters[1].Name,
                            parameters[2].Name,
                            "NULL",
                            parameters[3].Name,
                            parameters[3].Name,
                            parameters[4].Name,
                            parameters[4].Name,
                            parameters[4].Name,
                        ]
                    ),
                    parameters
                )
            );
        }
    }

    private void AddDescriptorCommands(
        List<MaterializedDocumentFixtureSqlCommand> commands,
        MaterializedDocumentFixture fixture
    )
    {
        foreach (var descriptor in fixture.SourceSetup.Descriptors.OrderBy(row => row.DocumentId))
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["DocumentId"] = descriptor.DocumentId,
                ["ResourceKeyId"] = descriptor.ResourceKeyId,
                ["Namespace"] = descriptor.Namespace,
                ["CodeValue"] = descriptor.CodeValue,
                ["ShortDescription"] = descriptor.ShortDescription,
                ["Description"] = JsonNodeValue(JsonObjectValueOrNull(descriptor.Values, "Description")),
                ["EffectiveBeginDate"] = JsonNodeDateValue(
                    JsonObjectValueOrNull(descriptor.Values, "EffectiveBeginDate")
                ),
                ["EffectiveEndDate"] = JsonNodeDateValue(
                    JsonObjectValueOrNull(descriptor.Values, "EffectiveEndDate")
                ),
                ["Discriminator"] =
                    JsonNodeValue(JsonObjectValueOrNull(descriptor.Values, "Discriminator"))
                    ?? descriptor.CodeValue,
                ["Uri"] = $"{descriptor.Namespace}#{descriptor.CodeValue}",
            };

            commands.Add(BuildInsertCommand("dms", "Descriptor", values));
        }
    }

    private void AddTableRowCommands(
        List<MaterializedDocumentFixtureSqlCommand> commands,
        IReadOnlyList<MaterializedDocumentSourceTableRow> rows
    )
    {
        foreach (
            var row in rows.OrderBy(row => row.DocumentId).ThenBy(row => row.Schema).ThenBy(row => row.Table)
        )
        {
            var values = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["DocumentId"] = row.DocumentId,
            };

            foreach (var value in row.Values.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                if (value.Key != "DocumentId")
                {
                    values[value.Key] = JsonNodeValue(value.Value, value.Key);
                }
            }

            commands.Add(BuildInsertCommand(row.Schema, row.Table, values));
        }
    }

    private void AddReferentialIdentityCommands(
        List<MaterializedDocumentFixtureSqlCommand> commands,
        MaterializedDocumentFixture fixture
    )
    {
        foreach (
            var row in fixture.SourceSetup.ReferentialIdentityRows.OrderBy(row =>
                Guid.Parse(row.ReferentialId)
            )
        )
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ReferentialId"] = Guid.Parse(row.ReferentialId),
                ["DocumentId"] = row.DocumentId,
                ["ResourceKeyId"] = row.ResourceKeyId,
            };

            commands.Add(BuildInsertCommand("dms", "ReferentialIdentity", values));
        }
    }

    private void AddPreserveDocumentStampCommands(
        List<MaterializedDocumentFixtureSqlCommand> commands,
        MaterializedDocumentFixture fixture
    )
    {
        foreach (var document in fixture.SourceSetup.Documents.OrderBy(document => document.DocumentId))
        {
            var parameters = new List<MaterializedDocumentFixtureSqlParameter>
            {
                new(_dialect.ParameterName(0), document.ContentVersion),
                new(_dialect.ParameterName(1), DocumentTimestampValue(document.ContentLastModifiedAt)),
                new(_dialect.ParameterName(2), document.DocumentId),
            };

            commands.Add(
                new(
                    $"""
                    UPDATE {_dialect.QualifiedTable("dms", "Document")}
                    SET {_dialect.Quote("ContentVersion")} = {parameters[0].Name},
                        {_dialect.Quote("IdentityVersion")} = {parameters[0].Name},
                        {_dialect.Quote("ContentLastModifiedAt")} = {parameters[1].Name},
                        {_dialect.Quote("IdentityLastModifiedAt")} = {parameters[1].Name}
                    WHERE {_dialect.Quote("DocumentId")} = {parameters[2].Name}
                    """,
                    parameters
                )
            );
        }
    }

    private MaterializedDocumentFixtureSqlCommand BuildInsertCommand(
        string schema,
        string table,
        IReadOnlyDictionary<string, object?> values
    )
    {
        var parameters = values
            .Select(
                (value, index) =>
                    new MaterializedDocumentFixtureSqlParameter(_dialect.ParameterName(index), value.Value)
            )
            .ToArray();

        return new(
            _dialect.Insert(schema, table, values.Keys.ToArray(), parameters.Select(p => p.Name).ToArray()),
            parameters
        );
    }

    private string InferColumnType(string columnName, IReadOnlyList<JsonNode> values)
    {
        if (values.Count == 0)
        {
            return _dialect.Text(1024);
        }

        if (IsBigIntColumn(columnName))
        {
            return "bigint";
        }

        if (columnName == "Ordinal")
        {
            return "integer";
        }

        if (columnName.EndsWith("Date", StringComparison.Ordinal))
        {
            return "date";
        }

        if (values.All(value => TryGetValue<bool>(value, out _)))
        {
            return _dialect.BooleanColumnType;
        }

        if (values.All(value => TryGetValue<int>(value, out _)))
        {
            return "integer";
        }

        if (values.All(value => TryGetValue<long>(value, out _)))
        {
            return "bigint";
        }

        return _dialect.Text(1024);
    }

    private static IEnumerable<MaterializedDocumentSourceTableRow> AllTableRows(
        MaterializedDocumentFixture fixture
    ) =>
        fixture
            .SourceSetup.ConcreteRootRows.Concat(fixture.SourceSetup.ChildRows)
            .Concat(fixture.SourceSetup.ExtensionRows);

    private static IEnumerable<FixtureResourceKeyRow> ResourceKeys(MaterializedDocumentFixture fixture)
    {
        Dictionary<short, FixtureResourceKeyRow> resourceKeys = [];

        if (fixture.ExpectedCacheRow is not null)
        {
            var sourceDocument = fixture.SourceSetup.Documents.Single(document =>
                document.DocumentId == fixture.ExpectedCacheRow.DocumentId
            );
            resourceKeys[sourceDocument.ResourceKeyId] = new FixtureResourceKeyRow(
                sourceDocument.ResourceKeyId,
                fixture.ExpectedCacheRow.ProjectName,
                fixture.ExpectedCacheRow.ResourceName,
                fixture.ExpectedCacheRow.ResourceVersion
            );
        }

        if (fixture.ExpectedProjectionFailure is not null)
        {
            resourceKeys[fixture.ExpectedProjectionFailure.ResourceKeyId] = new FixtureResourceKeyRow(
                fixture.ExpectedProjectionFailure.ResourceKeyId,
                fixture.ExpectedProjectionFailure.ProjectName,
                fixture.ExpectedProjectionFailure.ResourceName,
                fixture.ExpectedProjectionFailure.ResourceVersion
            );
        }

        foreach (var document in fixture.SourceSetup.Documents)
        {
            resourceKeys.TryAdd(
                document.ResourceKeyId,
                new FixtureResourceKeyRow(
                    document.ResourceKeyId,
                    "Fixture",
                    $"ResourceKey{document.ResourceKeyId.ToString(CultureInfo.InvariantCulture)}",
                    "fixture"
                )
            );
        }

        return resourceKeys.Values;
    }

    private static object? JsonNodeValue(JsonNode? node, string? columnName = null)
    {
        if (node is null)
        {
            return null;
        }

        if (columnName is not null && IsBigIntColumn(columnName) && TryGetValue<long>(node, out var idValue))
        {
            return idValue;
        }

        return node switch
        {
            JsonValue value when TryGetValue<bool>(value, out var boolValue) => boolValue,
            JsonValue value when TryGetValue<int>(value, out var intValue) => intValue,
            JsonValue value when TryGetValue<long>(value, out var longValue) => longValue,
            JsonValue value when TryGetValue<decimal>(value, out var decimalValue) => decimalValue,
            JsonValue value when TryGetValue<string>(value, out var stringValue) => ParseScalarString(
                stringValue
            ),
            _ => node.ToJsonString(),
        };
    }

    private static bool IsBigIntColumn(string columnName) =>
        columnName.EndsWith("DocumentId", StringComparison.Ordinal)
        || columnName.EndsWith("DescriptorId", StringComparison.Ordinal)
        || columnName == "CollectionItemId";

    private static JsonNode? JsonObjectValueOrNull(JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetPropertyValue(propertyName, out var value) ? value : null;

    private static object? JsonNodeDateValue(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (TryGetValue<string>(node, out var stringValue))
        {
            return DateOnly.ParseExact(stringValue, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var scalarValue = JsonNodeValue(node);
        return scalarValue is DateOnly date
            ? date
            : DateOnly.Parse(scalarValue!.ToString()!, CultureInfo.InvariantCulture);
    }

    private static object ParseScalarString(string value) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date
        )
            ? date
            : value;

    private object DocumentTimestampValue(DateTimeOffset value) =>
        _options.CreateSchemasAndTables ? FormatDateTimeOffset(value) : value;

    private static string FormatDateTimeOffset(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool TryGetValue<T>(JsonNode node, out T value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue(out T? parsedValue))
        {
            value = parsedValue;
            return true;
        }

        value = default!;
        return false;
    }

    private sealed record FixtureResourceKeyRow(
        short ResourceKeyId,
        string ProjectName,
        string ResourceName,
        string ResourceVersion
    );

    private abstract class FixtureSqlDialect
    {
        public abstract string UuidColumnType { get; }

        public abstract string TimestampWithOffsetColumnType { get; }

        public abstract string BooleanColumnType { get; }

        public abstract string Quote(string identifier);

        public abstract string CreateSchemaIfMissing(string schema);

        public abstract string CreateTableIfMissing(string schema, string table, string tableBody);

        public abstract string ParameterName(int ordinal);

        public abstract string Text(int length);

        public string QualifiedTable(string schema, string table) => $"{Quote(schema)}.{Quote(table)}";

        public string Insert(
            string schema,
            string table,
            IReadOnlyList<string> columns,
            IReadOnlyList<string> values
        )
        {
            var columnList = string.Join(", ", columns.Select(Quote));
            var valueList = string.Join(", ", values);

            return $"INSERT INTO {QualifiedTable(schema, table)} ({columnList}) VALUES ({valueList})";
        }

        public abstract string InsertIfMissing(
            string schema,
            string table,
            IReadOnlyList<string> columns,
            IReadOnlyList<string> values,
            IReadOnlyList<string> keyColumns
        );
    }

    private sealed class PostgresqlFixtureSqlDialect : FixtureSqlDialect
    {
        public override string UuidColumnType => "uuid";

        public override string TimestampWithOffsetColumnType => "text";

        public override string BooleanColumnType => "boolean";

        public override string Quote(string identifier) =>
            $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

        public override string CreateSchemaIfMissing(string schema) =>
            $"CREATE SCHEMA IF NOT EXISTS {Quote(schema)}";

        public override string CreateTableIfMissing(string schema, string table, string tableBody) =>
            $"""
                CREATE TABLE IF NOT EXISTS {QualifiedTable(schema, table)} (
                    {tableBody}
                )
                """;

        public override string ParameterName(int ordinal) =>
            $"@p{ordinal.ToString(CultureInfo.InvariantCulture)}";

        public override string Text(int length) =>
            $"varchar({length.ToString(CultureInfo.InvariantCulture)})";

        public override string InsertIfMissing(
            string schema,
            string table,
            IReadOnlyList<string> columns,
            IReadOnlyList<string> values,
            IReadOnlyList<string> keyColumns
        ) =>
            Insert(schema, table, columns, values)
            + $" ON CONFLICT ({string.Join(", ", keyColumns.Select(Quote))}) DO NOTHING";
    }

    private sealed class MssqlFixtureSqlDialect : FixtureSqlDialect
    {
        public override string UuidColumnType => "uniqueidentifier";

        public override string TimestampWithOffsetColumnType => "nvarchar(64)";

        public override string BooleanColumnType => "bit";

        public override string Quote(string identifier) =>
            $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

        public override string CreateSchemaIfMissing(string schema) =>
            $"IF SCHEMA_ID(N'{schema.Replace("'", "''", StringComparison.Ordinal)}') IS NULL EXEC(N'CREATE SCHEMA {Quote(schema).Replace("'", "''", StringComparison.Ordinal)}')";

        public override string CreateTableIfMissing(string schema, string table, string tableBody) =>
            $"""
                IF OBJECT_ID(N'{QualifiedTable(schema, table).Replace(
                    "'",
                    "''",
                    StringComparison.Ordinal
                )}', N'U') IS NULL
                CREATE TABLE {QualifiedTable(schema, table)} (
                    {tableBody}
                )
                """;

        public override string ParameterName(int ordinal) =>
            $"@p{ordinal.ToString(CultureInfo.InvariantCulture)}";

        public override string Text(int length) =>
            $"nvarchar({length.ToString(CultureInfo.InvariantCulture)})";

        public override string InsertIfMissing(
            string schema,
            string table,
            IReadOnlyList<string> columns,
            IReadOnlyList<string> values,
            IReadOnlyList<string> keyColumns
        )
        {
            var keyPredicate = string.Join(
                " AND ",
                keyColumns.Select((column, index) => $"{Quote(column)} = {values[index]}")
            );

            return $"IF NOT EXISTS (SELECT 1 FROM {QualifiedTable(schema, table)} WHERE {keyPredicate}) "
                + Insert(schema, table, columns, values);
        }
    }
}

public sealed class MaterializedDocumentFixtureSeederOptions
{
    public bool CreateSchemasAndTables { get; init; } = true;

    public bool SeedResourceKeys { get; init; } = true;
}
