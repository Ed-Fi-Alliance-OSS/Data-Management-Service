// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Enumerates all DS 5.2 resources with multi-hop Person authorization join paths
/// and produces a JSON report for the DMS-1064 ticket.
/// </summary>
[TestFixture]
public class Given_MultiHop_Person_Auth_Path_Enumeration
{
    private const string Ds52FixturePath =
        "../Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json";

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
        _modelSet = RuntimePlanFixtureModelSetBuilder.Build(Ds52FixturePath, SqlDialect.Pgsql);
        var compiler = new MappingSetCompiler();
        _mappingSet = compiler.Compile(_modelSet);
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

                entries.Add(
                    new MultiHopEntry(
                        ResourceName: resource.ResourceName,
                        PersonType: resolvedPath.Kind.ToString(),
                        SecurableElementJsonPaths: jsonPaths.ToArray(),
                        JoinPath: resolvedPath
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
                            .ToArray(),
                        HopCount: resolvedPath.Steps.Count
                    )
                );
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
    public void It_should_generate_multi_hop_report_json()
    {
        _multiHopEntries.Should().NotBeEmpty("there should be at least one multi-hop Person path in DS 5.2");

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        var json = JsonSerializer.Serialize(_multiHopEntries, options);

        var outputPath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "multi-hop-person-auth-paths.json"
        );
        File.WriteAllText(outputPath, json);

        TestContext.Out.WriteLine($"Report written to: {outputPath}");
        TestContext.Out.WriteLine($"Total multi-hop entries: {_multiHopEntries.Count}");
        TestContext.Out.WriteLine();
        TestContext.Out.WriteLine(json);
    }

    [Test]
    public void It_should_find_CourseTranscript_Student_two_hop_path()
    {
        var entry = _multiHopEntries.Find(e =>
            e.ResourceName == "CourseTranscript" && e.PersonType == "Student"
        );

        entry.Should().NotBeNull("CourseTranscript should have a multi-hop Student path");

        entry!.HopCount.Should().Be(2);
        entry.SecurableElementJsonPaths.Should().Contain("$.studentAcademicRecordReference.studentUniqueId");

        entry.JoinPath.Should().HaveCount(2);

        entry.JoinPath[0].SourceTable.Should().Be("edfi.CourseTranscript");
        entry.JoinPath[0].SourceColumn.Should().Be("StudentAcademicRecord_DocumentId");
        entry.JoinPath[0].TargetTable.Should().Be("edfi.StudentAcademicRecord");
        entry.JoinPath[0].TargetColumn.Should().Be("DocumentId");

        entry.JoinPath[1].SourceTable.Should().Be("edfi.StudentAcademicRecord");
        entry.JoinPath[1].SourceColumn.Should().Be("Student_DocumentId");
        entry.JoinPath[1].TargetTable.Should().Be("edfi.Student");
        entry.JoinPath[1].TargetColumn.Should().Be("DocumentId");
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
        string[] SecurableElementJsonPaths,
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
