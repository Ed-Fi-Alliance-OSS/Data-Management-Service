// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Playwright;

namespace EdFi.DmsConfigurationService.Tests.E2E.SecurityMetadataTools;

/// <summary>
/// Shared setup for the policy scenarios added alongside their launcher selectors.
/// These tests own a fresh isolated deployment; they never use Reqnroll cleanup or snapshots.
/// </summary>
[TestFixture]
[Category("SecurityMetadataToolsService")]
[NonParallelizable]
public partial class Given_SecurityMetadataToolsService
{
    private IPlaywright _playwright = null!;
    private IAPIRequestContext _context = null!;
    protected string FixtureDirectory { get; private set; } = string.Empty;
    protected string EvidenceDirectory { get; private set; } = string.Empty;
    protected string ToolPath { get; private set; } = string.Empty;

    [OneTimeSetUp]
    public async Task SetUpIsolatedScenario()
    {
        if (Environment.GetEnvironmentVariable("SECURITY_METADATA_TOOLS_SERVICE_RUNNER") != "true")
        {
            Assert.Ignore("Policy scenarios require Invoke-IsolatedConfigE2E.ps1.");
        }

        FixtureDirectory = RequiredSetting("SECURITY_METADATA_TOOLS_FIXTURE_DIRECTORY");
        EvidenceDirectory = RequiredSetting("SECURITY_METADATA_TOOLS_EVIDENCE_DIRECTORY");
        ToolPath = Path.Combine(
            AppContext.BaseDirectory,
            "SecurityMetadataTools",
            "security-metadata-tools.dll"
        );
        _playwright = await Playwright.CreateAsync();
        _context = await _playwright.APIRequest.NewContextAsync(
            new() { BaseURL = RequiredSetting("CONFIGURATION_SERVICE_E2E_API_URL"), IgnoreHTTPSErrors = true }
        );
    }

    [OneTimeTearDown]
    public async Task TearDownIsolatedScenario()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
        _playwright?.Dispose();
    }

    private static string RequiredSetting(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The isolated service runner must set {name}.");
}
