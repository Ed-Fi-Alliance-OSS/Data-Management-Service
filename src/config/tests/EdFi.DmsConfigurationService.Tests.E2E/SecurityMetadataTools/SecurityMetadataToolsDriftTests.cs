// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E.SecurityMetadataTools;

public partial class Given_SecurityMetadataToolsService
{
    [Test]
    [Category("SecurityMetadataToolsDrift")]
    public async Task It_detects_a_precise_stored_policy_edit_from_an_editable_baseline()
    {
        await AssertContainerAsync("Filesystem", "5.2");
        string referenceDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "Claims",
            "SecurityMetadataTools",
            "Drift"
        );
        var sourceHashes = HashFiles(referenceDirectory);
        HashFiles(FixtureDirectory).Should().BeEquivalentTo(sourceHashes);
        string input = Path.Combine(FixtureDirectory, "Claims.json");
        byte[] inputBytes = await File.ReadAllBytesAsync(input);
        await File.WriteAllBytesAsync(Path.Combine(EvidenceDirectory, "input-before.json"), inputBytes);
        string token = await GetAccessTokenAsync();
        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };
        // Both E2E environments configure PathBase=config; exercise prefixed verification here.
        string[] verifyArguments =
        [
            "verify",
            "--input",
            input,
            "--cms-url",
            $"{RequiredSetting("CONFIGURATION_SERVICE_E2E_API_URL").TrimEnd('/')}/config",
            "--access-token-env",
            "CMS_POLICY_TEST_TOKEN",
        ];

        await AssertStoredMetadataAsync("before");
        await RunToolAsync("verify-before", token, verifyArguments);

        // Resolve both IDs from secured endpoints, without assuming seeded identifiers on either backend.
        var catalogResponse = await _context.GetAsync("/v3/claimSets", new() { Headers = headers });
        catalogResponse.Status.Should().Be(200);
        var catalog = JsonNode.Parse(await catalogResponse.TextAsync())!.AsArray();
        catalog.Should().ContainSingle();
        var editable = catalog.Single()!;
        editable["claimSetName"]!.GetValue<string>().Should().Be("DriftEditable");
        editable["_isSystemReserved"]!.GetValue<bool>().Should().BeFalse();
        int claimSetId = editable["id"]!.GetValue<int>();
        var resourcesResponse = await _context.GetAsync(
            "/v3/resourceClaimActions?resourceName=student",
            new() { Headers = headers }
        );
        resourcesResponse.Status.Should().Be(200);
        var resources = JsonNode.Parse(await resourcesResponse.TextAsync())!.AsArray();
        var student = resources.Single(resource =>
            resource!["claimName"]!.GetValue<string>() == "http://ed-fi.org/identity/claims/ed-fi/student"
        )!;
        int resourceClaimId = student["resourceClaimId"]!.GetValue<int>();

        // This supported edit preserves Read while enabling Create; no catalog or reserved flag changes.
        var mutation = await _context.PutAsync(
            $"/v3/claimSets/{claimSetId}/resourceClaimActions/{resourceClaimId}",
            new()
            {
                Headers = headers,
                DataObject = new
                {
                    claimSetId,
                    resourceClaimId,
                    resourceClaimActions = new[]
                    {
                        new { name = "Read", enabled = true },
                        new { name = "Create", enabled = true },
                    },
                },
            }
        );
        mutation.Status.Should().Be(204);
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, "mutation-status.txt"), "204");

        await AssertStoredMetadataAsync("after");
        string expectedReport =
            (await File.ReadAllTextAsync(Path.Combine(referenceDirectory, "expected-verify-after.txt")))
                .ReplaceLineEndings("\n")
                .TrimEnd('\n') + Environment.NewLine;
        await RunToolAsync("verify-after", token, verifyArguments, 2, expectedReport);
        await AssertContainerAsync("Filesystem", "5.2");

        (await File.ReadAllBytesAsync(input)).Should().Equal(inputBytes);
        HashFiles(FixtureDirectory).Should().BeEquivalentTo(sourceHashes);
        HashFiles(referenceDirectory).Should().BeEquivalentTo(sourceHashes);
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDirectory, "fixture-sha256-after.json"),
            JsonSerializer.Serialize(HashFiles(FixtureDirectory))
        );

        async Task AssertStoredMetadataAsync(string phase)
        {
            var management = await _context.GetAsync(
                "/management/current-claims",
                new() { Headers = headers }
            );
            management.Status.Should().Be(404);
            await File.WriteAllTextAsync(
                Path.Combine(EvidenceDirectory, $"management-{phase}-status.txt"),
                "404"
            );
            var response = await _context.GetAsync("/v3/authorizationMetadata", new() { Headers = headers });
            response.Status.Should().Be(200);
            string metadata = await response.TextAsync();
            await File.WriteAllTextAsync(
                Path.Combine(EvidenceDirectory, $"api-metadata-{phase}.json"),
                metadata
            );
            string expected = await File.ReadAllTextAsync(
                Path.Combine(referenceDirectory, $"expected-metadata-{phase}.json")
            );
            // Full equality includes the complete catalog and every grant, not only the edited action.
            ProjectPermissions(metadata)
                .ToJsonString()
                .Should()
                .Be(ProjectPermissions(expected).ToJsonString());
        }
    }
}
