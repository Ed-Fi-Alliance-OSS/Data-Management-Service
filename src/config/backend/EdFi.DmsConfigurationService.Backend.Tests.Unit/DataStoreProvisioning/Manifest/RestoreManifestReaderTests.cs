// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Manifest.RestoreManifestSample;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Manifest;

public class RestoreManifestReaderTests
{
    /// <summary>A single backslash, kept out of any <c>\u</c> spelling in this source file.</summary>
    private const string Backslash = "\\";

    private const string Missing = "The restore manifest is missing required field '{0}'.";

    private const string NotHex =
        "The restore manifest field '{0}' must be a 64-character lowercase hex SHA-256.";

    private const string NotContractMember =
        "The restore manifest declares a member at position {0} that is not part of the version 1 contract.";

    private static readonly string[] _requiredFields =
    [
        "version",
        "packageId",
        "packageVersion",
        "databaseEngine",
        "templateKind",
        "dataStandardVersion",
        "contentProfile",
        "projects",
        "apiSchemaFormatVersion",
        "effectiveSchemaHash",
        "resourceKeyCount",
        "resourceKeySeedHashB64",
        "relationalMappingVersion",
        "engineVersion",
        "documentJsonColumnType",
        "inventory",
        "inventorySha256",
        "artifactFileName",
        "artifactSha256",
    ];

    [TestFixture]
    public class Given_the_valid_postgresql_sample
    {
        private RestoreManifest _manifest = null!;

        [SetUp]
        public void Setup() => _manifest = Read(PostgresqlManifest());

        [Test]
        public void It_reads_the_identity_members()
        {
            _manifest.Version.Should().Be(1);
            _manifest.PackageId.Should().Be(PostgresqlPackageId);
            _manifest.PackageVersion.Should().Be("1.0.123");
            _manifest.DatabaseEngine.Should().Be("postgresql");
            _manifest.TemplateKind.Should().Be("Minimal");
            _manifest.DataStandardVersion.Should().Be("5.2.0");
            _manifest.ContentProfile.Should().Be("DmsDatastoreOnly");
        }

        [Test]
        public void It_keeps_projects_in_document_order() =>
            _manifest.Projects.Should().Equal("edfi", "tpdm");

        [Test]
        public void It_reads_the_compatibility_members()
        {
            _manifest.ApiSchemaFormatVersion.Should().Be("1.0.0");
            _manifest.EffectiveSchemaHash.Should().Be(EffectiveSchemaHash);
            _manifest.ResourceKeyCount.Should().Be(42);
            _manifest.ResourceKeySeedHashB64.Should().Be(ResourceKeySeedHashB64);
            _manifest.RelationalMappingVersion.Should().Be("v2");
            _manifest.EngineVersion.Should().Be("16.8");
            _manifest.DocumentJsonColumnType.Should().Be("jsonb");
        }

        [Test]
        public void It_has_no_compatibility_level_on_postgresql() =>
            _manifest.DatabaseCompatibilityLevel.Should().BeNull();

        [Test]
        public void It_canonicalizes_the_unsorted_inventory() =>
            CanonicalInventoryJson.Serialize(_manifest.Inventory).Should().Be(CanonicalInventoryText);

        [Test]
        public void It_reads_the_hash_the_delivered_module_recorded_for_that_inventory() =>
            _manifest.InventorySha256.Should().Be(InventorySha256);

        [Test]
        public void It_reads_the_artifact_members()
        {
            _manifest.ArtifactFileName.Should().Be($"{PostgresqlPackageId}.sql");
            _manifest.ArtifactSha256.Should().Be(ArtifactSha256);
        }
    }

    [TestFixture]
    public class Given_the_valid_mssql_sample
    {
        private RestoreManifest _manifest = null!;
        private RestoreManifest _fromBytesWithByteOrderMark = null!;

        [SetUp]
        public void Setup()
        {
            _manifest = Read(MssqlManifest());
            byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(ToJson(MssqlManifest()))];
            _fromBytesWithByteOrderMark = RestoreManifestReader.Read(bytes);
        }

