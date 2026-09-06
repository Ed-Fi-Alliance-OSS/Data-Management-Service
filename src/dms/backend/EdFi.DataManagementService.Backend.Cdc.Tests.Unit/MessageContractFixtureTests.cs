// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Category("CdcMessageContract")]
public class Given_MessageContractFixtureCatalog
{
    private IReadOnlyList<MessageContractFixture> _fixtures = [];

    [SetUp]
    public void Setup() =>
        _fixtures = MessageContractFixtureCatalog.LoadAll(TestContext.CurrentContext.TestDirectory);

    [Test]
    public void It_loads_both_providers_for_each_required_shared_case()
    {
        string[] cases =
        [
            "ordinary-link-bearing-student-school-association",
            "descriptor-school-type",
            "extension-student-school-association",
            "school-address-property-absence",
        ];
        foreach (string name in cases)
        {
            _fixtures
                .Where(f =>
                    f.MaterializedCase.EndsWith('/' + name, StringComparison.Ordinal)
                    && f.SourceRecord.GetProperty("operation").GetString() == "c"
                )
                .Select(f => f.SourceRecord.GetProperty("provider").GetString())
                .Should()
                .BeEquivalentTo("postgresql", "sqlserver");
        }
        _fixtures.Select(f => f.ScenarioId).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void It_assembles_the_complete_envelope_from_shared_metadata_and_document_body()
    {
        MessageContractFixture fixture = _fixtures[0];
        JsonObject expected = JsonNode
            .Parse(
                """
                {
                  "contractVersion": 1,
                  "documentUuid": "aaaaaaaa-bbbb-cccc-dddd-000000000101",
                  "projectName": "Ed-Fi",
                  "resourceName": "StudentSchoolAssociation",
                  "resourceVersion": "1.0",
                  "contentVersion": 222,
                  "lastModifiedAt": "2026-07-30T14:15:16Z"
                }
                """
            )!
            .AsObject();
        string root = MessageContractFixtureCatalog.ResolveFixtureRoot(
            TestContext.CurrentContext.TestDirectory
        );
        string bodyFile = Path.Combine(root, fixture.MaterializedCase, "expected-public-cdc-document.json");
        expected["document"] = JsonNode.Parse(File.ReadAllText(bodyFile))!["document"]!.DeepClone();
        MessageContractJson.ShouldEqual(
            fixture.ExpectedEnvelope,
            JsonSerializer.SerializeToElement(expected)
        );
    }

    [Test]
    public void It_preserves_the_shared_property_absence_and_descriptor_no_link_context()
    {
        JsonElement document = _fixtures
            .First(f =>
                f.MaterializedCase.EndsWith("school-address-property-absence", StringComparison.Ordinal)
            )
            .ExpectedEnvelope.GetProperty("document");
        document.GetProperty("addresses")[0].TryGetProperty("addressTypeDescriptor", out _).Should().BeTrue();
        document
            .GetProperty("addresses")[1]
            .TryGetProperty("addressTypeDescriptor", out _)
            .Should()
            .BeFalse();
        JsonElement descriptor = _fixtures
            .First(f => f.MaterializedCase.EndsWith("descriptor-school-type", StringComparison.Ordinal))
            .ExpectedEnvelope.GetProperty("document");
        descriptor.GetProperty("_etag").GetString().Should().Be("222-01234567.j._.n.i");
        descriptor.TryGetProperty("link", out _).Should().BeFalse();
    }

    [TestCase(
        "postgresql",
        "io.debezium.data.Uuid",
        "io.debezium.data.Json",
        "io.debezium.time.ZonedTimestamp"
    )]
    [TestCase("sqlserver", "", "", "io.debezium.time.IsoTimestamp")]
    public void It_retains_provider_schema_and_source_metadata(
        string provider,
        string uuidName,
        string jsonName,
        string timeName
    )
    {
        JsonElement record = _fixtures
            .First(f => f.SourceRecord.GetProperty("provider").GetString() == provider)
            .SourceRecord;
        JsonElement fields = record
            .GetProperty("valueSchema")
            .GetProperty("fields")
            .GetProperty("after")
            .GetProperty("fields");
        LogicalName(record.GetProperty("keySchema").GetProperty("fields").GetProperty("DocumentUuid"))
            .Should()
            .Be(uuidName);
        LogicalName(fields.GetProperty("DocumentJson")).Should().Be(jsonName);
        LogicalName(fields.GetProperty("LastModifiedAt")).Should().Be(timeName);
        fields.GetProperty("ContentVersion").GetProperty("type").GetString().Should().Be("INT64");
        record
            .GetProperty("valueSchema")
            .GetProperty("fields")
            .GetProperty("source")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be($"io.debezium.connector.{provider}.Source");
        foreach (
            JsonElement schema in new[]
            {
                record.GetProperty("keySchema").GetProperty("fields").GetProperty("DocumentUuid"),
                fields.GetProperty("DocumentUuid"),
                fields.GetProperty("DocumentJson"),
                fields.GetProperty("LastModifiedAt"),
            }.Where(schema => schema.TryGetProperty("name", out _))
        )
        {
            schema.GetProperty("version").GetInt32().Should().Be(1);
        }
        record
            .GetProperty("sourcePartition")
            .TryGetProperty("database", out _)
            .Should()
            .Be(provider == "sqlserver");
        record
            .GetProperty("sourcePartition")
            .GetProperty("server")
            .GetString()
            .Should()
            .Be("contract-source");
        record.GetProperty("sourceOffset").EnumerateObject().Should().NotBeEmpty();
        record.GetProperty("headers").GetArrayLength().Should().Be(1);
        record.GetProperty("timestamp").GetInt64().Should().Be(1785420916123);
    }

    [Test]
    public void It_binds_raw_document_json_from_the_cache_without_copying_public_bodies()
    {
        foreach (
            MessageContractFixture fixture in _fixtures.Where(f =>
                f.SourceRecord.GetProperty("operation").GetString() == "c"
            )
        )
        {
            JsonElement after = fixture.SourceRecord.GetProperty("value").GetProperty("after");
            MessageContractJson.ShouldEqual(
                Parse(after.GetProperty("DocumentJson").GetString()!),
                fixture.CacheRow.GetProperty("documentJson")
            );
            after.GetProperty("DocumentJson").GetString().Should().NotContain("_etag");
            after
                .GetProperty("StreamEtag")
                .GetString()
                .Should()
                .Be(fixture.ExpectedEnvelope.GetProperty("document").GetProperty("_etag").GetString());
        }
    }

    [Test]
    public void It_retains_sqlserver_unavailable_delete_before_and_postgresql_available_uuid()
    {
        foreach (
            MessageContractFixture fixture in _fixtures.Where(f =>
                f.SourceRecord.GetProperty("operation").GetString() == "d"
            )
        )
        {
            JsonElement record = fixture.SourceRecord;
            string expected =
                record.GetProperty("provider").GetString() == "sqlserver"
                    ? "__debezium_unavailable_value"
                    : fixture.CacheRow.GetProperty("documentUuid").GetString()!;
            record
                .GetProperty("value")
                .GetProperty("before")
                .GetProperty("DocumentUuid")
                .GetString()
                .Should()
                .Be(expected);
            record.GetProperty("value").GetProperty("after").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    private static string LogicalName(JsonElement schema) =>
        schema.TryGetProperty("name", out JsonElement name) ? name.GetString()! : "";

    internal static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}

[TestFixture]
[Category("CdcMessageContract")]
public class Given_MessageContractFixtureExactJson
{
    [TestCase("{}", "{\"x\":null}", false)]
    [TestCase("{\"a\":1,\"b\":2}", "{\"b\":2,\"a\":1}", true)]
    [TestCase("[1,2]", "[2,1]", false)]
    [TestCase("[null]", "[]", false)]
    [TestCase("1", "\"1\"", false)]
    [TestCase("true", "1", false)]
    [TestCase("null", "\"null\"", false)]
    [TestCase("9007199254740992", "9007199254740993", false)]
    [TestCase("9223372036854775808", "9223372036854775809", false)]
    [TestCase("0.1234567890123456789012345678901", "0.1234567890123456789012345678902", false)]
    [TestCase("0.1234567890123456789012345678901", "1234567890123456789012345678901e-31", true)]
    [TestCase("1.00", "1e0", true)]
    [TestCase("-0e999999999999999999999999", "0.000", true)]
    [TestCase("1e999999999999999999999999", "10e999999999999999999999998", true)]
    [TestCase("{\"a\":1,\"a\":1}", "{\"a\":1,\"b\":1}", false)]
    [TestCase("[1,{\"n\":-12.3400e2},null,true]", "[1.0,{\"n\":-1234},null,true]", true)]
    public void It_compares_exact_values_and_property_presence(string left, string right, bool expected) =>
        MessageContractJson
            .Equivalent(
                Given_MessageContractFixtureCatalog.Parse(left),
                Given_MessageContractFixtureCatalog.Parse(right)
            )
            .Should()
            .Be(expected);
}

[TestFixture]
[Category("CdcMessageContract")]
public class Given_MessageContractFixtureValidation
{
    [TestCase("{\"type\":\"INT64\",\"optional\":false}", "\"42\"")]
    [TestCase("{\"type\":\"INT64\",\"optional\":false}", "9223372036854775808")]
    [TestCase("{\"type\":\"INT64\",\"optional\":false}", "1.5")]
    [TestCase("{\"type\":\"STRING\",\"optional\":false}", "null")]
    [TestCase("{\"type\":\"STRING\"}", "\"x\"")]
    [TestCase("{\"type\":\"UNKNOWN\",\"optional\":true}", "null")]
    [TestCase(
        "{\"type\":\"STRUCT\",\"optional\":true,\"fields\":{\"x\":{\"type\":\"UNKNOWN\",\"optional\":true}}}",
        "null"
    )]
    [TestCase(
        "{\"type\":\"STRUCT\",\"optional\":false,\"fields\":{\"x\":{\"type\":\"STRING\",\"optional\":false}}}",
        "{}"
    )]
    [TestCase(
        "{\"type\":\"STRUCT\",\"optional\":false,\"fields\":{}}",
        "{\"secret-property\":\"secret-value\"}"
    )]
    public void It_rejects_invalid_schema_value_descriptors_with_bounded_diagnostics(
        string schema,
        string value
    )
    {
        Action act = () =>
            MessageContractFixtureCatalog.ValidateSchemaValue(
                Given_MessageContractFixtureCatalog.Parse(schema),
                Given_MessageContractFixtureCatalog.Parse(value)
            );
        string message = act.Should().Throw<MessageContractFixtureException>().Which.ToString();
        message.Should().NotContain("secret-property").And.NotContain("secret-value");
        act.Should().Throw<MessageContractFixtureException>().Which.Message.Length.Should().BeLessThan(160);
    }

    [TestCase("{}")]
    [TestCase("{\"x\":null}")]
    [TestCase("{\"x\":\"available\"}")]
    public void It_preserves_absent_null_and_available_optional_before_fields(string value)
    {
        Action act = () =>
            MessageContractFixtureCatalog.ValidateSchemaValue(
                Given_MessageContractFixtureCatalog.Parse(
                    """{"type":"STRUCT","optional":false,"fields":{"x":{"type":"STRING","optional":true}}}"""
                ),
                Given_MessageContractFixtureCatalog.Parse(value)
            );
        act.Should().NotThrow();
    }
}

[TestFixture]
[Category("CdcMessageContract")]
public class Given_MessageContractFixtureArtifactFiles
{
    private string _directory = "";

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cdc-message-fixtures-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");
        foreach (string path in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(_directory, "Fixtures", Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }
    }

    [TearDown]
    public void Teardown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void It_loads_from_detached_build_artifacts_without_the_checkout() =>
        MessageContractFixtureCatalog.LoadAll(_directory).Should().HaveCount(10);

    [Test]
    public void It_resolves_the_same_catalog_from_a_checkout_layout()
    {
        string[] expected = MessageContractFixtureCatalog
            .LoadAll(_directory)
            .Select(f => f.ScenarioId)
            .ToArray();
        string backend = Path.Combine(_directory, "src/dms/backend");
        Directory.CreateDirectory(backend);
        Directory.Move(Path.Combine(_directory, "Fixtures"), Path.Combine(backend, "Fixtures"));
        MessageContractFixtureCatalog.LoadAll(_directory).Select(f => f.ScenarioId).Should().Equal(expected);
        MessageContractFixtureCatalog.LoadAll(backend).Select(f => f.ScenarioId).Should().Equal(expected);
    }

    [Test]
    public void It_identifies_a_missing_catalog()
    {
        File.Delete(CatalogPath());
        Action act = () => MessageContractFixtureCatalog.LoadAll(_directory);
        act.Should().Throw<MessageContractFixtureException>().WithMessage("*fixture-root-missing*");
    }

    [Test]
    public void It_rejects_duplicate_scenario_ids()
    {
        JsonNode catalog = JsonNode.Parse(File.ReadAllText(CatalogPath()))!;
        catalog["scenarios"]![1]!["scenarioId"] = catalog["scenarios"]![0]!["scenarioId"]!.DeepClone();
        File.WriteAllText(CatalogPath(), catalog.ToJsonString());
        Action act = () => MessageContractFixtureCatalog.LoadAll(_directory);
        act.Should().Throw<MessageContractFixtureException>().WithMessage("*duplicate-scenario*");
    }

    [TestCase("../outside.json")]
    [TestCase("/outside.json")]
    public void It_rejects_fixture_references_outside_the_root(string reference)
    {
        JsonNode catalog = JsonNode.Parse(File.ReadAllText(CatalogPath()))!;
        catalog["scenarios"]![0]!["providerInput"] = reference;
        File.WriteAllText(CatalogPath(), catalog.ToJsonString());
        Action act = () => MessageContractFixtureCatalog.LoadAll(_directory);
        act.Should().Throw<MessageContractFixtureException>().WithMessage("*fixture-relative-path*");
    }

    [Test]
    public void It_validates_provider_inputs_after_binding_shared_values()
    {
        string path = Path.Combine(
            _directory,
            "Fixtures/cdc/message-contract/providers/postgresql-upsert.json"
        );
        JsonNode input = JsonNode.Parse(File.ReadAllText(path))!;
        input["schemas"]!["row"]!["fields"]!["ContentVersion"]!["type"] = "STRING";
        File.WriteAllText(path, input.ToJsonString());
        Action act = () => MessageContractFixtureCatalog.LoadAll(_directory);
        act.Should().Throw<MessageContractFixtureException>().WithMessage("*schema-value-type*");
    }

    [Test]
    public void It_bounds_recursive_schema_reference_failures()
    {
        string path = Path.Combine(
            _directory,
            "Fixtures/cdc/message-contract/providers/postgresql-upsert.json"
        );
        JsonNode input = JsonNode.Parse(File.ReadAllText(path))!;
        input["schemas"]!["row"] = JsonNode.Parse("""{"$schema":"row"}""");
        File.WriteAllText(path, input.ToJsonString());
        Action act = () => MessageContractFixtureCatalog.LoadAll(_directory);
        act.Should().Throw<MessageContractFixtureException>().WithMessage("*template-depth*");
    }

    private string CatalogPath() => Path.Combine(_directory, "Fixtures/cdc/message-contract/catalog.json");

    [TestCase("expected-cache-row.json")]
    [TestCase("expected-stream-etag.json")]
    [TestCase("expected-public-cdc-document.json")]
    public void It_identifies_missing_shared_files(string filename)
    {
        File.Delete(SharedPath(filename));
        Action act = () => MessageContractFixtureCatalog.LoadAll(_directory);
        act.Should().Throw<MessageContractFixtureException>().WithMessage("*file-missing*");
    }

    [TestCase("{}")]
    [TestCase("{secret-malformed-json")]
    public void It_rejects_incomplete_or_malformed_shared_files_without_dumping_contents(string contents)
    {
        File.WriteAllText(SharedPath("expected-public-cdc-document.json"), contents);
        Action act = () => MessageContractFixtureCatalog.LoadAll(_directory);
        act.Should()
            .Throw<MessageContractFixtureException>()
            .Which.Message.Should()
            .NotContain("secret-malformed-json");
    }

    [TestCase("id", "00000000-0000-0000-0000-000000000000")]
    [TestCase("_etag", "wrong")]
    [TestCase("_lastModifiedDate", "2026-07-30T14:15:17Z")]
    public void It_rejects_inconsistent_public_metadata(string field, string value)
    {
        string path = SharedPath("expected-public-cdc-document.json");
        JsonNode document = JsonNode.Parse(File.ReadAllText(path))!;
        document["document"]![field] = value;
        File.WriteAllText(path, document.ToJsonString());
        Action act = () => MessageContractFixtureCatalog.LoadAll(_directory);
        act.Should().Throw<MessageContractFixtureException>().WithMessage("*public-body-metadata*");
    }

    private string SharedPath(string filename) =>
        Path.Combine(
            _directory,
            "Fixtures/document-cache/materialized-documents/ordinary-link-bearing-student-school-association",
            filename
        );
}
