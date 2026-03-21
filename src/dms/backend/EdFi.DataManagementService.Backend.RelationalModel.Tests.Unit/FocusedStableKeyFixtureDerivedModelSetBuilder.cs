// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using EdFi.DataManagementService.Backend.Tests.Common;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

internal static class FocusedStableKeyFixtureDerivedModelSetBuilder
{
    private const string ProjectFileName =
        "EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj";
    private const string FixtureInputsDirectoryName = "inputs";
    private const string FixtureInputsPropertyName = "inputs";

    public static DerivedRelationalModelSet Build(
        string fixtureRelativePath,
        SqlDialect dialect,
        bool useStrictPasses = false
    )
    {
        var fixturePath = GetFixturePath(fixtureRelativePath);
        var fixtureRoot = ParseJsonObject(fixturePath, "Fixture");
        var projects = LoadProjects(fixtureRoot, fixturePath);
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(projects);
        var setPasses = useStrictPasses
            ? RelationalModelSetPasses.CreateStrict()
            : RelationalModelSetPasses.CreateDefault();

        return new DerivedRelationalModelSetBuilder(setPasses).Build(
            schemaSet,
            dialect,
            CreateDialectRules(dialect)
        );
    }

    private static IReadOnlyList<EffectiveProjectSchema> LoadProjects(
        JsonObject fixtureRoot,
        string fixturePath
    )
    {
        var inputEntries = RequireArray(fixtureRoot[FixtureInputsPropertyName], FixtureInputsPropertyName);
        List<EffectiveProjectSchema> projects = new(inputEntries.Count);

        foreach (var inputEntry in inputEntries)
        {
            var inputObject = RequireObject(inputEntry, "inputs entry");
            var fileName = RequireString(inputObject, "fileName");
            var isExtensionProject = inputObject["isExtensionProject"]?.GetValue<bool>() ?? false;
            var inputPath = Path.Combine(
                Path.GetDirectoryName(fixturePath)
                    ?? throw new InvalidOperationException(
                        $"Unable to resolve fixture directory for '{fixturePath}'."
                    ),
                FixtureInputsDirectoryName,
                fileName
            );
            var inputRoot = ParseJsonObject(inputPath, "Fixture input");
            var projectSchema = RequireObject(inputRoot["projectSchema"], "projectSchema");

            projects.Add(
                EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
                    projectSchema,
                    isExtensionProject
                )
            );
        }

        return projects;
    }

    private static ISqlDialectRules CreateDialectRules(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new PgsqlDialectRules(),
            SqlDialect.Mssql => new MssqlDialectRules(),
            _ => throw new NotSupportedException($"Unsupported dialect '{dialect}'."),
        };
    }

    private static string GetFixturePath(string fixtureRelativePath)
    {
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            ProjectFileName
        );

        return Path.Combine(projectRoot, fixtureRelativePath);
    }

    private static JsonArray RequireArray(JsonNode? node, string path)
    {
        return node as JsonArray
            ?? throw new InvalidOperationException($"Expected '{path}' to be a JSON array.");
    }

    private static JsonObject RequireObject(JsonNode? node, string path)
    {
        return node as JsonObject
            ?? throw new InvalidOperationException($"Expected '{path}' to be a JSON object.");
    }

    private static string RequireString(JsonObject node, string propertyName)
    {
        return node[propertyName]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Expected '{propertyName}' to be a string property.");
    }

    private static JsonObject ParseJsonObject(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{description} not found: {path}", path);
        }

        var root = JsonNode.Parse(File.ReadAllText(path));

        return root as JsonObject
            ?? throw new InvalidOperationException($"{description} '{path}' parsed null or non-object.");
    }
}
