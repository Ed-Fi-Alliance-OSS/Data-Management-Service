// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using NUnit.Framework.Api;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

// Linked into the integration assembly too: discovery executes case sources, never fixture setup.
internal static class MessageContractTraceability
{
    internal static readonly string[] AssignedInvariants =
    [
        "CDC-INV-02",
        "CDC-INV-06",
        "CDC-INV-07",
        "CDC-INV-08",
        "CDC-INV-09",
        "CDC-INV-10",
        "CDC-INV-13",
        "CDC-INV-14",
    ];

    internal sealed record Discovered(
        string Test,
        string Method,
        string[] Invariants,
        string[] Categories,
        string[] ScenarioIds,
        bool Runnable = true
    );

    internal static Discovered[] Discover(Assembly assembly)
    {
        ITest root = new DefaultTestAssemblyBuilder().Build(assembly, new Dictionary<string, object>());
        return Leaves(root)
            .Where(t =>
                (
                    t.FullName.Contains("MessageContract", StringComparison.Ordinal)
                    || Properties(t, "Category").Contains("CdcMessageContract")
                ) && !t.FullName.Contains("MessageContractTraceability", StringComparison.Ordinal)
            )
            .Select(t => new Discovered(
                t.FullName,
                $"{t.TypeInfo?.FullName}.{t.Method?.Name}",
                GetInvariants(t),
                Properties(t, "Category"),
                Properties(t, "ScenarioId"),
                t.RunState == RunState.Runnable
            ))
            .OrderBy(t => t.Test, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<ITest> Leaves(ITest test) =>
        test.IsSuite ? test.Tests.SelectMany(Leaves) : [test];

    // A method/case annotation replaces the fixture defaults; it does not broaden them.
    internal static string[] GetInvariants(ITest test)
    {
        for (ITest current = test; current is not null; current = current.Parent!)
        {
            string[] values = current.Properties["CdcInvariant"].Cast<string>().ToArray();
            if (values.Length > 0)
            {
                return values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            }
        }
        return [];
    }

    private static string[] Properties(ITest test, string key)
    {
        List<string> values = [];
        for (ITest current = test; current is not null; current = current.Parent!)
        {
            values.AddRange(current.Properties[key].Cast<object>().Select(v => v.ToString()!));
        }
        return values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal static string[] Validate(
        IReadOnlyList<Discovered> discovered,
        IReadOnlyCollection<string> required
    )
    {
        List<string> errors = [];
        if (discovered.Count == 0)
        {
            errors.Add("empty-coverage");
        }
        foreach (var test in discovered)
        {
            if (test.Invariants.Length == 0)
            {
                errors.Add($"missing-invariant: {test.Test}");
            }
            foreach (string invariant in test.Invariants.Except(AssignedInvariants))
            {
                errors.Add($"unassigned-invariant: {test.Test}: {invariant}");
            }
            if (!test.Runnable)
            {
                errors.Add($"non-runnable-case: {test.Test}");
            }
            if (!test.Categories.Contains("CdcMessageContract"))
            {
                errors.Add($"missing-category: {test.Test}");
            }
            bool serialized = test.Categories.Contains("CdcMessageContractSerialized");
            bool kafka = test.Categories.Contains("CdcMessageContractKafka");
            bool database = test.Categories.Contains("DatabaseIntegration");
            if ((serialized || kafka) && !database)
            {
                errors.Add($"missing-database-category: {test.Test}");
            }
            if (
                database
                && (
                    !(serialized ^ kafka)
                    || !(
                        test.Categories.Contains("PostgresqlIntegration")
                        ^ test.Categories.Contains("MssqlIntegration")
                    )
                )
            )
            {
                errors.Add($"missing-qualification-category: {test.Test}");
            }
        }
        foreach (string invariant in required.Except(discovered.SelectMany(t => t.Invariants)))
        {
            errors.Add($"uncovered-invariant: {invariant}");
        }
        return errors.ToArray();
    }
}

[TestFixture]
[Category("CdcMessageContract")]
public sealed class Given_MessageContractTraceability
{
    private MessageContractTraceability.Discovered[] _discovered = null!;
    private string _assembly = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        var assembly = typeof(Given_MessageContractTraceability).Assembly;
        _assembly = assembly.GetName().Name!;
        _discovered = MessageContractTraceability.Discover(assembly);
    }

    [Test]
    public void It_maps_discovered_cases_to_assigned_invariants_and_execution_lanes()
    {
        // The integration scenarios cover the full story; unit helpers cover a subset.
        string[] required = _assembly.EndsWith(".Integration", StringComparison.Ordinal)
            ? MessageContractTraceability.AssignedInvariants
            : [];
        MessageContractTraceability.Validate(_discovered, required).Should().BeEmpty();
    }

    [Test]
    public void It_packages_shared_fixtures_and_integration_runner_support()
    {
        MessageContractFixtureCatalog.LoadAll(AppContext.BaseDirectory).Should().NotBeEmpty();
        if (_assembly.EndsWith(".Integration", StringComparison.Ordinal))
        {
            foreach (
                string resource in new[]
                {
                    "MessageContractRunner.java",
                    "MessageContractSourceObserver.java",
                    "MessageContractProducerProxy.java",
                }
            )
            {
                File.Exists(Path.Combine(AppContext.BaseDirectory, "MessageContractRunner", resource))
                    .Should()
                    .BeTrue();
            }
            var assembly = typeof(Given_MessageContractTraceability).Assembly;
            assembly.GetType(typeof(MessageContractConsumer).FullName!).Should().NotBeNull();
            assembly.GetType(typeof(MessageContractConsumerBootstrap).FullName!).Should().NotBeNull();
        }
    }

    [OneTimeTearDown]
    public void ExportDiscoveryEvidence()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "TestResults",
            "MessageContractTraceability"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cdc-message-contract-" + _assembly + ".json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                _discovered.Select(d => new
                {
                    Test = d.Method,
                    d.Invariants,
                    d.Categories,
                    d.ScenarioIds,
                })
            )
        );
        TestContext.AddTestAttachment(path, "Discovered invariant annotations; test arguments omitted.");
    }
}