        [Test]
        public void It_reads_the_sql_server_members()
        {
            _manifest.DatabaseEngine.Should().Be("mssql");
            _manifest.PackageId.Should().Be(MssqlPackageId);
            _manifest.DatabaseCompatibilityLevel.Should().Be(170);
            _manifest.DocumentJsonColumnType.Should().Be("nvarchar(max)");
            _manifest.ArtifactFileName.Should().Be($"{MssqlPackageId}.bak");
        }

        [Test]
        public void It_reads_the_same_manifest_from_bytes_behind_a_byte_order_mark()
        {
            _fromBytesWithByteOrderMark.PackageId.Should().Be(MssqlPackageId);
            _fromBytesWithByteOrderMark.DatabaseCompatibilityLevel.Should().Be(170);
            _fromBytesWithByteOrderMark.InventorySha256.Should().Be(InventorySha256);
        }
    }

    [TestFixture]
    public class Given_each_required_field_removed_in_turn
    {
        private readonly Dictionary<string, string?> _whenAbsent = [];
        private readonly Dictionary<string, string?> _whenNull = [];

        [SetUp]
        public void Setup()
        {
            _whenAbsent.Clear();
            _whenNull.Clear();
            foreach (string field in _requiredFields)
            {
                _whenAbsent[field] = RejectionOf(manifest => manifest.Remove(field));
                _whenNull[field] = RejectionOf(manifest => manifest[field] = null);
            }
        }

        [Test]
        public void It_covers_every_delivered_required_field() => _requiredFields.Should().HaveCount(19);

        [Test]
        public void It_names_the_absent_field()
        {
            foreach (string field in _requiredFields)
            {
                _whenAbsent[field].Should().Be(string.Format(Missing, field), field);
            }
        }

        [Test]
        public void It_treats_json_null_as_absent()
        {
            foreach (string field in _requiredFields)
            {
                _whenNull[field].Should().Be(string.Format(Missing, field), field);
            }
        }
    }

    [TestFixture]
    public class Given_a_manifest_that_violates_one_rule
    {
        private readonly Dictionary<string, string?> _verdicts = [];

        [SetUp]
        public void Setup()
        {
            _verdicts.Clear();
            Record("version as string", m => m["version"] = "1");
            _verdicts["version with a fraction"] = RejectionOf(
                ToJson(PostgresqlManifest())
                    .Replace("\"version\":1,", "\"version\":1.0,", StringComparison.Ordinal)
            );
            Record("version 2", m => m["version"] = 2);
            foreach (string engine in new[] { "oracle", "PostgreSQL", "MSSQL" })
            {
                Record($"engine {engine}", m => m["databaseEngine"] = engine);
            }

            foreach (string kind in new[] { "Full", "minimal", "POPULATED" })
            {
                Record($"kind {kind}", m => m["templateKind"] = kind);
            }

            foreach (string profile in new[] { "dmsdatastoreonly", "Everything", "DmsDatastoreOnly " })
            {
                Record($"profile {profile}", m => m["contentProfile"] = profile);
            }

            Record("projects scalar", m => m["projects"] = "edfi");
            Record("projects empty", m => m["projects"] = new JsonArray());
            Record("projects duplicate ignoring case", m => m["projects"] = new JsonArray("edfi", "EDFI"));
            Record("projects number entry", m => m["projects"] = new JsonArray("edfi", 5));
            Record("projects blank entry", m => m["projects"] = new JsonArray("edfi", " "));
            foreach (string field in new[] { "effectiveSchemaHash", "inventorySha256", "artifactSha256" })
            {
                Record($"{field} uppercase", m => m[field] = string.Concat(Enumerable.Repeat("AB", 32)));
                Record($"{field} short", m => m[field] = "abc123");
                Record(
                    $"{field} trailing newline",
                    m => m[field] = string.Concat(Enumerable.Repeat("ab", 32)) + "\n"
                );
            }

            Record("resourceKeyCount zero", m => m["resourceKeyCount"] = 0);
            Record("resourceKeyCount string", m => m["resourceKeyCount"] = "42");
            Record("resourceKeyCount fraction", m => m["resourceKeyCount"] = 42.5);
            Record("seed not base64", m => m["resourceKeySeedHashB64"] = "not base64!!");
            Record("seed 16 bytes", m => m["resourceKeySeedHashB64"] = Convert.ToBase64String(new byte[16]));
            Record("mssql without level", m => m.Remove("databaseCompatibilityLevel"), mssql: true);
            Record("mssql level 80", m => m["databaseCompatibilityLevel"] = 80, mssql: true);
            Record("mssql level string", m => m["databaseCompatibilityLevel"] = "170", mssql: true);
            Record("postgresql level 160", m => m["databaseCompatibilityLevel"] = 160);
            Record("postgresql level null", m => m["databaseCompatibilityLevel"] = null);
            Record(
                "inventory hash of another inventory",
                m => m["inventorySha256"] = string.Concat(Enumerable.Repeat("00", 32))
            );
            Record(
                "inventory changed after hashing",
                m =>
                    ((JsonArray)m["inventory"]!["schemas"]!).Add(
                        new JsonObject { ["schemaName"] = "extra", ["objects"] = new JsonArray() }
                    )
            );
            Record("artifact with slash", m => m["artifactFileName"] = "nested/dump.sql");
            Record("artifact with backslash", m => m["artifactFileName"] = "nested" + Backslash + "dump.sql");
            Record("postgresql artifact .bak", m => m["artifactFileName"] = "backup.bak");
            Record("mssql artifact .sql", m => m["artifactFileName"] = "dump.sql", mssql: true);
            Record("artifact uppercase extension", m => m["artifactFileName"] = "DUMP.SQL");
            Record("unknown member", m => m["createdUtc"] = "2026-11-05T00:00:00Z");
            Record("whitespace packageId", m => m["packageId"] = "  ");
            Record("numeric packageVersion", m => m["packageVersion"] = 123);
        }

        [Test]
        public void It_requires_an_integer_version_equal_to_one()
        {
            _verdicts["version as string"]
                .Should()
                .Be("The restore manifest field 'version' must be an integer manifest format version.");
            _verdicts["version with a fraction"]
                .Should()
                .Be("The restore manifest field 'version' must be an integer manifest format version.");
            _verdicts["version 2"]
                .Should()
                .Be("The restore manifest field 'version' is unsupported; only version 1 is supported.");
        }

        [Test]
        public void It_requires_an_exact_lowercase_engine_token()
        {
            foreach (string engine in new[] { "oracle", "PostgreSQL", "MSSQL" })
            {
                _verdicts[$"engine {engine}"]
                    .Should()
                    .Be(
                        "The restore manifest field 'databaseEngine' must be exactly 'postgresql' or 'mssql'.",
                        engine
                    );
            }
        }

        [Test]
        public void It_requires_an_exact_template_kind()
        {
            foreach (string kind in new[] { "Full", "minimal", "POPULATED" })
            {
                _verdicts[$"kind {kind}"]
                    .Should()
                    .Be(
                        "The restore manifest field 'templateKind' must be exactly 'Minimal' or 'Populated'.",
                        kind
                    );
            }
        }

        [Test]
        public void It_requires_the_exact_dms_datastore_only_profile()
        {
            foreach (string profile in new[] { "dmsdatastoreonly", "Everything", "DmsDatastoreOnly " })
            {
                _verdicts[$"profile {profile}"]
                    .Should()
                    .StartWith(
                        "The restore manifest field 'contentProfile' must be exactly 'DmsDatastoreOnly'",
                        profile
                    );
            }
        }

        [Test]
        public void It_requires_a_non_empty_array_of_unique_project_strings()
        {
            const string NotAnArray =
                "The restore manifest field 'projects' must be a non-empty JSON array of project schema names.";
            _verdicts["projects scalar"].Should().Be(NotAnArray);
            _verdicts["projects empty"].Should().Be(NotAnArray);
            _verdicts["projects duplicate ignoring case"]
                .Should()
                .Be("The restore manifest field 'projects' contains a duplicate entry.");
            _verdicts["projects number entry"]
                .Should()
                .Be(
                    "The restore manifest field 'projects' contains an entry that is not a non-empty JSON string."
                );
            _verdicts["projects blank entry"]
                .Should()
                .Be(
                    "The restore manifest field 'projects' contains an entry that is not a non-empty JSON string."
                );
        }

        [Test]
        public void It_requires_lowercase_64_hex_hashes_with_nothing_after_them()
        {
            foreach (string field in new[] { "effectiveSchemaHash", "inventorySha256", "artifactSha256" })
            {
                _verdicts[$"{field} uppercase"].Should().Be(string.Format(NotHex, field), field);
                _verdicts[$"{field} short"].Should().Be(string.Format(NotHex, field), field);
                _verdicts[$"{field} trailing newline"].Should().Be(string.Format(NotHex, field), field);
            }
        }

        [Test]
        public void It_requires_a_positive_integer_resource_key_count()
        {
            const string Rule = "The restore manifest field 'resourceKeyCount' must be a positive integer.";
            _verdicts["resourceKeyCount zero"].Should().Be(Rule);
            _verdicts["resourceKeyCount string"].Should().Be(Rule);
            _verdicts["resourceKeyCount fraction"].Should().Be(Rule);
        }

        [Test]
        public void It_requires_a_32_byte_base64_seed_hash()
        {
            _verdicts["seed not base64"]
                .Should()
                .Be("The restore manifest field 'resourceKeySeedHashB64' is not valid base64.");
            _verdicts["seed 16 bytes"]
                .Should()
                .Be(
                    "The restore manifest field 'resourceKeySeedHashB64' must decode to exactly 32 bytes (SHA-256)."
                );
        }

        [Test]
        public void It_requires_a_compatibility_level_of_at_least_90_for_mssql()
        {
            const string Rule =
                "The restore manifest field 'databaseCompatibilityLevel' is required for mssql and must be an integer SQL Server compatibility level (90 or higher).";
            _verdicts["mssql without level"].Should().Be(Rule);
            _verdicts["mssql level 80"].Should().Be(Rule);
            _verdicts["mssql level string"].Should().Be(Rule);
        }

        [Test]
        public void It_forbids_a_compatibility_level_for_postgresql_but_allows_null()
        {
            _verdicts["postgresql level 160"]
                .Should()
                .Be(
                    "The restore manifest field 'databaseCompatibilityLevel' must be omitted or null for postgresql."
                );
            _verdicts["postgresql level null"].Should().BeNull();
        }

        [Test]
        public void It_recomputes_the_inventory_hash_and_requires_it_to_match()
        {
            const string Rule =
                "The restore manifest is internally inconsistent: the recomputed canonical inventory hash does not match the recorded 'inventorySha256'.";
            _verdicts["inventory hash of another inventory"].Should().Be(Rule);
            _verdicts["inventory changed after hashing"].Should().Be(Rule);
        }

        [Test]
        public void It_requires_a_plain_artifact_file_name_with_the_engine_extension()
        {
            const string Charset =
                "The restore manifest field 'artifactFileName' contains unsupported characters; only letters, digits, dots, dashes, and underscores are allowed.";
            _verdicts["artifact with slash"].Should().Be(Charset);
            _verdicts["artifact with backslash"].Should().Be(Charset);
            _verdicts["postgresql artifact .bak"]
                .Should()
                .Be(
                    "The restore manifest field 'artifactFileName' must end with '.sql' for databaseEngine 'postgresql'."
                );
            _verdicts["mssql artifact .sql"]
                .Should()
                .Be(
                    "The restore manifest field 'artifactFileName' must end with '.bak' for databaseEngine 'mssql'."
                );
            _verdicts["artifact uppercase extension"].Should().BeNull();
        }

        [Test]
        public void It_rejects_a_member_outside_the_contract_by_position() =>
            _verdicts["unknown member"].Should().Be(string.Format(NotContractMember, 20));

        [Test]
        public void It_requires_text_fields_to_be_non_blank_json_strings()
        {
            _verdicts["whitespace packageId"]
                .Should()
                .Be("The restore manifest field 'packageId' must be a non-empty JSON string.");
            _verdicts["numeric packageVersion"]
                .Should()
                .Be("The restore manifest field 'packageVersion' must be a non-empty JSON string.");
        }

        private void Record(string label, Action<JsonObject> mutate, bool mssql = false) =>
            _verdicts[label] = RejectionOf(mutate, mssql);
    }

    [TestFixture]
    public class Given_documents_that_are_not_manifests
    {
        private readonly Dictionary<string, string?> _verdicts = [];
        private string? _verdictForEmptyBytes;

        [SetUp]
        public void Setup()
        {
            _verdicts.Clear();
            string valid = ToJson(PostgresqlManifest());
            _verdicts["empty"] = RejectionOf(string.Empty);
            _verdicts["whitespace"] = RejectionOf(" \n\t");
            _verdicts["not json"] = RejectionOf("{ not json");
            _verdicts["array root"] = RejectionOf("[]");
            _verdicts["string root"] = RejectionOf("\"manifest\"");
            _verdicts["duplicate member"] = RejectionOf(
                valid.Replace("\"version\":1,", "\"version\":1,\"version\":1,", StringComparison.Ordinal)
            );
            _verdicts["unpaired surrogate escape"] = RejectionOf(
                valid.Replace("\"1.0.123\"", "\"1.0.123" + Backslash + "uD800\"", StringComparison.Ordinal)
            );
            _verdicts["unpaired surrogate escape in a member name"] = RejectionOf(
                valid.Replace(
                    "\"packageVersion\":",
                    "\"package" + Backslash + "uDC00Version\":",
                    StringComparison.Ordinal
                )
            );
            _verdicts["trailing comma"] = RejectionOf(
                valid.Replace("\"version\":1,", "\"version\":1,,", StringComparison.Ordinal)
            );
            try
            {
                RestoreManifestReader.Read(ReadOnlyMemory<byte>.Empty);
            }
            catch (RestoreManifestException exception)
            {
                _verdictForEmptyBytes = exception.Message;
            }
        }

        [Test]
        public void It_reports_an_empty_document()
        {
            _verdicts["empty"].Should().Be("The restore manifest is empty.");
            _verdicts["whitespace"].Should().Be("The restore manifest is empty.");
            _verdictForEmptyBytes.Should().Be("The restore manifest is empty.");
        }

        [Test]
        public void It_reports_text_that_is_not_strict_json()
        {
            _verdicts["not json"].Should().Be("The restore manifest is not valid JSON.");
            _verdicts["duplicate member"].Should().Be("The restore manifest is not valid JSON.");
            _verdicts["trailing comma"].Should().Be("The restore manifest is not valid JSON.");
        }

        [Test]
        public void It_requires_an_object_root()
        {
            _verdicts["array root"].Should().Be("The restore manifest must be a JSON object.");
            _verdicts["string root"].Should().Be("The restore manifest must be a JSON object.");
        }

        [Test]
        public void It_reports_malformed_unicode_as_a_document_defect_before_any_value_is_read()
        {
            _verdicts["unpaired surrogate escape"]
                .Should()
                .Be("The restore manifest contains text that is not well-formed Unicode.");
            _verdicts["unpaired surrogate escape in a member name"]
                .Should()
                .Be("The restore manifest contains text that is not well-formed Unicode.");
        }
    }

    [TestFixture]
    public class Given_an_inventory_that_violates_one_rule
    {
        private readonly Dictionary<string, string?> _verdicts = [];

        [SetUp]
        public void Setup()
        {
            _verdicts.Clear();
            _verdicts["inventory scalar"] = RejectionOf(manifest => manifest["inventory"] = "x");
            Record("schemas absent", inventory => inventory.Remove("schemas"));
            Record("schemas null", inventory => inventory["schemas"] = null);
            Record("schemas scalar", inventory => inventory["schemas"] = "dms");
            Record(
                "schemas singleton object",
                inventory =>
                    inventory["schemas"] = new JsonObject
                    {
                        ["schemaName"] = "dms",
                        ["objects"] = new JsonArray(),
                    }
            );
            Record("null schema entry", inventory => inventory["schemas"] = new JsonArray((JsonNode?)null));
            Record(
                "schema without a name",
                inventory =>
                    inventory["schemas"] = new JsonArray(new JsonObject { ["objects"] = new JsonArray() })
            );
            Record(
                "schema with a numeric name",
                inventory =>
                    inventory["schemas"] = new JsonArray(
                        new JsonObject { ["schemaName"] = 5, ["objects"] = new JsonArray() }
                    )
            );
            Record(
                "schema without objects",
                inventory => inventory["schemas"] = new JsonArray(new JsonObject { ["schemaName"] = "dms" })
            );
            Record(
                "schema with null objects",
                inventory =>
                    inventory["schemas"] = new JsonArray(
                        new JsonObject { ["schemaName"] = "dms", ["objects"] = null }
                    )
            );
            Record(
                "schema with a singleton object",
                inventory =>
                    inventory["schemas"] = new JsonArray(
                        new JsonObject { ["schemaName"] = "dms", ["objects"] = Entry("x", "table") }
                    )
            );
            Record(
                "schema with an extra member",
                inventory =>
                    inventory["schemas"] = new JsonArray(
                        new JsonObject
                        {
                            ["schemaName"] = "dms",
                            ["objects"] = new JsonArray(),
                            ["extra"] = 1,
                        }
                    )
            );
            Record(
                "null object entry",
                inventory =>
                    inventory["schemas"] = new JsonArray(
                        new JsonObject
                        {
                            ["schemaName"] = "dms",
                            ["objects"] = new JsonArray((JsonNode?)null),
                        }
                    )
            );
            Record(
                "object without a type",
                inventory =>
                    inventory["schemas"] = new JsonArray(
                        new JsonObject
                        {
                            ["schemaName"] = "dms",
                            ["objects"] = new JsonArray(new JsonObject { ["name"] = "x" }),
                        }
                    )
            );
            Record(
                "object with a blank name",
                inventory =>
                    inventory["schemas"] = new JsonArray(
                        new JsonObject
                        {
                            ["schemaName"] = "dms",
                            ["objects"] = new JsonArray(Entry(" ", "table")),
                        }
                    )
            );
            Record(
                "object with an extra member",
                inventory =>
                    inventory["schemas"] = new JsonArray(
                        new JsonObject
                        {
                            ["schemaName"] = "dms",
                            ["objects"] = new JsonArray(
                                new JsonObject
                                {
                                    ["name"] = "x",
                                    ["type"] = "table",
                                    ["owner"] = "dbo",
                                }
                            ),
                        }
                    )
            );
            Record("inventory with an extra member", inventory => inventory["generatedAt"] = "2026-11-05");
            Record("principals scalar", inventory => inventory["principals"] = "reader");
            Record("null principal", inventory => inventory["principals"] = new JsonArray((JsonNode?)null));
            Record("numeric principal", inventory => inventory["principals"] = new JsonArray(5));
            Record("principals null", inventory => inventory["principals"] = null);
            Record("principals absent", inventory => inventory.Remove("principals"));
            Record(
                "duplicate schema",
                inventory =>
                    ((JsonArray)inventory["schemas"]!).Add(
                        new JsonObject { ["schemaName"] = "edfi", ["objects"] = new JsonArray() }
                    )
            );
            Record(
                "duplicate object across type casing",
                inventory => ((JsonArray)inventory["schemas"]![0]!["objects"]!).Add(Entry("School", "TABLE"))
            );
            Record(
                "duplicate principal",
                inventory => inventory["principals"] = new JsonArray("reader", "reader")
            );
        }

        [Test]
        public void It_requires_an_inventory_object_with_a_schemas_array()
        {
            _verdicts["inventory scalar"]
                .Should()
                .Be("The restore manifest field 'inventory' must be a JSON object.");
            _verdicts["schemas absent"]
                .Should()
                .Be("The restore manifest inventory is missing the required 'schemas' array.");
            _verdicts["schemas null"]
                .Should()
                .Be("The restore manifest inventory is missing the required 'schemas' array.");
            _verdicts["schemas scalar"]
                .Should()
                .Be(
                    "The restore manifest inventory field 'schemas' must be a JSON array, not a single object or scalar."
                );
            _verdicts["schemas singleton object"]
                .Should()
                .Be(
                    "The restore manifest inventory field 'schemas' must be a JSON array, not a single object or scalar."
                );
        }

        [Test]
        public void It_requires_each_schema_entry_to_be_an_object_with_a_name_and_an_objects_array()
        {
            _verdicts["null schema entry"]
                .Should()
                .Be("The restore manifest inventory schema entry 1 must be a JSON object.");
            _verdicts["schema without a name"]
                .Should()
                .Be(
                    "The restore manifest inventory schema entry 1 must declare a non-empty 'schemaName' string."
                );
            _verdicts["schema with a numeric name"]
                .Should()
                .Be(
                    "The restore manifest inventory schema entry 1 must declare a non-empty 'schemaName' string."
                );
            _verdicts["schema without objects"]
                .Should()
                .Be(
                    "The restore manifest inventory schema entry 1 is missing its 'objects' array (an empty schema declares an empty array)."
                );
            _verdicts["schema with null objects"]
                .Should()
                .Be(
                    "The restore manifest inventory schema entry 1 is missing its 'objects' array (an empty schema declares an empty array)."
                );
            _verdicts["schema with a singleton object"]
                .Should()
                .Be(
                    "The restore manifest inventory schema entry 1 field 'objects' must be a JSON array, not a single object or scalar."
                );
        }

        [Test]
        public void It_requires_each_object_entry_to_be_an_object_with_a_name_and_a_type()
        {
            _verdicts["null object entry"]
                .Should()
                .Be("The restore manifest inventory object entry 1 of schema entry 1 must be a JSON object.");
            _verdicts["object without a type"]
                .Should()
                .Be(
                    "The restore manifest inventory object entry 1 of schema entry 1 must declare both a non-empty 'name' and a non-empty 'type' string."
                );
            _verdicts["object with a blank name"]
                .Should()
                .Be(
                    "The restore manifest inventory object entry 1 of schema entry 1 must declare both a non-empty 'name' and a non-empty 'type' string."
                );
        }

        [Test]
        public void It_rejects_members_outside_the_contract_at_every_level()
        {
            _verdicts["inventory with an extra member"]
                .Should()
                .Be(
                    "The restore manifest inventory declares a member at position 3 that is not part of the version 1 contract."
                );
            _verdicts["schema with an extra member"]
                .Should()
                .Be(
                    "The restore manifest inventory schema entry 1 declares a member at position 3 that is not part of the version 1 contract."
                );
            _verdicts["object with an extra member"]
                .Should()
                .Be(
                    "The restore manifest inventory object entry 1 of schema entry 1 declares a member at position 3 that is not part of the version 1 contract."
                );
        }

        [Test]
        public void It_requires_principals_to_be_an_array_of_non_empty_strings_when_present()
        {
            _verdicts["principals scalar"]
                .Should()
                .Be(
                    "The restore manifest inventory field 'principals' must be a JSON array when present, not a single value."
                );
            _verdicts["null principal"]
                .Should()
                .Be("The restore manifest inventory principal entry 1 must be a non-empty JSON string.");
            _verdicts["numeric principal"]
                .Should()
                .Be("The restore manifest inventory principal entry 1 must be a non-empty JSON string.");
        }

        [Test]
        public void It_treats_absent_or_null_principals_as_none()
        {
            _verdicts["principals null"].Should().BeNull();
            _verdicts["principals absent"].Should().BeNull();
        }

        [Test]
        public void It_refuses_an_inventory_that_is_not_canonical_before_comparing_hashes()
        {
            _verdicts["duplicate schema"]
                .Should()
                .Be(
                    "The restore manifest inventory is not canonical: schema entry 3 repeats an earlier schema name."
                );
            _verdicts["duplicate object across type casing"]
                .Should()
                .Be(
                    "The restore manifest inventory is not canonical: object entry 3 of schema entry 1 repeats an earlier object of the same type and name."
                );
            _verdicts["duplicate principal"]
                .Should()
                .Be(
                    "The restore manifest inventory is not canonical: principal entry 2 repeats an earlier principal."
                );
        }

        private void Record(string label, Action<JsonObject> mutateInventory) =>
            _verdicts[label] = RejectionOf(manifest => mutateInventory((JsonObject)manifest["inventory"]!));
    }

    /// <summary>
    /// The committed manifests under <c>Fixtures/DataStoreProvisioning/Manifest</c> were produced by the delivered
    /// module's <c>New-TemplateRestoreManifest</c>, which self-validates with <c>Assert-RestoreManifestShape</c>;
    /// the C# reader must accept exactly what that producer wrote, for both engines, and must recompute the
    /// inventory hash it recorded.
    /// </summary>
    [TestFixture]
    public class Given_the_PowerShell_produced_manifest_fixtures
    {
        private RestoreManifest _postgresql = null!;
        private RestoreManifest _mssql = null!;
        private RestoreManifest _mssqlFromBytes = null!;
        private string _fixtureInventoryHash = null!;

        [SetUp]
        public void Setup()
        {
            string directory = CanonicalInventoryHashTests.FixtureDirectory;
            _postgresql = RestoreManifestReader.Read(
                File.ReadAllText(Path.Combine(directory, "restore-manifest.postgresql.json"))
            );
            _mssql = RestoreManifestReader.Read(
                File.ReadAllText(Path.Combine(directory, "restore-manifest.mssql.json"))
            );
            _mssqlFromBytes = RestoreManifestReader.Read(
                File.ReadAllBytes(Path.Combine(directory, "restore-manifest.mssql.json"))
            );
            _fixtureInventoryHash = CanonicalInventoryHashTests.ReadFixtureText("inventory.sha256");
        }

        [Test]
        public void It_accepts_the_postgresql_manifest_the_producer_wrote()
        {
            _postgresql.PackageId.Should().Be(PostgresqlPackageId);
            _postgresql.DatabaseEngine.Should().Be("postgresql");
            _postgresql.DatabaseCompatibilityLevel.Should().BeNull();
            _postgresql.EngineVersion.Should().Be("18.1");
            _postgresql.RelationalMappingVersion.Should().Be("v3");
            _postgresql.ArtifactFileName.Should().EndWith(".sql");
        }

        [Test]
        public void It_accepts_the_mssql_manifest_the_producer_wrote_with_its_trailing_compatibility_level()
        {
            _mssql.PackageId.Should().Be(MssqlPackageId);
            _mssql.DatabaseEngine.Should().Be("mssql");
            _mssql.DatabaseCompatibilityLevel.Should().Be(170);
            _mssql.DocumentJsonColumnType.Should().Be("nvarchar(max)");
            _mssql.ArtifactFileName.Should().EndWith(".bak");
        }

        [Test]
        public void It_recomputes_the_inventory_hash_the_producer_recorded()
        {
            _postgresql.InventorySha256.Should().Be(_fixtureInventoryHash);
            _mssql.InventorySha256.Should().Be(_fixtureInventoryHash);
            CanonicalInventoryHash.Compute(_postgresql.Inventory).Should().Be(_fixtureInventoryHash);
        }

        [Test]
        public void It_reads_the_bytes_form_identically() =>
            _mssqlFromBytes.InventorySha256.Should().Be(_mssql.InventorySha256);

        [Test]
        public void It_reads_the_escaped_schema_name_back_to_its_characters() =>
            _postgresql
                .Inventory.Schemas.Select(schema => schema.SchemaName)
                .Should()
                .Contain("we\"ird" + Backslash + "schema");
    }
}
