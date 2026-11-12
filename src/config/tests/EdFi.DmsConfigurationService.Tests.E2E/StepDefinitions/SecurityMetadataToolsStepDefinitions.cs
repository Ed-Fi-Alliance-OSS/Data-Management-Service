// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Tests.E2E.Management;
using FluentAssertions;
using Reqnroll;

namespace EdFi.DmsConfigurationService.Tests.E2E.StepDefinitions;

[Binding]
public sealed class SecurityMetadataToolsStepDefinitions(ScenarioContext scenarioContext)
{
    private readonly ClaimsDatabaseSnapshot _database = new();
    private string _artifactDirectory = string.Empty;
    private string _outputPath = string.Empty;
    private byte[] _uploadedBytes = [];
    private JsonObject _converted = null!;
    private JsonNode _standardProvider = null!;
    private bool _restoreProvider;
    private List<ClaimsDatabaseSnapshot.CatalogEntry> _isolatedCatalog = [];
    private StepDefinitions Steps => scenarioContext.ScenarioContainer.Resolve<StepDefinitions>();

    [Given("SecurityMetadataTools converts the representative ODS export")]
    public async Task GivenTheRepresentativeExportIsConverted()
    {
        _artifactDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "TestResults",
            $"SecurityMetadataTools-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_artifactDirectory);
        _outputPath = Path.Combine(_artifactDirectory, "converted-claims.json");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "SecurityMetadataTools",
                        "security-metadata-tools.dll"
                    ),
                    "convert-ods-xml",
                    "--input",
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "TestData",
                        "Claims",
                        "OdsSecurityMetadata",
                        "representative.xml"
                    ),
                    "--output",
                    _outputPath,
                },
            },
        };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0, await stderr);
        (await stdout).Should().BeEmpty();
        (await stderr).Should().BeEmpty();
        TestContext.AddTestAttachment(
            _outputPath,
            "Unmodified SecurityMetadataTools output uploaded by this scenario"
        );
        _converted = JsonNode.Parse(await File.ReadAllBytesAsync(_outputPath))!.AsObject();
    }

    [Given("an isolated claims catalog baseline is captured")]
    public async Task GivenAnIsolatedCatalogBaselineIsCaptured()
    {
        // Capture before any mutation, including the reload used to establish the standard provider.
        await _database.CaptureAsync();
        await SaveEvidenceAsync("baseline-catalog.json", JsonSerializer.Serialize(_database.Catalog));
        await SaveEvidenceAsync("baseline-hierarchy.json", _database.Hierarchy);

        // Start with the standard configured composition, even when earlier scenarios uploaded claims.
        _restoreProvider = true;
        await ReloadStandardClaimsAsync();
        await Steps.WhenAGETRequestIsMadeTo("/management/current-claims");
        Steps.LastApiResponse.Status.Should().Be(200);
        _standardProvider = JsonNode.Parse(await Steps.LastApiResponse.TextAsync())!;
        string importedNameToInsert = ConvertedCatalog().First().Name;
        await _database.IsolateAsync(importedNameToInsert);
        _isolatedCatalog = await _database.ReadCatalogAsync();
        _isolatedCatalog
            .Should()
            .BeEquivalentTo(
                _database.Catalog.Where(entry => entry.IsSystemReserved && entry.Name != importedNameToInsert)
            );
        _isolatedCatalog.Should().NotContain(entry => entry.Name == importedNameToInsert);
        string[] importedNames = ConvertedCatalog().Select(entry => entry.Name).ToArray();
        importedNames
            .Except(_isolatedCatalog.Select(entry => entry.Name), StringComparer.Ordinal)
            .Should()
            .NotBeEmpty("at least one imported name must be absent before the upload");
        _isolatedCatalog
            .Where(entry => importedNames.Contains(entry.Name, StringComparer.Ordinal))
            .Should()
            .NotBeEmpty("the fixture must exercise importing an existing reserved name without demoting it");
        await SaveEvidenceAsync("isolated-catalog.json", JsonSerializer.Serialize(_isolatedCatalog));
    }

    [When("the SecurityMetadataTools output file is uploaded verbatim")]
    public async Task WhenTheOutputIsUploadedVerbatim()
    {
        // DataByte avoids even decoding/re-encoding the file on its way to the HTTP request.
        _uploadedBytes = await File.ReadAllBytesAsync(_outputPath);
        await Steps.PostVerbatimBodyAsync("/management/upload-claims", _uploadedBytes);
    }

    [Then("the current claims match the SecurityMetadataTools output")]
    public async Task ThenCurrentClaimsMatchTheOutput()
    {
        string response = await Steps.LastApiResponse.TextAsync();
        await SaveEvidenceAsync("provider-claims.json", response);
        JsonNode
            .DeepEquals(_converted, JsonNode.Parse(response))
            .Should()
            .BeTrue(
                "the provider must retain the complete converted document, including every imported isSystemReserved=false flag"
            );
        ConvertedCatalog().Should().OnlyContain(entry => !entry.IsSystemReserved);
        (await File.ReadAllBytesAsync(_outputPath)).Should().Equal(_uploadedBytes);
    }

    [Then("the converted hierarchy and catalog are independently persisted")]
    public async Task ThenTheHierarchyAndCatalogArePersisted()
    {
        var persistedCatalog = await _database.ReadCatalogAsync();
        string persistedHierarchy = await _database.ReadHierarchyAsync();
        await SaveEvidenceAsync("persisted-catalog.json", JsonSerializer.Serialize(persistedCatalog));
        await SaveEvidenceAsync("persisted-hierarchy.json", persistedHierarchy);

        // Use the same model and default serializer as ClaimsDocumentRepository, on BOTH sides.
        // This retains all policy/order checks while accounting for absent defaults becoming null
        // and absent collection properties becoming empty arrays in the persisted representation.
        JsonNode
            .DeepEquals(
                NormalizeHierarchy(_converted["claimsHierarchy"]!.ToJsonString()),
                NormalizeHierarchy(persistedHierarchy)
            )
            .Should()
            .BeTrue("persisted policy must match every converted claim, action, strategy and override");

        var reserved = _isolatedCatalog.Where(entry => entry.IsSystemReserved).ToList();
        var expectedCatalog = reserved
            .Select(entry => new CatalogValue(entry.Name, entry.IsSystemReserved))
            .Concat(
                ConvertedCatalog().Where(entry => !reserved.Exists(existing => existing.Name == entry.Name))
            );
        persistedCatalog
            .Select(entry => new CatalogValue(entry.Name, entry.IsSystemReserved))
            .Should()
            .BeEquivalentTo(expectedCatalog);
        persistedCatalog
            .Where(entry => entry.IsSystemReserved)
            .Should()
            .BeEquivalentTo(
                reserved,
                "existing reserved rows must keep their IDs and stored flags even when the import says false"
            );
        var newlyImported = persistedCatalog
            .Where(entry => !_isolatedCatalog.Exists(existing => existing.Name == entry.Name))
            .ToList();
        newlyImported
            .Select(entry => new CatalogValue(entry.Name, entry.IsSystemReserved))
            .Should()
            .BeEquivalentTo(
                ConvertedCatalog()
                    .Where(entry => !_isolatedCatalog.Exists(existing => existing.Name == entry.Name))
            );
        newlyImported.Should().NotBeEmpty().And.OnlyContain(entry => !entry.IsSystemReserved);
    }

    [AfterScenario("SecurityMetadataTools")]
    public async Task RestoreClaimsAsync()
    {
        try
        {
            if (_restoreProvider)
            {
                await ReloadStandardClaimsAsync();
                await Steps.WhenAGETRequestIsMadeTo("/management/current-claims");
                Steps.LastApiResponse.Status.Should().Be(200);
                string restoredProvider = await Steps.LastApiResponse.TextAsync();
                await SaveEvidenceAsync("restored-provider-claims.json", restoredProvider);
                if (_standardProvider is not null)
                {
                    JsonNode
                        .DeepEquals(_standardProvider, JsonNode.Parse(restoredProvider))
                        .Should()
                        .BeTrue("cleanup must restore the standard provider composition");
                }
            }
        }
        finally
        {
            // Always restore the database, including when reload or a provider assertion fails.
            try
            {
                await _database.RestoreAsync();
                if (_database.Captured)
                {
                    await SaveEvidenceAsync(
                        "restored-catalog.json",
                        JsonSerializer.Serialize(await _database.ReadCatalogAsync())
                    );
                    await SaveEvidenceAsync("restored-hierarchy.json", await _database.ReadHierarchyAsync());
                }
            }
            finally
            {
                await _database.DisposeAsync();
            }
        }
    }

    private async Task ReloadStandardClaimsAsync()
    {
        await Steps.PostVerbatimBodyAsync("/management/reload-claims", "{}");
        Steps
            .LastApiResponse.Status.Should()
            .Be(200, "the isolated CMS must enable unrestricted claims loading");
    }

    private static JsonNode NormalizeHierarchy(string json) =>
        JsonSerializer.SerializeToNode(JsonSerializer.Deserialize<List<Claim>>(json)!)!;

    private IEnumerable<CatalogValue> ConvertedCatalog() =>
        _converted["claimSets"]!
            .AsArray()
            .Select(entry => new CatalogValue(
                entry!["claimSetName"]!.GetValue<string>(),
                entry["isSystemReserved"]!.GetValue<bool>()
            ));

    private async Task SaveEvidenceAsync(string fileName, string contents)
    {
        string path = Path.Combine(_artifactDirectory, fileName);
        await File.WriteAllTextAsync(path, contents);
        TestContext.AddTestAttachment(path);
    }

    private sealed record CatalogValue(string Name, bool IsSystemReserved);
}
