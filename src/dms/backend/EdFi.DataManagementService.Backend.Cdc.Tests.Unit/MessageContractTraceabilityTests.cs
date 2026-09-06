// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using NUnit.Framework.Api;
using NUnit.Framework.Interfaces;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

// Linked into the integration assembly too: discovery executes case sources, never fixture setup.
// This keeps the normal PR checks Docker-free without a dependency between test assemblies.
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
        string Id,
        string Test,
        string[] Categories,
        string[] ScenarioIds,
        bool Runnable = true
    );

    internal sealed record Mapping(string Id, string Test, string[] Invariants);

    internal sealed record Group(
        string Assembly,
        string Fixture,
        string[] Invariants,
        Dictionary<string, string> Tests
    );

    internal sealed record Contract(
        int FormatVersion,
        Dictionary<string, string[]> Invariants,
        string[] Exclusions,
        Group[] Groups
    );

    internal static Contract Read(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<Contract>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        )!;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidOperationException("duplicate-json-property");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    internal static string StableId(string fullName) =>
        "MC-TEST-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullName)))[..20];

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
                StableId(t.FullName),
                t.FullName,
                Properties(t, "Category"),
                Properties(t, "ScenarioId"),
                t.RunState == RunState.Runnable
            ))
            .OrderBy(t => t.Test, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<ITest> Leaves(ITest test) =>
        test.IsSuite ? test.Tests.SelectMany(Leaves) : [test];

    private static string[] Properties(ITest test, string key)
    {
        List<string> values = [];
        for (ITest current = test; current is not null; current = current.Parent!)
        {
            values.AddRange(current.Properties[key].Cast<object>().Select(v => v.ToString()!));
        }
        return values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal static Mapping[] Mappings(Contract contract, string assembly) =>
        contract
            .Groups.Where(g => g.Assembly == assembly)
            .SelectMany(g => g.Tests.Select(t => new Mapping(t.Key, g.Fixture + "." + t.Value, g.Invariants)))
            .ToArray();

    internal static string[] Validate(
        IReadOnlyList<Mapping> mappings,
        IReadOnlyList<Discovered> discovered,
        IReadOnlyCollection<string> required
    )
    {
        List<string> errors = [];
        if (mappings.Count == 0 || discovered.Count == 0)
        {
            errors.Add("empty-coverage");
        }
        if (mappings.Select(m => m.Id).Distinct().Count() != mappings.Count)
        {
            errors.Add("duplicate-id");
        }
        if (mappings.Select(m => m.Test).Distinct().Count() != mappings.Count)
        {
            errors.Add("duplicate-test");
        }
        if (discovered.Select(d => d.Id).Distinct().Count() != discovered.Count)
        {
            errors.Add("duplicate-discovered-id");
        }
        if (discovered.Any(d => !d.Runnable))
        {
            errors.Add("non-runnable-case");
        }
        if (
            discovered.Any(d =>
                (
                    d.Categories.Contains("CdcMessageContractSerialized")
                    || d.Categories.Contains("CdcMessageContractKafka")
                ) && !d.Categories.Contains("DatabaseIntegration")
            )
        )
        {
            errors.Add("missing-database-category");
        }
        if (mappings.Any(m => m.Id != StableId(m.Test)))
        {
            errors.Add("invalid-stable-id");
        }
        if (mappings.Any(m => m.Invariants.Length == 0))
        {
            errors.Add("missing-invariant");
        }
        if (mappings.SelectMany(m => m.Invariants).Except(AssignedInvariants).Any())
        {
            errors.Add("unassigned-invariant");
        }
        if (required.Except(mappings.SelectMany(m => m.Invariants)).Any())
        {
            errors.Add("uncovered-invariant");
        }
        if (discovered.Select(d => d.Id).Except(mappings.Select(m => m.Id)).Any())
        {
            errors.Add("missing-mapping");
        }
        if (mappings.Select(m => m.Id).Except(discovered.Select(d => d.Id)).Any())
        {
            errors.Add("stale-reference");
        }
        if (discovered.Any(d => !d.Categories.Contains("CdcMessageContract")))
        {
            errors.Add("missing-category");
        }
        if (
            discovered.Any(d =>
                d.Categories.Contains("DatabaseIntegration")
                && (
                    !(
                        d.Categories.Contains("CdcMessageContractSerialized")
                        ^ d.Categories.Contains("CdcMessageContractKafka")
                    )
                    || !(
                        d.Categories.Contains("PostgresqlIntegration")
                        ^ d.Categories.Contains("MssqlIntegration")
                    )
                )
            )
        )
        {
            errors.Add("missing-qualification-category");
        }
        return errors.ToArray();
    }
}

[TestFixture]
[Category("CdcMessageContract")]
public sealed class Given_MessageContractTraceability
{
    private MessageContractTraceability.Contract _contract = null!;
    private MessageContractTraceability.Discovered[] _discovered = null!;
    private string _assembly = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _assembly = typeof(Given_MessageContractTraceability).Assembly.GetName().Name!;
        _discovered = MessageContractTraceability.Discover(
            typeof(Given_MessageContractTraceability).Assembly
        );
        _contract = MessageContractTraceability.Read(
            File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Fixtures",
                    "cdc",
                    "message-contract",
                    "traceability.json"
                )
            )
        );
    }

    [Test]
    public void It_maps_every_discovered_parameterized_case_to_assigned_invariants()
    {
        MessageContractTraceability
            .Validate(MessageContractTraceability.Mappings(_contract, _assembly), _discovered, [])
            .Should()
            .BeEmpty(
                "the manifest must match executable NUnit discovery, including all provider and case variants"
            );
    }

    [Test]
    public void It_covers_the_story_boundary_without_duplicate_or_unassigned_identifiers()
    {
        _contract.FormatVersion.Should().Be(1);
        _contract.Invariants.Keys.Should().BeEquivalentTo(MessageContractTraceability.AssignedInvariants);
        _contract
            .Invariants.Values.Should()
            .OnlyContain(links =>
                links.Length > 0
                && Array.TrueForAll(
                    links,
                    link =>
                        link.StartsWith(
                            "reference/design/backend-redesign/design-docs/cdc/",
                            StringComparison.Ordinal
                        ) && link.Contains('#')
                )
            );
        _contract.Exclusions.Should().NotBeEmpty();
        _contract
            .Groups.Select(g => g.Assembly)
            .Distinct()
            .Should()
            .BeEquivalentTo(
                "EdFi.DataManagementService.Backend.Cdc.Tests.Unit",
                "EdFi.DataManagementService.Backend.Cdc.Tests.Integration"
            );
        var all = _contract
            .Groups.SelectMany(g =>
                MessageContractTraceability.Mappings(_contract with { Groups = [g] }, g.Assembly)
            )
            .ToArray();
        all.Select(m => m.Id).Should().OnlyHaveUniqueItems();
        all.Select(m => m.Test).Should().OnlyHaveUniqueItems();
        all.Should()
            .OnlyContain(m =>
                m.Id == MessageContractTraceability.StableId(m.Test) && m.Invariants.Length > 0
            );
        all.SelectMany(m => m.Invariants)
            .Distinct()
            .Should()
            .BeEquivalentTo(MessageContractTraceability.AssignedInvariants);
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

    [Test]
    public void It_retains_stable_ids_and_categories_as_discovery_evidence()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "TestResults",
            "MessageContractTraceability"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, _assembly + ".json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                _discovered.Select(d => new
                {
                    d.Id,
                    d.Categories,
                    d.ScenarioIds,
                })
            )
        );
        TestContext.AddTestAttachment(
            path,
            "Executable message-contract discovery; no record bodies or source identities."
        );
        _discovered.Should().NotBeEmpty();
    }
}

