// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateIntegrationBoundaries")]
public class Given_CdcConnectorTemplateIntegrationBoundaryTests
{
    private static readonly IReadOnlyList<ForbiddenSourceToken> ForbiddenSourceTokens =
    [
        new("HttpClient", IsNeverAllowed),
        new("connectors/", IsNeverAllowed),
        new("CREATE PUBLICATION", IsNeverAllowed),
        new("ALTER PUBLICATION", IsNeverAllowed),
        new("sp_cdc_enable_table", IsNeverAllowed),
        new("ACL", IsNeverAllowed),
        new("offset.storage", IsNeverAllowed),
        new("topic.creation", IsAllowedTopicCreationGuardrail),
    ];

    private static readonly IReadOnlyList<string> GeneratedSourceFileSuffixes =
    [
        ".AssemblyInfo.cs",
        ".GlobalUsings.g.cs",
    ];

    [Test]
    public void It_returns_render_registration_and_artifact_evidence_without_connect_lifecycle_inputs()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(
            CdcProvider.SqlServer,
            artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                includeRedactedArtifactPayload: true
            )
        );

        CdcConnectorTemplateResult result = service.Render(request);

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.RegistrationPayload.Should().NotBeNull();
        result.RegistrationPayload!.Name.Should().Be(request.ConnectorName.Value);
        result.RegistrationPayload.Config.Should().Equal(result.Config);
        result.RedactedArtifactPayload.Should().NotBeNull();
        result
            .RedactedArtifactPayload!.FileName.Should()
            .Be(new CdcSafeName("cdc-connector-template.sqlserver.dms_binding_connector.manifest.json"));
        result.SchemaHistoryTopicName.Should().Be("edfi.documents.schema-history");
        result.Config.Should().Contain("name", request.ConnectorName.Value);
        result
            .Config.Should()
            .Contain("schema.history.internal.kafka.topic", "edfi.documents.schema-history");
        result
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("topic.creation.", StringComparison.Ordinal));
        result
            .Config.Keys.Should()
            .NotContain(key => key.Contains("offset.storage", StringComparison.Ordinal));
    }

    [Test]
    public void It_allows_preflight_and_live_validation_from_supplied_read_back_evidence()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);

        CdcConnectorTemplateResult preflight = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: request.BindingIdentity.BindingGeneration,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                )
            )
        );
        CdcConnectorTemplateResult liveReadBack = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: request.BindingIdentity.BindingGeneration,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                ),
                new CdcConnectorTemplateSourcePartitionEvidence(
                    new Dictionary<string, string> { ["server"] = request.ConnectorName.Value }
                )
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflight.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflight.Config.Should().Equal(rendered.Config);
        preflight.Diagnostics.Should().BeEmpty();
        liveReadBack.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        liveReadBack.Config.Should().Equal(rendered.Config);
        liveReadBack.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_keeps_connect_rest_provider_mutation_topic_acl_and_offset_lifecycle_code_out_of_scope()
    {
        SourceTokenMatch[] forbiddenMatches = EnumerateCdcSourceTokenMatches()
            .Where(match => !match.Token.IsAllowed(match.FilePath, match.Line))
            .ToArray();

        forbiddenMatches.Should().BeEmpty();
    }

    [Test]
    public void It_ignores_generated_build_output_when_scanning_boundary_source()
    {
        string sourceDirectory = CdcSourceDirectory();
        string objDirectory = Path.Combine(sourceDirectory, "obj", "CdcBoundaryGuardrailTests");
        string binDirectory = Path.Combine(sourceDirectory, "bin", "CdcBoundaryGuardrailTests");
        string objGeneratedFilePath = Path.Combine(objDirectory, "Generated.AssemblyInfo.cs");
        string binGeneratedFilePath = Path.Combine(binDirectory, "Generated.GlobalUsings.g.cs");

        Directory.CreateDirectory(objDirectory);
        Directory.CreateDirectory(binDirectory);
        File.WriteAllText(
            objGeneratedFilePath,
            "public static class GeneratedObjSource { public const string Forbidden = \"HttpClient\"; }"
        );
        File.WriteAllText(
            binGeneratedFilePath,
            "public static class GeneratedBinSource { public const string Forbidden = \"topic.creation\"; }"
        );

        try
        {
            IReadOnlyList<string> sourceFiles = CdcSourceFiles();

            using var _ = new AssertionScope();
            sourceFiles.Should().NotContain(objGeneratedFilePath);
            sourceFiles.Should().NotContain(binGeneratedFilePath);
            IsScannedCSharpSourceFile(Path.Combine(sourceDirectory, "Generated.AssemblyInfo.cs"))
                .Should()
                .BeFalse();
            IsScannedCSharpSourceFile(Path.Combine(sourceDirectory, "Generated.GlobalUsings.g.cs"))
                .Should()
                .BeFalse();
        }
        finally
        {
            DeleteFileIfExists(objGeneratedFilePath);
            DeleteFileIfExists(binGeneratedFilePath);
            DeleteDirectoryIfEmpty(objDirectory);
            DeleteDirectoryIfEmpty(binDirectory);
        }
    }

    [Test]
    public void It_documents_that_the_pinned_runtime_must_supply_the_murmur2_partitioner_class()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        CdcConnectorTemplateResult result = service.Render(BuildRequest(CdcProvider.Postgresql));

        using var _ = new AssertionScope();
        result
            .Config.Should()
            .Contain(
                "producer.override.partitioner.class",
                "org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner"
            );
        CdcSourceFiles()
            .Select(File.ReadAllText)
            .Should()
            .NotContain(
                source => source.Contains("class KafkaMurmur2V1Partitioner", StringComparison.Ordinal),
                "this repository emits the pinned-image partitioner class name while the qualified connector runtime owns packaging the implementation"
            );
    }

    private static ServiceProvider BuildServiceProvider() =>
        new ServiceCollection().AddCdcConnectorTemplates().BuildServiceProvider();

    private static IReadOnlyList<SourceTokenMatch> EnumerateCdcSourceTokenMatches() =>
        CdcSourceFiles()
            .SelectMany(filePath =>
                File.ReadLines(filePath)
                    .Select((line, index) => new { Line = line, LineNumber = index + 1 })
                    .SelectMany(line =>
                        ForbiddenSourceTokens
                            .Where(token => line.Line.Contains(token.Value, StringComparison.Ordinal))
                            .Select(token => new SourceTokenMatch(
                                Path.GetRelativePath(FindRepositoryRoot(), filePath),
                                line.LineNumber,
                                token,
                                line.Line.Trim()
                            ))
                    )
            )
            .ToArray();

    private static IReadOnlyList<string> CdcSourceFiles() =>
        EnumerateCdcSourceFiles(CdcSourceDirectory()).ToArray();

    private static IEnumerable<string> EnumerateCdcSourceFiles(string directoryPath)
    {
        foreach (
            string filePath in Directory.EnumerateFiles(directoryPath, "*.cs", SearchOption.TopDirectoryOnly)
        )
        {
            if (IsScannedCSharpSourceFile(filePath))
            {
                yield return filePath;
            }
        }

        foreach (string childDirectoryPath in Directory.EnumerateDirectories(directoryPath))
        {
            if (IsScannedSourceDirectory(childDirectoryPath))
            {
                foreach (string filePath in EnumerateCdcSourceFiles(childDirectoryPath))
                {
                    yield return filePath;
                }
            }
        }
    }

    private static string CdcSourceDirectory() =>
        Path.Combine(FindRepositoryRoot(), "src", "dms", "backend", "EdFi.DataManagementService.Backend.Cdc");

    private static bool IsScannedSourceDirectory(string directoryPath)
    {
        string directoryName = Path.GetFileName(directoryPath);

        return !directoryName.Equals("bin", StringComparison.OrdinalIgnoreCase)
            && !directoryName.Equals("obj", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsScannedCSharpSourceFile(string filePath)
    {
        string fileName = Path.GetFileName(filePath);

        return !GeneratedSourceFileSuffixes.Any(suffix =>
            fileName.EndsWith(suffix, StringComparison.Ordinal)
        );
    }

    private static void DeleteFileIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static void DeleteDirectoryIfEmpty(string directoryPath)
    {
        if (Directory.Exists(directoryPath) && !Directory.EnumerateFileSystemEntries(directoryPath).Any())
        {
            Directory.Delete(directoryPath);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "dms", "EdFi.DataManagementService.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from the test directory.");
    }

    private static bool IsNeverAllowed(string filePath, string line) => false;

    private static bool IsAllowedTopicCreationGuardrail(string filePath, string line) =>
        Path.GetFileName(filePath) == "CdcConnectorTemplateInputValidation.cs"
        && line.Contains("\"topic.creation.\"", StringComparison.Ordinal);

    private sealed record ForbiddenSourceToken(string Value, Func<string, string, bool> IsAllowed);

    private sealed record SourceTokenMatch(
        string FilePath,
        int LineNumber,
        ForbiddenSourceToken Token,
        string Line
    );
}
