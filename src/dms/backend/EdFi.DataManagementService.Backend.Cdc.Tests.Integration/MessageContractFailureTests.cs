// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, Category = "MssqlIntegration")]
[Category("DatabaseIntegration")]
[Category("CdcMessageContract")]
[Category("CdcMessageContractSerialized")]
public sealed class Given_MessageContractFailure(CdcProvider provider)
{
    private static readonly string[] Sentinels =
    [
        "MC06-BODY-SENTINEL",
        "MC06-SECRET-SENTINEL",
        "MC06-TENANT-SENTINEL",
        "MC06-CONNECTION-SENTINEL",
        "MC06-CREDENTIAL-SENTINEL",
    ];

    private static readonly IReadOnlyDictionary<string, JsonElement> Cases = LoadCases();
    private IReadOnlyDictionary<string, MessageContractRunnerObservation> _observations = null!;
    private IReadOnlyDictionary<string, MessageContractRunnerScenario> _scenarios = null!;
    private MessageContractFixture _baseline = null!;
    private string _evidencePath = null!;

    private string Prefix => provider == CdcProvider.Postgresql ? "PG" : "SQL";

    private string ScenarioId(string name) => $"MC-FAILURE-{Prefix}-{name}";

    public static IEnumerable<TestCaseData> FailureCases() =>
        Cases
            .Where(c => c.Value.GetProperty("expected").GetString() == "failed")
            .Select(c => new TestCaseData(c.Key));

    public static IEnumerable<TestCaseData> ConverterCases() =>
        Cases
            .Where(c => c.Value.GetProperty("expected").GetString() != "failed")
            .Select(c => new TestCaseData(c.Key));

    [OneTimeSetUp]
    public async Task Setup()
    {
        string providerName = provider == CdcProvider.Postgresql ? "postgresql" : "sqlserver";
        var fixtures = MessageContractFixtureCatalog.LoadAll(AppContext.BaseDirectory);
        _baseline = fixtures.Single(f =>
            f.ScenarioId == $"MC-FIX-{Prefix}-ORDINARY-LINK-BEARING-STUDENT-SCHOOL-ASSOCIATION"
        );
        var delete = fixtures.Single(f => f.ScenarioId == $"MC-FIX-{Prefix}-DOCUMENT-DELETE");
        Dictionary<string, string> config = new()
        {
            ["provider"] = providerName,
            ["target.topic"] = "edfi.documents",
            ["progress.topic"] = "edfi.documents.cdc-progress",
        };
        List<MessageContractRunnerScenario> scenarios =
        [
            new(ScenarioId("BASELINE"), _baseline.SourceRecord, config, 7),
        ];
        foreach ((string name, JsonElement entry) in Cases)
        {
            JsonNode input = JsonNode.Parse(
                (
                    entry.GetProperty("base").GetString() == "delete" ? delete : _baseline
                ).SourceRecord.GetRawText()
            )!;
            JsonNode configuration = JsonSerializer.SerializeToNode(config)!;
            JsonNode document = JsonNode.Parse(
                _baseline
                    .SourceRecord.GetProperty("value")
                    .GetProperty("after")
                    .GetProperty("DocumentJson")
                    .GetString()!
            )!;
            // Secret/body/tenant/connection sentinels are synthetic and outside the allowed
            // artifact diagnostic identities. Oversized allowed identities are separate cases.
            document["diagnosticProbe"] = string.Join('|', Sentinels);
            input["value"]!["source"]!["db"] = Sentinels[2];
            input["value"]!["source"]!["name"] = $"Server={Sentinels[3]};Password={Sentinels[4]}";
            input["sourcePartition"]!["diagnosticProbe"] = $"Server={Sentinels[3]};Password={Sentinels[4]}";
            input["diagnosticSentinels"] = JsonSerializer.SerializeToNode(Sentinels);
            foreach (
                JsonElement mutation in entry
                    .GetProperty("patches")
                    .EnumerateArray()
                    .Where(p => p.GetProperty("target").GetString() == "document")
            )
            {
                Apply(document, mutation);
            }
            if (entry.GetProperty("base").GetString() == "upsert")
            {
                input["value"]!["after"]!["DocumentJson"] = document.ToJsonString();
            }
            foreach (
                JsonElement mutation in entry
                    .GetProperty("patches")
                    .EnumerateArray()
                    .Where(p => p.GetProperty("target").GetString() != "document")
            )
            {
                Apply(
                    mutation.GetProperty("target").GetString() == "config" ? configuration : input,
                    mutation
                );
            }
            // These deliberately invalid records bypass the positive fixture validator.
            // Their construction must still reach configuration/transform/conversion, never fail in input.
            scenarios.Add(
                new(
                    ScenarioId(name),
                    JsonSerializer.SerializeToElement(input),
                    configuration.Deserialize<Dictionary<string, string>>()!,
                    7,
                    ConverterOnly: entry.GetProperty("mode").GetString() == "converter"
                )
            );
        }
        _scenarios = scenarios.ToDictionary(s => s.ScenarioId, StringComparer.Ordinal);
        try
        {
            MessageContractRunnerResult result = await MessageContractRunner
                .FromEnvironment()
                .RunAsync(scenarios);
            _observations = result.Observations.ToDictionary(o => o.ScenarioId, StringComparer.Ordinal);
            // Persist only failed observations: no source records or successfully converted bodies.
            string evidenceDirectory = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "TestResults",
                "MessageContractFailure"
            );
            Directory.CreateDirectory(evidenceDirectory);
            string path = Path.Combine(
                evidenceDirectory,
                $"cdc-message-contract-{Prefix}-{Guid.NewGuid():N}.json"
            );
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(
                    new
                    {
                        result.ConnectImage,
                        Failures = result
                            .Observations.Where(o => o.Status == "failed")
                            .Select(o => o.Data)
                            .ToArray(),
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );
            _evidencePath = path;
        }
        catch (MessageContractRunnerPrerequisiteException failure)
        {
            CdcConnectorTemplateSmokeSettings
                .FromEnvironment(provider)
                .StopOnPrerequisiteFailure(provider, failure.Message);
        }
    }