[TestFixture]
[Category("CdcMessageContract")]
public sealed class Given_MessageContractTraceability_invalid_coverage
{
    [TestCase("{\"groups\":[],\"groups\":[]}")]
    [TestCase("{\"groups\":[{\"tests\":{\"same\":\"first\",\"same\":\"second\"}}]}")]
    public void It_rejects_duplicate_json_properties_before_deserialization(string json)
    {
        Action read = () => MessageContractTraceability.Read(json);
        read.Should().Throw<InvalidOperationException>().WithMessage("duplicate-json-property");
    }

    [TestCase("missing-mapping")]
    [TestCase("stale-reference")]
    [TestCase("duplicate-id")]
    [TestCase("duplicate-test")]
    [TestCase("duplicate-discovered-id")]
    [TestCase("invalid-stable-id")]
    [TestCase("missing-invariant")]
    [TestCase("unassigned-invariant")]
    [TestCase("uncovered-invariant")]
    [TestCase("missing-category")]
    [TestCase("missing-qualification-category")]
    [TestCase("empty-coverage")]
    [TestCase("non-runnable-case")]
    [TestCase("missing-database-category")]
    public void It_rejects_broken_coverage(string fault)
    {
        string id = MessageContractTraceability.StableId("fixture.It_checks(\"PG\",1)");
        MessageContractTraceability.Mapping mapping = new(id, "fixture.It_checks(\"PG\",1)", ["CDC-INV-07"]);
        MessageContractTraceability.Discovered test = new(id, mapping.Test, ["CdcMessageContract"], []);
        List<MessageContractTraceability.Mapping> mappings = [mapping];
        List<MessageContractTraceability.Discovered> discovered = [test];
        string[] required = ["CDC-INV-07"];
        switch (fault)
        {
            case "missing-mapping":
                mappings.Clear();
                break;
            case "stale-reference":
                discovered.Clear();
                break;
            case "duplicate-id":
                mappings.Add(mapping with { Test = "other" });
                break;
            case "duplicate-test":
                mappings.Add(mapping with { Id = "other" });
                break;
            case "duplicate-discovered-id":
                discovered.Add(test);
                break;
            case "invalid-stable-id":
                mappings[0] = mapping with { Id = "other" };
                break;
            case "missing-invariant":
                mappings[0] = mapping with { Invariants = [] };
                break;
            case "unassigned-invariant":
                mappings[0] = mapping with { Invariants = ["CDC-INV-01"] };
                break;
            case "uncovered-invariant":
                required = ["CDC-INV-13"];
                break;
            case "missing-category":
                discovered[0] = test with { Categories = [] };
                break;
            case "missing-qualification-category":
                discovered[0] = test with { Categories = ["CdcMessageContract", "DatabaseIntegration"] };
                break;
            case "non-runnable-case":
                discovered[0] = test with { Runnable = false };
                break;
            case "missing-database-category":
                discovered[0] = test with { Categories = ["CdcMessageContract", "CdcMessageContractKafka"] };
                break;
            case "empty-coverage":
                mappings.Clear();
                discovered.Clear();
                break;
        }
        MessageContractTraceability.Validate(mappings, discovered, required).Should().Contain(fault);
    }
}
