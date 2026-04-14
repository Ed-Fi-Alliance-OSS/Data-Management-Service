// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Enumerates all DS 5.2 resources with multi-hop Person authorization join paths
/// and produces a JSON report for the DMS-1064 ticket.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_MultiHop_Person_Auth_Path_Enumeration(SqlDialect dialect)
{
    private const string ProjectFileName = "EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj";

    private string GoldenFileName { get; } =
        $"multi-hop-person-auth-paths-{dialect.ToString().ToLowerInvariant()}.json";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly SecurableElementKind[] _personKinds =
    [
        SecurableElementKind.Student,
        SecurableElementKind.Contact,
        SecurableElementKind.Staff,
    ];

    private DerivedRelationalModelSet _modelSet = null!;
    private MappingSet _mappingSet = null!;
    private List<MultiHopEntry> _multiHopEntries = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        (_modelSet, _mappingSet) = Ds52FixtureHelper.BuildAndCompile(dialect);
        _multiHopEntries = BuildMultiHopEntries();
    }

    private List<MultiHopEntry> BuildMultiHopEntries()
    {
        var entries = new List<MultiHopEntry>();

        foreach (var concreteResource in _modelSet.ConcreteResourcesInNameOrder)
        {
            var resource = concreteResource.RelationalModel.Resource;

            if (
                !_mappingSet.SecurableElementColumnPathsByResource.TryGetValue(
                    resource,
                    out var resolvedPaths
                )
            )
            {
                continue;
            }

            foreach (var resolvedPath in resolvedPaths)
            {
                if (!_personKinds.Contains(resolvedPath.Kind))
                {
                    continue;
                }

                if (resolvedPath.Steps.Count <= 1)
                {
                    continue;
                }

                var jsonPaths = resolvedPath.Kind switch
                {
                    SecurableElementKind.Student => concreteResource.SecurableElements.Student,
                    SecurableElementKind.Contact => concreteResource.SecurableElements.Contact,
                    SecurableElementKind.Staff => concreteResource.SecurableElements.Staff,
                    _ => [],
                };

                var joinSteps = resolvedPath
                    .Steps.Select(step =>
                    {
                        var targetTable =
                            step.TargetTable
                            ?? throw new InvalidOperationException(
                                $"Multi-hop step for {resource.ResourceName} has null TargetTable"
                            );
                        var targetColumn =
                            step.TargetColumnName
                            ?? throw new InvalidOperationException(
                                $"Multi-hop step for {resource.ResourceName} has null TargetColumnName"
                            );

                        return new JoinStep(
                            SourceTable: step.SourceTable.ToString(),
                            SourceColumn: step.SourceColumnName.Value,
                            TargetTable: targetTable.ToString(),
                            TargetColumn: targetColumn.Value
                        );
                    })
                    .ToArray();

                foreach (var jsonPath in jsonPaths)
                {
                    entries.Add(
                        new MultiHopEntry(
                            ResourceName: resource.ResourceName,
                            PersonType: resolvedPath.Kind.ToString(),
                            SecurableElementJsonPath: jsonPath,
                            JoinPath: joinSteps,
                            HopCount: resolvedPath.Steps.Count
                        )
                    );
                }
            }
        }

        entries.Sort(
            (a, b) =>
            {
                int cmp = b.HopCount.CompareTo(a.HopCount);
                return cmp != 0
                    ? cmp
                    : string.Compare(a.ResourceName, b.ResourceName, StringComparison.Ordinal);
            }
        );

        return entries;
    }

    [Test]
    public void It_should_match_golden_report_json()
    {
        var actualJson = JsonSerializer.Serialize(_multiHopEntries, _jsonOptions);

        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            ProjectFileName
        );
        var expectedPath = Path.Combine(
            projectRoot,
            "Fixtures",
            "authoritative",
            "ds-5.2",
            "expected",
            GoldenFileName
        );

        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "authoritative",
            "ds-5.2",
            "actual",
            GoldenFileName
        );
        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, actualJson);

        if (GoldenFixtureTestHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, actualJson);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue($"golden file missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate.");

        var diffOutput = GoldenFixtureTestHelpers.RunGitDiff(expectedPath, actualPath);

        diffOutput
            .Should()
            .BeEmpty(
                "the multi-hop person auth path report must match the golden file. "
                    + "If the change is intentional, set UPDATE_GOLDENS=1 and re-run."
            );
    }

    /// <summary>
    /// Validates the resolver's BFS shortest-path contract: each person kind should
    /// produce exactly one resolved column path per resource. A failure here in a
    /// future schema version is an expected early warning that the resolver's
    /// one-path-per-kind assumption no longer holds, not necessarily a bug.
    /// </summary>
    [Test]
    public void It_should_have_at_most_one_resolved_path_per_person_kind_per_resource()
    {
        foreach (var (resource, resolvedPaths) in _mappingSet.SecurableElementColumnPathsByResource)
        {
            var personPathsByKind = resolvedPaths
                .Where(p => _personKinds.Contains(p.Kind))
                .GroupBy(p => p.Kind);

            foreach (var group in personPathsByKind)
            {
                group
                    .Should()
                    .HaveCount(
                        1,
                        $"resource '{resource.ResourceName}' should have exactly one resolved "
                            + $"path for {group.Key}, but found {group.Count()}"
                    );
            }
        }
    }

    [Test]
    public void It_should_resolve_every_person_kind_present_in_securable_elements()
    {
        foreach (var concreteResource in _modelSet.ConcreteResourcesInNameOrder)
        {
            var resource = concreteResource.RelationalModel.Resource;
            var elements = concreteResource.SecurableElements;

            if (
                !_mappingSet.SecurableElementColumnPathsByResource.TryGetValue(
                    resource,
                    out var resolvedPaths
                )
            )
            {
                continue;
            }

            var resolvedPersonKinds = resolvedPaths
                .Where(p => _personKinds.Contains(p.Kind))
                .Select(p => p.Kind)
                .ToHashSet();

            var expectedPersonKinds = new List<SecurableElementKind>();
            if (elements.Student.Count > 0)
            {
                expectedPersonKinds.Add(SecurableElementKind.Student);
            }
            if (elements.Contact.Count > 0)
            {
                expectedPersonKinds.Add(SecurableElementKind.Contact);
            }
            if (elements.Staff.Count > 0)
            {
                expectedPersonKinds.Add(SecurableElementKind.Staff);
            }

            foreach (var expectedKind in expectedPersonKinds)
            {
                resolvedPersonKinds
                    .Should()
                    .Contain(
                        expectedKind,
                        $"resource '{resource.ResourceName}' has {expectedKind} securable elements "
                            + "but no resolved column path for that kind"
                    );
            }
        }
    }

    [Test]
    public void It_should_process_all_concrete_resources_with_person_securable_elements()
    {
        // Person resources (Student, Contact, Staff) have securable elements but
        // don't produce mapping entries because they ARE the authorization anchor.
        var personResourceNames = new HashSet<string>(
            ["Student", "Contact", "Staff"],
            StringComparer.Ordinal
        );

        var resourcesWithPersonSecurableElements = _modelSet
            .ConcreteResourcesInNameOrder.Where(r =>
                r.SecurableElements.Student.Count > 0
                || r.SecurableElements.Contact.Count > 0
                || r.SecurableElements.Staff.Count > 0
            )
            .Select(r => r.RelationalModel.Resource)
            .Where(r => !personResourceNames.Contains(r.ResourceName))
            .ToHashSet();

        var resourcesInMapping = _mappingSet.SecurableElementColumnPathsByResource.Keys.ToHashSet();

        resourcesWithPersonSecurableElements
            .Should()
            .BeSubsetOf(
                resourcesInMapping,
                "every concrete resource with person securable elements (excluding "
                    + "person resources that are their own authorization anchor) should "
                    + "have a resolved entry in the mapping set"
            );
    }

    [Test]
    public void It_should_exclude_single_hop_paths()
    {
        _multiHopEntries.Should().OnlyContain(e => e.HopCount > 1);
    }

    [Test]
    public void It_should_only_include_Person_kinds()
    {
        var allowedTypes = new[] { "Student", "Contact", "Staff" };
        _multiHopEntries.Select(e => e.PersonType).Should().OnlyContain(t => allowedTypes.Contains(t));
    }

    [Test]
    public void It_should_sort_by_longest_path_first()
    {
        for (int i = 1; i < _multiHopEntries.Count; i++)
        {
            var prev = _multiHopEntries[i - 1];
            var curr = _multiHopEntries[i];

            if (prev.HopCount == curr.HopCount)
            {
                string.Compare(prev.ResourceName, curr.ResourceName, StringComparison.Ordinal)
                    .Should()
                    .BeLessThanOrEqualTo(
                        0,
                        $"entries with equal hop count should be sorted by resource name: "
                            + $"'{prev.ResourceName}' should come before or equal to '{curr.ResourceName}'"
                    );
            }
            else
            {
                prev.HopCount.Should()
                    .BeGreaterThan(
                        curr.HopCount,
                        $"entries should be sorted by descending hop count: "
                            + $"'{prev.ResourceName}' ({prev.HopCount}) should come before "
                            + $"'{curr.ResourceName}' ({curr.HopCount})"
                    );
            }
        }
    }

    private sealed record MultiHopEntry(
        string ResourceName,
        string PersonType,
        string SecurableElementJsonPath,
        JoinStep[] JoinPath,
        int HopCount
    );

    private sealed record JoinStep(
        string SourceTable,
        string SourceColumn,
        string TargetTable,
        string TargetColumn
    );
}