    [Test]
    public void It_accepts_the_unmodified_shared_baseline()
    {
        // VSTest retains test-level attachments; fixture-level NUnit attachments are omitted from TRX.
        TestContext.AddTestAttachment(
            _evidencePath,
            $"{Prefix} stable failure reasons and in-container diagnostic audits"
        );
        var observation = _observations[ScenarioId("BASELINE")];
        observation.Status.Should().Be("retained", observation.ToString());
        using var document = JsonDocument.Parse(
            observation
                .Data.GetProperty("record")
                .GetProperty("valueBytes")
                .GetProperty("base64")
                .GetBytesFromBase64()
        );
        MessageContractJson.ShouldEqual(document.RootElement, _baseline.ExpectedEnvelope);
    }

    [TestCaseSource(nameof(FailureCases))]
    public void It_fails_at_the_owned_boundary_with_stable_reasons_and_bounded_diagnostics(string name)
    {
        JsonElement entry = Cases[name];
        var observation = _observations[ScenarioId(name)];
        observation.Status.Should().Be("failed", observation.ToString());
        observation.Data.TryGetProperty("record", out _).Should().BeFalse();
        JsonElement failure = observation.Data.GetProperty("failure");
        string mode = entry.GetProperty("mode").GetString()!;
        failure.GetProperty("stage").GetString().Should().Be(mode == "converter" ? "conversion" : mode);
        failure
            .GetProperty("category")
            .GetString()
            .Should()
            .Be(mode == "configuration" ? "ConfigException" : "DataException");
        failure.GetProperty("reason").GetString().Should().Be(entry.GetProperty("reason").GetString());
        JsonElement metadata = failure.GetProperty("metadata");
        metadata.EnumerateObject().Count().Should().BeLessThanOrEqualTo(5);
        foreach (JsonProperty property in metadata.EnumerateObject())
        {
            new[] { "provider", "operation", "sourceTopic", "sourceSchema", "sourceTable" }
                .Should()
                .Contain(property.Name);
            string value = property.Value.GetString()!;
            bool allowed = property.Name switch
            {
                "provider" => value is "postgresql" or "sqlserver" or "[redacted]",
                "operation" => value is "c" or "u" or "r" or "d" or "t" or "[redacted]",
                _ => value == "[redacted]",
            };
            allowed.Should().BeTrue("runner metadata must use only bounded allowed values");
        }
        JsonElement audit = failure.GetProperty("audit");
        audit.GetProperty("messageSentinelFree").GetBoolean().Should().BeTrue();
        audit.GetProperty("metadataSentinelFree").GetBoolean().Should().BeTrue();
        audit.GetProperty("messageLength").GetInt32().Should().BeLessThanOrEqualTo(1200);
        audit.GetProperty("metadataCount").GetInt32().Should().BeLessThanOrEqualTo(7);
        audit.GetProperty("metadataMaxLength").GetInt32().Should().BeLessThanOrEqualTo(128);
        audit.GetProperty("metadataControlFree").GetBoolean().Should().BeTrue();
        audit.GetProperty("hasCause").GetBoolean().Should().BeFalse();
        if (mode == "transform")
        {
            audit.GetProperty("metadataCount").GetInt32().Should().BeGreaterThan(0);
            metadata.EnumerateObject().Count().Should().BeGreaterThan(0);
        }
        if (name is "DIAGNOSTIC-TOPIC" or "DIAGNOSTIC-SCHEMA" or "DIAGNOSTIC-TABLE" or "DIAGNOSTIC-OP")
        {
            audit.GetProperty("metadataMaxLength").GetInt32().Should().Be(128);
        }
        string captured = observation.Data.GetRawText();
        captured.Length.Should().BeLessThanOrEqualTo(1600);
        ContainsSentinel(captured).Should().BeFalse("captured runner output must omit all synthetic secrets");
        ContainsSentinel(observation.ToString()).Should().BeFalse();
        ContainsSentinel(_scenarios[ScenarioId(name)].ToString()).Should().BeFalse();
    }