[TestFixture(false)]
[TestFixture(true)]
[Category("CdcMessageContract")]
public sealed class Given_MessageContractTraceability_annotations(bool methodOverride)
{
    private string[] _invariants = null!;

    [SetUp]
    public void Setup()
    {
        TestSuite fixture = new("fixture");
        fixture.Properties.Add("CdcInvariant", "CDC-INV-07");
        fixture.Properties.Add("CdcInvariant", "CDC-INV-08");
        TestSuite method = new("method");
        fixture.Add(method);
        TestSuite parameterizedCase = new("case");
        method.Add(parameterizedCase);
        if (methodOverride)
        {
            method.Properties.Add("CdcInvariant", "CDC-INV-10");
        }
        _invariants = MessageContractTraceability.GetInvariants(parameterizedCase);
    }

    [Test]
    public void It_inherits_fixture_defaults_unless_the_method_overrides_them()
    {
        string[] expected = methodOverride ? ["CDC-INV-10"] : ["CDC-INV-07", "CDC-INV-08"];
        _invariants.Should().Equal(expected);
    }
}

[TestFixture("valid")]
[TestFixture("missing-invariant")]
[TestFixture("unassigned-invariant")]
[TestFixture("uncovered-invariant")]
[TestFixture("missing-category")]
[TestFixture("missing-qualification-category")]
[TestFixture("empty-coverage")]
[TestFixture("non-runnable-case")]
[TestFixture("missing-database-category")]
[Category("CdcMessageContract")]
public sealed class Given_MessageContractTraceability_coverage(string fault)
{
    private string[] _errors = null!;

    [SetUp]
    public void Setup()
    {
        MessageContractTraceability.Discovered test = new(
            "fixture.case",
            "fixture.method",
            ["CDC-INV-07"],
            ["CdcMessageContract"],
            []
        );
        string[] required = ["CDC-INV-07"];
        test = fault switch
        {
            "missing-invariant" => test with { Invariants = [] },
            "unassigned-invariant" => test with { Invariants = ["CDC-INV-01"] },
            "missing-category" => test with { Categories = [] },
            "missing-qualification-category" => test with
            {
                Categories = ["CdcMessageContract", "DatabaseIntegration"],
            },
            "non-runnable-case" => test with { Runnable = false },
            "missing-database-category" => test with
            {
                Categories = ["CdcMessageContract", "CdcMessageContractKafka"],
            },
            _ => test,
        };
        if (fault == "uncovered-invariant")
        {
            required = ["CDC-INV-13"];
        }
        _errors = MessageContractTraceability.Validate(fault == "empty-coverage" ? [] : [test], required);
    }

    [Test]
    public void It_validates_annotation_and_execution_requirements()
    {
        if (fault == "valid")
        {
            _errors.Should().BeEmpty();
        }
        else
        {
            _errors.Should().Contain(error => error.StartsWith(fault, StringComparison.Ordinal));
        }
    }
}
