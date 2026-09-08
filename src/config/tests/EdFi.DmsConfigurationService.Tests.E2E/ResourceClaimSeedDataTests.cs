// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E;

[TestFixture]
public class Given_resource_claim_seed_data
{
    private IReadOnlyCollection<string> _missingSeedClaimNames = [];

    [SetUp]
    public void SetUp()
    {
        string repositoryRoot = FindRepositoryRoot();
        string authoritativeCompositionPath = Path.Combine(
            repositoryRoot,
            "src",
            "config",
            "tests",
            "EdFi.DmsConfigurationService.Tests.E2E",
            "TestData",
            "Claims",
            "authoritative-composition.json"
        );
        string resourceClaimSeedPath = Path.Combine(
            repositoryRoot,
            "src",
            "config",
            "backend",
            "EdFi.DmsConfigurationService.Backend.Postgresql",
            "Deploy",
            "Scripts",
            "0009_Insert_ResourceClaim.sql"
        );

        HashSet<string> hierarchyClaimNames = LoadHierarchyClaimNames(authoritativeCompositionPath);
        HashSet<string> seededClaimNames = LoadSeededClaimNames(resourceClaimSeedPath);

        _missingSeedClaimNames = hierarchyClaimNames
            .Where(claimName => !seededClaimNames.Contains(claimName))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    [Test]
    public void It_has_resource_claim_metadata_for_every_authoritative_claim()
    {
        _missingSeedClaimNames.Should().BeEmpty();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (
                File.Exists(Path.Combine(directory.FullName, "LICENSE"))
                && File.Exists(
                    Path.Combine(directory.FullName, "src", "config", "EdFi.DmsConfigurationService.sln")
                )
            )
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static HashSet<string> LoadHierarchyClaimNames(string path)
    {
        JsonNode root =
            JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Could not parse '{path}'.");
        JsonArray hierarchy = root switch
        {
            JsonArray hierarchyArray => hierarchyArray,
            JsonObject hierarchyDocument
                when hierarchyDocument["claimsHierarchy"] is JsonArray hierarchyArray => hierarchyArray,
            _ => throw new InvalidOperationException($"Could not find claimsHierarchy in '{path}'."),
        };

        HashSet<string> claimNames = new(StringComparer.Ordinal);
        AddClaimNames(hierarchy, claimNames);

        return claimNames;
    }

    private static void AddClaimNames(JsonArray claims, HashSet<string> claimNames)
    {
        foreach (JsonNode? claimNode in claims)
        {
            if (claimNode is null)
            {
                continue;
            }

            string? claimName = claimNode["name"]?.GetValue<string>();
            if (claimName is not null)
            {
                claimNames.Add(claimName);
            }

            if (claimNode["claims"] is JsonArray childClaims)
            {
                AddClaimNames(childClaims, claimNames);
            }
        }
    }

    private static HashSet<string> LoadSeededClaimNames(string path)
    {
        string seedSql = File.ReadAllText(path);
        MatchCollection matches = Regex.Matches(seedSql, @"\(\d+,'[^']+','(?<claimName>[^']+)'\)");

        return matches.Select(match => match.Groups["claimName"].Value).ToHashSet(StringComparer.Ordinal);
    }
}