    [TestCaseSource(nameof(ConverterCases))]
    public void It_distinguishes_exact_public_bytes_from_delegation_and_kafka_null(string name)
    {
        JsonElement entry = Cases[name];
        var observation = _observations[ScenarioId(name)];
        observation.Status.Should().Be("retained", observation.ToString());
        JsonElement record = observation.Data.GetProperty("record");
        JsonElement bytes = record.GetProperty("valueBytes");
        string expected = entry.GetProperty("expected").GetString()!;
        if (expected == "null")
        {
            MessageContractJson.ShouldEqual(
                bytes,
                JsonSerializer.SerializeToElement(new { kind = "kafka-null" })
            );
            return;
        }
        bytes.GetProperty("kind").GetString().Should().Be("bytes");
        byte[] actual = bytes.GetProperty("base64").GetBytesFromBase64();
        actual.Length.Should().Be(bytes.GetProperty("length").GetInt32());
        if (expected == "bytes")
        {
            actual
                .SequenceEqual(
                    _scenarios[ScenarioId(name)].SourceRecord.GetProperty("value").GetBytesFromBase64()
                )
                .Should()
                .BeTrue();
            record.GetProperty("converterBytesEqual").GetBoolean().Should().BeTrue();
            record.GetProperty("converterDefensiveCopy").GetBoolean().Should().BeTrue();
        }
        else
        {
            using var decoded = JsonDocument.Parse(actual);
            MessageContractJson.ShouldEqual(decoded.RootElement, entry.GetProperty("expectedJson"));
            if (record.TryGetProperty("converterBytesEqual", out JsonElement equal))
            {
                equal
                    .GetBoolean()
                    .Should()
                    .BeFalse("other byte schemas must serialize through JsonConverter");
            }
        }
    }

    [Test]
    public void It_omits_bodies_from_an_intentional_assertion_failure()
    {
        JsonElement secret = JsonSerializer.SerializeToElement(new { body = string.Join('|', Sentinels) });
        JsonElement different = JsonSerializer.SerializeToElement(new { body = "different" });
        // Exercise the actual assertion boundary, retaining only its diagnostic string.
        Exception failure = Assert.Throws<AssertionException>(() =>
            MessageContractJson.ShouldEqual(secret, different)
        )!;
        ContainsSentinel(failure.ToString()).Should().BeFalse();
        failure.Message.Length.Should().BeLessThan(1000);
    }

    private static bool ContainsSentinel(string value) =>
        Array.Exists(Sentinels, s => value.Contains(s, StringComparison.Ordinal));

    private static IReadOnlyDictionary<string, JsonElement> LoadCases()
    {
        string root = MessageContractFixtureCatalog.ResolveFixtureRoot(AppContext.BaseDirectory);
        using var catalog = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "cdc/message-contract/failure-catalog.json"))
        );
        if (catalog.RootElement.GetProperty("formatVersion").GetInt32() != 1)
        {
            throw new InvalidOperationException("Unsupported failure catalog version.");
        }
        return catalog
            .RootElement.GetProperty("scenarios")
            .EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.Clone(), StringComparer.Ordinal);
    }

    private static void Apply(JsonNode target, JsonElement mutation)
    {
        string[] path = mutation.GetProperty("path").EnumerateArray().Select(p => p.GetString()!).ToArray();
        foreach (string segment in path[..^1])
        {
            target =
                target[segment] ?? throw new InvalidOperationException("Missing failure mutation parent.");
        }
        if (mutation.TryGetProperty("remove", out JsonElement remove) && remove.GetBoolean())
        {
            target.AsObject().Remove(path[^1]).Should().BeTrue("removed fixture field must exist");
        }
        else
        {
            target[path[^1]] = JsonNode.Parse(mutation.GetProperty("value").GetRawText());
        }
    }
}
