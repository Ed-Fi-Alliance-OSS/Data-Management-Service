// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Playwright;

namespace EdFi.DmsConfigurationService.Tests.E2E.SecurityMetadataTools;

/// <summary>
/// Shared setup and startup parity checks for the isolated policy scenario selectors.
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

    [TestCase("5.2", TestName = "It_matches_reviewed_Hybrid_permissions_for_DS_5_2")]
    [TestCase("6.1", TestName = "It_matches_reviewed_Hybrid_permissions_for_DS_6_1")]
    [Category("SecurityMetadataToolsHybrid")]
    public async Task It_matches_reviewed_Hybrid_permissions(string version)
    {
        await AssertContainerAsync("Hybrid", version);
        string referenceDirectory = HybridReferenceDirectory;
        var sourceHashes = HashFiles(Path.Combine(referenceDirectory, "fragments"));
        HashFiles(FixtureDirectory).Should().BeEquivalentTo(sourceHashes);

        // The same startup mount is supplied to the standalone CLI; generated files stay outside it.
        string merged = Path.Combine(EvidenceDirectory, "merged.json");
        await RunToolAsync(
            "merge",
            "",
            ["merge", "--fragments", FixtureDirectory, "--data-standard-version", version, "--output", merged]
        );
        await AssertMergedPermissionsAsync(merged, version);
        HashFiles(FixtureDirectory).Should().BeEquivalentTo(sourceHashes);
        HashFiles(Path.Combine(referenceDirectory, "fragments")).Should().BeEquivalentTo(sourceHashes);
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDirectory, "fixture-sha256.json"),
            JsonSerializer.Serialize(sourceHashes)
        );
    }

    [Test]
    [Category("SecurityMetadataToolsFilesystem")]
    public async Task It_loads_unmodified_merge_output_through_Filesystem_startup()
    {
        await AssertContainerAsync("Filesystem", "5.2");
        Directory
            .GetFileSystemEntries(FixtureDirectory)
            .Should()
            .Equal(Path.Combine(FixtureDirectory, "Claims.json"));
        string merged = Path.Combine(FixtureDirectory, "Claims.json");
        byte[] startupBytes = await File.ReadAllBytesAsync(Path.Combine(EvidenceDirectory, "merged.json"));
        (await File.ReadAllBytesAsync(merged)).Should().Equal(startupBytes);
        string beforeHash = (
            await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "merged-sha256-before.txt"))
        ).Trim();
        Convert.ToHexString(SHA256.HashData(startupBytes)).Should().Be(beforeHash);

        await AssertMergedPermissionsAsync(merged, "5.2");

        (await File.ReadAllBytesAsync(merged)).Should().Equal(startupBytes);
        var afterHashes = HashFiles(FixtureDirectory);
        afterHashes.Should().BeEquivalentTo(new Dictionary<string, string> { ["Claims.json"] = beforeHash });
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDirectory, "fixture-sha256-after.json"),
            JsonSerializer.Serialize(afterHashes)
        );
    }

    [Test]
    [Category("SecurityMetadataToolsUpload")]
    public async Task It_uploads_unmodified_merge_output_and_verifies_stored_permissions()
    {
        await AssertContainerAsync("Hybrid", "5.2", unrestrictedLoading: true);
        Directory.GetFileSystemEntries(FixtureDirectory).Should().BeEmpty();
        string fragments = Path.Combine(HybridReferenceDirectory, "fragments");
        var sourceHashes = HashFiles(fragments);
        string token = await GetAccessTokenAsync();
        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };

        // An empty fragment directory selects exactly the bundled base. Check the stored full
        // catalog and permissions before upload so unrelated retained sets cannot mask a mismatch.
        string baseline = Path.Combine(EvidenceDirectory, "baseline.json");
        string baselineMetadata = Path.Combine(EvidenceDirectory, "baseline-metadata.json");
        await RunToolAsync(
            "baseline-merge",
            "",
            ["merge", "--fragments", FixtureDirectory, "--data-standard-version", "5.2", "--output", baseline]
        );
        await RunToolAsync(
            "baseline-resolve",
            "",
            ["resolve", "--input", baseline, "--output", baselineMetadata]
        );
        var initialResponse = await _context.GetAsync(
            "/v3/authorizationMetadata",
            new() { Headers = headers }
        );
        initialResponse.Status.Should().Be(200);
        string initialMetadata = await initialResponse.TextAsync();
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDirectory, "api-metadata-before.json"),
            initialMetadata
        );
        var initialPermissions = ProjectPermissions(initialMetadata);
        string[] expectedCatalog = JsonSerializer.Deserialize<string[]>(
            await File.ReadAllTextAsync(Path.Combine(HybridReferenceDirectory, "expected-catalog.json"))
        )!;
        initialPermissions
            .Select(set => set.Key)
            .Should()
            .Equal(expectedCatalog.Where(name => name is not "HybridEmpty" and not "HybridPolicy"));
        initialPermissions
            .ToJsonString()
            .Should()
            .Be(ProjectPermissions(await File.ReadAllTextAsync(baselineMetadata)).ToJsonString());

        string merged = Path.Combine(EvidenceDirectory, "merged.json");
        await RunToolAsync(
            "merge",
            "",
            ["merge", "--fragments", fragments, "--data-standard-version", "5.2", "--output", merged]
        );
        byte[] generatedBytes = await File.ReadAllBytesAsync(merged);
        // The exact generated byte array is the request body; no string or JSON round trip.
        var upload = await CmsTestRequests.PostVerbatimBodyAsync(
            _context,
            "/management/upload-claims",
            token,
            generatedBytes
        );
        upload.Status.Should().Be(200);
        using var uploadResult = JsonDocument.Parse(await upload.TextAsync());
        uploadResult.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        (await File.ReadAllBytesAsync(merged)).Should().Equal(generatedBytes);
        await File.WriteAllBytesAsync(Path.Combine(EvidenceDirectory, "uploaded-body.json"), generatedBytes);
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDirectory, "uploaded-sha256.txt"),
            Convert.ToHexString(SHA256.HashData(generatedBytes))
        );

        await AssertMergedPermissionsAsync(merged, "5.2", unrestrictedLoading: true);

        (await File.ReadAllBytesAsync(merged)).Should().Equal(generatedBytes);
        HashFiles(fragments).Should().BeEquivalentTo(sourceHashes);
        Directory.GetFileSystemEntries(FixtureDirectory).Should().BeEmpty();
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDirectory, "fixture-sha256.json"),
            JsonSerializer.Serialize(sourceHashes)
        );
    }

    private static string HybridReferenceDirectory =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "Claims", "SecurityMetadataTools", "Hybrid");

    private async Task AssertMergedPermissionsAsync(
        string merged,
        string version,
        bool unrestrictedLoading = false
    )
    {
        byte[] mergedBytes = await File.ReadAllBytesAsync(merged);
        string referenceDirectory = HybridReferenceDirectory;
        string resolved = Path.Combine(EvidenceDirectory, "offline-metadata.json");
        await RunToolAsync("resolve", "", ["resolve", "--input", merged, "--output", resolved]);
        string offline = await File.ReadAllTextAsync(resolved);
        AssertReviewedPermissions(offline, referenceDirectory, version);

        var anonymous = await _context.GetAsync("/v3/authorizationMetadata");
        anonymous.Status.Should().Be(401);
        string token = await GetAccessTokenAsync();
        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };
        var response = await _context.GetAsync("/v3/authorizationMetadata", new() { Headers = headers });
        response.Status.Should().Be(200);
        string api = await response.TextAsync();
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, "api-metadata.json"), api);
        AssertReviewedPermissions(api, referenceDirectory, version);
        // Independent dereferencing compares the complete catalog, ignoring only generated IDs and
        // unordered containers. Strategy sequences remain positional; no production resolver is used.
        ProjectPermissions(api).ToJsonString().Should().Be(ProjectPermissions(offline).ToJsonString());
        var management = await _context.GetAsync("/management/current-claims", new() { Headers = headers });
        management.Status.Should().Be(unrestrictedLoading ? 200 : 404);

        await RunToolAsync(
            "verify",
            token,
            [
                "verify",
                "--input",
                merged,
                "--cms-url",
                RequiredSetting("CONFIGURATION_SERVICE_E2E_API_URL"),
                "--access-token-env",
                "CMS_POLICY_TEST_TOKEN",
            ]
        );
        (await File.ReadAllBytesAsync(merged)).Should().Equal(mergedBytes);
    }

    private async Task<string> GetAccessTokenAsync()
    {
        var tokenResponse = await CmsTestRequests.GetClientAccessTokenAsync(
            _context,
            "DmsConfigurationService",
            "ValidClientSecret1234567890!Abcd",
            "edfi_admin_api/full_access"
        );
        tokenResponse.Status.Should().Be(200, "the isolated self-contained client must authenticate");
        using var tokenDocument = JsonDocument.Parse(await tokenResponse.TextAsync());
        string token = tokenDocument.RootElement.GetProperty("access_token").GetString()!;
        // Assert presence without including the credential in failure diagnostics.
        string.IsNullOrWhiteSpace(token).Should().BeFalse();
        return token;
    }

    private async Task AssertContainerAsync(string source, string version, bool unrestrictedLoading = false)
    {
        // Assert and retain only these nonsecret configuration values.
        string[] keys =
        [
            "ClaimsOptions__ClaimsSource",
            "ClaimsOptions__DataStandardVersion",
            "ClaimsOptions__ClaimsDirectory",
            "ClaimsOptions__DangerouslyEnableUnrestrictedClaimsLoading",
        ];
        // Capture JSON in memory and retain only allowlisted settings, never credentials.
        var inspect = await RunProcessAsync(
            "docker",
            [
                "inspect",
                RequiredSetting("CONFIGURATION_SERVICE_CONTAINER"),
                "--format",
                "{{json .Config.Env}}",
            ],
            ""
        );
        inspect.ExitCode.Should().Be(0, "the selected isolated container must be running");
        string[] environment = JsonSerializer.Deserialize<string[]>(inspect.Output)!;
        var settings = keys.ToDictionary(
            key => key,
            key =>
                environment.Single(value => value.StartsWith(key + "=", StringComparison.Ordinal))[
                    (key.Length + 1)..
                ]
        );
        settings
            .Should()
            .BeEquivalentTo(
                new Dictionary<string, string>
                {
                    [keys[0]] = source,
                    [keys[1]] = version,
                    [keys[2]] = "/app/additional-claims",
                    [keys[3]] = unrestrictedLoading ? "true" : "false",
                }
            );
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDirectory, "container-policy-settings.json"),
            JsonSerializer.Serialize(settings)
        );
    }

    private static void AssertReviewedPermissions(string json, string referenceDirectory, string version)
    {
        var actual = ProjectPermissions(json);
        string[] catalog = JsonSerializer.Deserialize<string[]>(
            File.ReadAllText(Path.Combine(referenceDirectory, "expected-catalog.json"))
        )!;
        actual.Select(set => set.Key).Should().Equal(catalog);
        var expected = ProjectPermissions(
            File.ReadAllText(Path.Combine(referenceDirectory, $"expected-metadata-{version}.json"))
        );
        foreach (string name in new[] { "HybridPolicy", "HybridEmpty" })
        {
            actual[name]!
                .ToJsonString()
                .Should()
                .Be(expected[name]!.ToJsonString(), $"reviewed {name} permissions");
        }
        var baseline = expected["EdFiSandbox"]!.AsObject().Single();
        actual["EdFiSandbox"]![baseline.Key]!.ToJsonString().Should().Be(baseline.Value!.ToJsonString());
        string otherVersionClaim =
            "http://ed-fi.org/identity/claims/"
            + (version == "5.2" ? "ed-fi" : "tpdm")
            + "/accreditationStatusDescriptor";
        actual["EdFiSandbox"]!.AsObject().ContainsKey(otherVersionClaim).Should().BeFalse();
        // Empty catalog entries must also have no unreferenced authorization records.
        var empty = JsonNode
            .Parse(json)!
            .AsArray()
            .Single(set => set!["claimSetName"]!.GetValue<string>() == "HybridEmpty")!;
        empty["authorizations"]!.AsArray().Should().BeEmpty();
    }

    private static JsonObject ProjectPermissions(string json)
    {
        JsonObject result = new();
        foreach (
            var set in JsonNode
                .Parse(json)!
                .AsArray()
                .OrderBy(set => set!["claimSetName"]!.GetValue<string>(), StringComparer.Ordinal)
        )
        {
            JsonObject claims = new();
            var authorizations = set!["authorizations"]!
                .AsArray()
                .ToDictionary(a => a!["id"]!.GetValue<int>());
            foreach (
                var claim in set["claims"]!
                    .AsArray()
                    .OrderBy(c => c!["name"]!.GetValue<string>(), StringComparer.Ordinal)
            )
            {
                JsonObject actions = new();
                foreach (
                    var action in authorizations[claim!["authorizationId"]!.GetValue<int>()]!["actions"]!
                        .AsArray()
                        .OrderBy(a => a!["name"]!.GetValue<string>(), StringComparer.Ordinal)
                )
                {
                    actions.Add(
                        action!["name"]!.GetValue<string>(),
                        action["authorizationStrategies"]!.DeepClone()
                    );
                }
                claims.Add(claim!["name"]!.GetValue<string>(), actions);
            }
            result.Add(set["claimSetName"]!.GetValue<string>(), claims);
        }
        return result;
    }

    private async Task RunToolAsync(string operation, string token, string[] arguments)
    {
        var result = await RunProcessAsync("dotnet", [ToolPath, .. arguments], token);
        bool leakedToken =
            token.Length > 0
            && (
                result.Output.Contains(token, StringComparison.Ordinal)
                || result.Error.Contains(token, StringComparison.Ordinal)
            );
        string output =
            token.Length > 0
                ? result.Output.Replace(token, "[REDACTED]", StringComparison.Ordinal)
                : result.Output;
        string error =
            token.Length > 0
                ? result.Error.Replace(token, "[REDACTED]", StringComparison.Ordinal)
                : result.Error;
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, $"{operation}-stdout.txt"), output);
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, $"{operation}-stderr.txt"), error);
        leakedToken.Should().BeFalse("credentials must never reach tool diagnostics");
        result.ExitCode.Should().Be(0, error);
        error.Should().BeEmpty();
        output
            .Should()
            .Be(
                operation == "verify"
                    ? "CMS authorization metadata matches the expected effective permissions."
                        + Environment.NewLine
                    : ""
            );
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string executable,
        string[] arguments,
        string token
    )
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (token.Length > 0)
        {
            process.StartInfo.Environment["CMS_POLICY_TEST_TOKEN"] = token;
        }
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    private static Dictionary<string, string> HashFiles(string directory) =>
        Directory
            .GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(directory, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            );

    private static string RequiredSetting(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The isolated service runner must set {name}.");
}
