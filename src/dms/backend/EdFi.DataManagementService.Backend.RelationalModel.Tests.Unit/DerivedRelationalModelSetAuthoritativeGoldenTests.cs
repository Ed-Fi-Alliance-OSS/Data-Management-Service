// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.RelationalModel.Manifest.DerivedModelSetManifestEmitter;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for an authoritative core and extension effective schema set.
/// </summary>
[TestFixture]
public class Given_An_Authoritative_Core_And_Extension_EffectiveSchemaSet
{
    private string _diffOutput = default!;
    private static readonly QualifiedResourceName[] _detailedResources =
    [
        new QualifiedResourceName("Ed-Fi", "AssessmentAdministration"),
        new QualifiedResourceName("Ed-Fi", "ProgramEvaluationElement"),
        new QualifiedResourceName("Ed-Fi", "StudentAssessmentRegistration"),
        new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation"),
    ];

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectRoot = FindProjectRoot(TestContext.CurrentContext.TestDirectory);
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "authoritative");
        var coreInputPath = Path.Combine(
            fixtureRoot,
            "ds-5.2",
            "inputs",
            "ds-5.2-api-schema-authoritative.json"
        );
        var extensionInputPath = Path.Combine(
            fixtureRoot,
            "sample",
            "inputs",
            "sample-api-schema-authoritative.json"
        );
        var expectedPath = Path.Combine(
            fixtureRoot,
            "sample",
            "expected",
            "authoritative-derived-relational-model-set.json"
        );
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "authoritative",
            "sample",
            "authoritative-derived-relational-model-set.json"
        );

        File.Exists(coreInputPath).Should().BeTrue($"fixture missing at {coreInputPath}");
        File.Exists(extensionInputPath).Should().BeTrue($"fixture missing at {extensionInputPath}");

        var coreSchema = LoadProjectSchema(coreInputPath);
        var extensionSchema = LoadProjectSchema(extensionInputPath);

        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(coreSchema, false);
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionSchema,
            true
        );

        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);

        var extensionSiteCapture = new ExtensionSiteCapturePass();
        IRelationalModelSetPass[] passes =
        [
            .. RelationalModelSetPasses.CreateDefault(),
            extensionSiteCapture,
        ];
        var builder = new DerivedRelationalModelSetBuilder(passes);
        var derivedSet = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var detailedResourceSet = new HashSet<QualifiedResourceName>(_detailedResources);
        var manifest = Emit(derivedSet, detailedResourceSet, extensionSiteCapture.GetExtensionSites);

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
    /// Load project schema.
    /// </summary>
    private static JsonObject LoadProjectSchema(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path));

        if (root is not JsonObject rootObject)
        {
            throw new InvalidOperationException($"ApiSchema parsed null or non-object: {path}");
        }

        return RequireObject(rootObject["projectSchema"], "projectSchema");
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
        var errorTask = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var error = errorTask.Result;

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
    /// Test type extension site capture pass.
    /// </summary>
    private sealed class ExtensionSiteCapturePass : IRelationalModelSetPass
    {
        private readonly Dictionary<QualifiedResourceName, IReadOnlyList<ExtensionSite>> _sitesByResource =
            new();

        /// <summary>
        /// Execute.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            foreach (var resource in context.ConcreteResourcesInNameOrder)
            {
                _sitesByResource[resource.ResourceKey.Resource] = context.GetExtensionSitesForResource(
                    resource.ResourceKey.Resource
                );
            }
        }

        /// <summary>
        /// Get extension sites.
        /// </summary>
        public IReadOnlyList<ExtensionSite> GetExtensionSites(QualifiedResourceName resource)
        {
            return _sitesByResource.TryGetValue(resource, out var sites) ? sites : [];
        }
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
