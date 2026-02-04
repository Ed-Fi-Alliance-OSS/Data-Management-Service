// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for an authoritative api schema for ed fi.
/// </summary>
[TestFixture]
public class Given_An_Authoritative_ApiSchema_For_Ed_Fi
{
    private string _diffOutput = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectRoot = FindProjectRoot(TestContext.CurrentContext.TestDirectory);
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "authoritative", "ds-5.2");
        var inputPath = Path.Combine(fixtureRoot, "inputs", "ds-5.2-api-schema-authoritative.json");
        var expectedPath = Path.Combine(
            fixtureRoot,
            "expected",
            "authoritative-relational-model.manifest.json"
        );
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "authoritative",
            "ds-5.2",
            "authoritative-relational-model.manifest.json"
        );

        File.Exists(inputPath).Should().BeTrue($"fixture missing at {inputPath}");

        var apiSchemaRoot = LoadApiSchemaRoot(inputPath);
        var manifest = BuildProjectManifest(apiSchemaRoot);

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, manifest);

        if (ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue($"authoritative manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate.");

        _diffOutput = RunGitDiff(expectedPath, actualPath);
    }

    /// <summary>
    /// It should match the authoritative manifest.
    /// </summary>
    [Test]
    public void It_should_match_the_authoritative_manifest()
    {
        if (!string.IsNullOrWhiteSpace(_diffOutput))
        {
            Assert.Fail(_diffOutput);
        }
    }

    /// <summary>
    /// Load api schema root.
    /// </summary>
    private static JsonNode LoadApiSchemaRoot(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path));

        return root ?? throw new InvalidOperationException($"ApiSchema parsed null: {path}");
    }

    /// <summary>
    /// Build project manifest.
    /// </summary>
    private static string BuildProjectManifest(JsonNode apiSchemaRoot)
    {
        if (apiSchemaRoot is not JsonObject rootObject)
        {
            throw new InvalidOperationException("ApiSchema root must be a JSON object.");
        }

        var projectSchema = RequireObject(rootObject["projectSchema"], "projectSchema");
        var projectName = RequireString(projectSchema, "projectName");
        var projectEndpointName = RequireString(projectSchema, "projectEndpointName");
        var projectVersion = RequireString(projectSchema, "projectVersion");
        var resourceSchemas = RequireObject(
            projectSchema["resourceSchemas"],
            "projectSchema.resourceSchemas"
        );

        var resourceEndpointNames = resourceSchemas
            .Select(entry => entry.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var pipeline = CreatePipeline();
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("project_name", projectName);
            writer.WriteString("project_endpoint_name", projectEndpointName);
            writer.WriteString("project_version", projectVersion);
            writer.WritePropertyName("resources");
            writer.WriteStartArray();

            foreach (var endpointName in resourceEndpointNames)
            {
                var context = new RelationalModelBuilderContext
                {
                    ApiSchemaRoot = apiSchemaRoot,
                    ResourceEndpointName = endpointName,
                };

                var buildResult = pipeline.Run(context);
                var manifest = RelationalModelManifestEmitter.Emit(buildResult);

                using var manifestDocument = JsonDocument.Parse(manifest);
                writer.WriteStartObject();
                writer.WriteString("resource_endpoint_name", endpointName);
                writer.WritePropertyName("manifest");
                manifestDocument.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);

        return json + "\n";
    }

    /// <summary>
    /// Create pipeline.
    /// </summary>
    private static RelationalModelBuilderPipeline CreatePipeline()
    {
        return new RelationalModelBuilderPipeline(
            new IRelationalModelBuilderStep[]
            {
                new ExtractInputsStep(),
                new ValidateJsonSchemaStep(),
                new DiscoverExtensionSitesStep(),
                new DeriveTableScopesAndKeysStep(),
                new DeriveColumnsAndBindDescriptorEdgesStep(),
                new CanonicalizeOrderingStep(),
            }
        );
    }

    /// <summary>
    /// Run git diff.
    /// </summary>
    private static string RunGitDiff(string expectedPath, string actualPath)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add("--no-index");
        startInfo.ArgumentList.Add("--ignore-space-at-eol");
        startInfo.ArgumentList.Add("--ignore-cr-at-eol");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(expectedPath);
        startInfo.ArgumentList.Add(actualPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode == 0)
        {
            return string.Empty;
        }

        if (process.ExitCode == 1)
        {
            return output;
        }

        return string.IsNullOrWhiteSpace(error) ? output : $"{error}\n{output}".Trim();
    }

    /// <summary>
    /// Should update goldens.
    /// </summary>
    private static bool ShouldUpdateGoldens()
    {
        var update = Environment.GetEnvironmentVariable("UPDATE_GOLDENS");

        return string.Equals(update, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(update, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Find project root.
    /// </summary>
    private static string FindProjectRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj"
            );
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj in parent directories."
        );
    }
}
