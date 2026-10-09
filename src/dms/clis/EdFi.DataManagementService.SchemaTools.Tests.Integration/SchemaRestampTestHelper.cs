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

    internal static (
        string Hash,
        string Format,
        short Count,
        string Seed,
        DateTime AppliedAt,
        string[] Components,
        string[] Keys
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
        return (
            snapshot.Hash,
            snapshot.Format,
            snapshot.Count,
            snapshot.Seed,
            snapshot.AppliedAt,
            components,
            keys
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
