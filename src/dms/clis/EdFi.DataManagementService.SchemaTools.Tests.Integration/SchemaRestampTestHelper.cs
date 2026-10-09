// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

internal static class SchemaRestampTestHelper
{
    internal static string ExtensionPath =>
        Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "restamp-extension-api-schema.json"
        );

    internal static EffectiveSchemaInfo BuildTarget(params string[] schemaPaths)
    {
        var loader = new ApiSchemaFileLoader(
            new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance),
            NullLogger<ApiSchemaFileLoader>.Instance
        );
        var result = loader.Load(schemaPaths[0], schemaPaths[1..]);
        if (result is not ApiSchemaFileLoadResult.SuccessResult success)
        {
            throw new InvalidOperationException($"Could not load re-stamp fixture: {result.GetType().Name}");
        }

        var builder = new EffectiveSchemaSetBuilder(
            new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
            new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
        );
        return builder.Build(success.NormalizedNodes).EffectiveSchema;
    }

    internal static (int ExitCode, string Output, string Error) RunRestamp(
        string connectionString,
        string dialect,
        bool migrationCompleted,
        params string[] schemaPaths
    )
    {
        List<string> arguments =
        [
            "ddl",
            "re-stamp",
            "--schema",
            .. schemaPaths,
            "--connection-string",
            connectionString,
            "--dialect",
            dialect,
        ];
        if (migrationCompleted)
        {
            arguments.Add("--migration-completed");
        }
        return CliTestHelper.RunCli([.. arguments]);
    }

    internal static void SetOldHash(DbConnection connection, string dialect, string oldHash)
    {
        var parent = Table(dialect, "EffectiveSchema");
        var child = Table(dialect, "SchemaComponent");
        var hash = Column(dialect, "EffectiveSchemaHash");
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {child}; UPDATE {parent} SET {hash} = @oldHash;";
        AddParameter(command, "oldHash", oldHash);
        command.ExecuteNonQuery();
    }

    internal static void InsertComponents(
        DbConnection connection,
        string dialect,
        EffectiveSchemaInfo target,
        string hashValue
    )
    {
        var child = Table(dialect, "SchemaComponent");
        foreach (var component in target.SchemaComponentsInEndpointOrder)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"INSERT INTO {child} ({Column(dialect, "EffectiveSchemaHash")}, {Column(dialect, "ProjectEndpointName")}, {Column(dialect, "ProjectName")}, {Column(dialect, "ProjectVersion")}, {Column(dialect, "IsExtensionProject")}) VALUES (@hash, @endpoint, @name, @version, @extension);";
            AddParameter(command, "hash", hashValue);
            AddParameter(command, "endpoint", component.ProjectEndpointName);
            AddParameter(command, "name", component.ProjectName);
            AddParameter(command, "version", component.ProjectVersion);
            AddParameter(command, "extension", component.IsExtensionProject);
            command.ExecuteNonQuery();
        }
    }

    internal static void InstallDmlRejectingTriggers(DbConnection connection, string dialect)
    {
        string[] statements = dialect switch
        {
            "pgsql" =>
            [
                """
                    CREATE FUNCTION dms.reject_restamp_dml() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN RAISE EXCEPTION 'DML rejected by re-stamp no-op test'; END; $$;
                    """,
                "CREATE TRIGGER reject_restamp_dml BEFORE INSERT OR UPDATE OR DELETE ON dms.\"EffectiveSchema\" FOR EACH ROW EXECUTE FUNCTION dms.reject_restamp_dml();",
                "CREATE TRIGGER reject_restamp_dml BEFORE INSERT OR UPDATE OR DELETE ON dms.\"ResourceKey\" FOR EACH ROW EXECUTE FUNCTION dms.reject_restamp_dml();",
                "CREATE TRIGGER reject_restamp_dml BEFORE INSERT OR UPDATE OR DELETE ON dms.\"SchemaComponent\" FOR EACH ROW EXECUTE FUNCTION dms.reject_restamp_dml();",
            ],
            "mssql" =>
            [
                "CREATE TRIGGER [dms].[reject_restamp_effective_schema] ON [dms].[EffectiveSchema] AFTER INSERT, UPDATE, DELETE AS THROW 51000, 'DML rejected by re-stamp no-op test', 1;",
                "CREATE TRIGGER [dms].[reject_restamp_resource_key] ON [dms].[ResourceKey] AFTER INSERT, UPDATE, DELETE AS THROW 51000, 'DML rejected by re-stamp no-op test', 1;",
                "CREATE TRIGGER [dms].[reject_restamp_schema_component] ON [dms].[SchemaComponent] AFTER INSERT, UPDATE, DELETE AS THROW 51000, 'DML rejected by re-stamp no-op test', 1;",
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
        };
        foreach (var sql in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    internal static void InstallParentUpdateRejectingTrigger(DbConnection connection, string dialect)
    {
        using var command = connection.CreateCommand();
        command.CommandText = dialect switch
        {
            "pgsql" => """
                CREATE FUNCTION dms.reject_restamp_parent_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'parent update rejected by re-stamp rollback test'; END; $$;
                CREATE TRIGGER reject_restamp_parent_update BEFORE UPDATE ON dms."EffectiveSchema" FOR EACH ROW EXECUTE FUNCTION dms.reject_restamp_parent_update();
                """,
            "mssql" =>
                "CREATE TRIGGER [dms].[reject_restamp_parent_update] ON [dms].[EffectiveSchema] AFTER UPDATE AS THROW 51000, 'Parent update rejected by re-stamp rollback test', 1;",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
        };
        command.ExecuteNonQuery();
    }

    internal static void InstallComponentInsertRejectingTrigger(
        DbConnection connection,
        string dialect,
        string endpointName
    )
    {
        using var command = connection.CreateCommand();
        var endpoint = endpointName.Replace("'", "''", StringComparison.Ordinal);
        command.CommandText = dialect switch
        {
            "pgsql" => $"""
                CREATE FUNCTION dms.reject_restamp_component_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."ProjectEndpointName" = '{endpoint}' THEN
                        RAISE EXCEPTION 'component insert rejected by re-stamp rollback test';
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER reject_restamp_component_insert BEFORE INSERT ON dms."SchemaComponent" FOR EACH ROW EXECUTE FUNCTION dms.reject_restamp_component_insert();
                """,
            "mssql" => $"""
                CREATE TRIGGER [dms].[reject_restamp_component_insert] ON [dms].[SchemaComponent] AFTER INSERT AS
                BEGIN
                    IF EXISTS (SELECT 1 FROM inserted WHERE [ProjectEndpointName] = '{endpoint}')
                        THROW 51001, 'Component insert rejected by re-stamp rollback test', 1;
                END;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
        };
        command.ExecuteNonQuery();
    }

    internal static void CorruptMetadata(DbConnection connection, string dialect, string field)
    {
        using var command = connection.CreateCommand();
        command.CommandText = (dialect, field) switch
        {
            ("pgsql", "format") =>
                "UPDATE dms.\"EffectiveSchema\" SET \"ApiSchemaFormatVersion\" = 'invalid';",
            ("mssql", "format") => "UPDATE [dms].[EffectiveSchema] SET [ApiSchemaFormatVersion] = 'invalid';",
            ("pgsql", "count") =>
                "UPDATE dms.\"EffectiveSchema\" SET \"ResourceKeyCount\" = \"ResourceKeyCount\" + 1;",
            ("mssql", "count") =>
                "UPDATE [dms].[EffectiveSchema] SET [ResourceKeyCount] = [ResourceKeyCount] + 1;",
            ("pgsql", "component") =>
                "UPDATE dms.\"SchemaComponent\" SET \"ProjectVersion\" = 'invalid' WHERE \"ProjectEndpointName\" = (SELECT MIN(\"ProjectEndpointName\") FROM dms.\"SchemaComponent\");",
            ("mssql", "component") =>
                "UPDATE [dms].[SchemaComponent] SET [ProjectVersion] = 'invalid' WHERE [ProjectEndpointName] = (SELECT MIN([ProjectEndpointName]) FROM [dms].[SchemaComponent]);",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        command.ExecuteNonQuery();
    }

    internal static (
        string Hash,
        string Format,
        short Count,
        string Seed,
        DateTime AppliedAt,
        string[] Components,
        string[] Keys,
        string[] Documents,
        string[] CacheRows,
        string[] ProjectionWork
    ) Capture(DbConnection connection, string dialect)
    {
        var parent = Table(dialect, "EffectiveSchema");
        using var parentCommand = connection.CreateCommand();
        parentCommand.CommandText =
            $"SELECT {Column(dialect, "EffectiveSchemaHash")}, {Column(dialect, "ApiSchemaFormatVersion")}, {Column(dialect, "ResourceKeyCount")}, {Column(dialect, "ResourceKeySeedHash")}, {Column(dialect, "AppliedAt")} FROM {parent};";
        using var parentReader = parentCommand.ExecuteReader();
        if (!parentReader.Read())
        {
            throw new InvalidOperationException("Missing test fixture fingerprint.");
        }
        var snapshot = (
            Hash: parentReader.GetString(0),
            Format: parentReader.GetString(1),
            Count: parentReader.GetInt16(2),
            Seed: Convert.ToHexString((byte[])parentReader.GetValue(3)),
            AppliedAt: parentReader.GetDateTime(4)
        );
        parentReader.Close();

        string[] ReadRows(string table, string columns, string orderBy)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {columns} FROM {Table(dialect, table)} ORDER BY {orderBy};";
            using var reader = command.ExecuteReader();
            List<string> rows = [];
            while (reader.Read())
            {
                rows.Add(
                    string.Join(
                        "|",
                        Enumerable
                            .Range(0, reader.FieldCount)
                            .Select(i =>
                                Convert.ToString(
                                    reader.GetValue(i),
                                    System.Globalization.CultureInfo.InvariantCulture
                                )
                            )
                    )
                );
            }
            return [.. rows];
        }

        var components = ReadRows(
            "SchemaComponent",
            string.Join(
                ", ",
                new[]
                {
                    "EffectiveSchemaHash",
                    "ProjectEndpointName",
                    "ProjectName",
                    "ProjectVersion",
                    "IsExtensionProject",
                }.Select(name => Column(dialect, name))
            ),
            Column(dialect, "ProjectEndpointName")
        );
        var keys = ReadRows(
            "ResourceKey",
            string.Join(
                ", ",
                new[] { "ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion" }.Select(name =>
                    Column(dialect, name)
                )
            ),
            Column(dialect, "ResourceKeyId")
        );
        var documents = ReadRows(
            "Document",
            string.Join(
                ", ",
                new[]
                {
                    "DocumentId",
                    "DocumentUuid",
                    "ResourceKeyId",
                    "ContentVersion",
                    "ContentLastModifiedAt",
                }.Select(name => Column(dialect, name))
            ),
            Column(dialect, "DocumentId")
        );
        var cacheRows = ReadRows(
            "DocumentCache",
            string.Join(
                ", ",
                new[]
                {
                    "DocumentId",
                    "DocumentUuid",
                    "ProjectName",
                    "ResourceName",
                    "ResourceVersion",
                    "ContentVersion",
                    "StreamEtag",
                    "LastModifiedAt",
                    "DocumentJson",
                }.Select(name => Column(dialect, name))
            ),
            Column(dialect, "DocumentId")
        );
        var projectionWork = ReadRows(
            "DocumentProjectionWork",
            string.Join(
                ", ",
                new[] { "DocumentId", "RequiredContentVersion", "FirstEnqueuedAt", "LastEnqueuedAt" }.Select(
                    name => Column(dialect, name)
                )
            ),
            Column(dialect, "DocumentId")
        );
        return (
            snapshot.Hash,
            snapshot.Format,
            snapshot.Count,
            snapshot.Seed,
            snapshot.AppliedAt,
            components,
            keys,
            documents,
            cacheRows,
            projectionWork
        );
    }

    private static string Table(string dialect, string name) =>
        dialect == "pgsql" ? $"dms.\"{name}\"" : $"[dms].[{name}]";

    private static string Column(string dialect, string name) =>
        dialect == "pgsql" ? $"\"{name}\"" : $"[{name}]";

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
