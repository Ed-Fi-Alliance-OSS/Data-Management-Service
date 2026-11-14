// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest.CanonicalInventory;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Manifest;

public class CanonicalInventoryJsonTests
{
    /// <summary>A single backslash, kept out of any <c>\u</c> spelling in this source file.</summary>
    private const string Backslash = "\\";

    [TestFixture]
    public class Given_the_pester_sample_inventory_in_input_order
    {
        private CanonicalInventory _inventory = null!;
        private string _json = null!;
        private string _jsonOfReorderedInput = null!;
        private string _jsonOfPrincipals = null!;

        private static Schema Edfi() =>
            new("edfi", [new Entry("School", "table"), new Entry("AcademicWeek", "table")]);

        private static Schema Dms() =>
            new(
                "dms",
                [
                    new Entry("GetMaxChangeVersion", "function"),
                    new Entry("Document", "TABLE"),
                    new Entry("EffectiveSchema", "table"),
                ]
            );

        [SetUp]
        public void Setup()
        {
            _inventory = Create([Edfi(), Dms()], []);
            _json = CanonicalInventoryJson.Serialize(_inventory);

            Schema dmsReordered = new(
                "dms",
                [
                    new Entry("EffectiveSchema", "table"),
                    new Entry("Document", "table"),
                    new Entry("GetMaxChangeVersion", "FUNCTION"),
                ]
            );
            _jsonOfReorderedInput = CanonicalInventoryJson.Serialize(Create([dmsReordered, Edfi()], []));

            _jsonOfPrincipals = CanonicalInventoryJson.Serialize(
                Create([Edfi(), Dms()], ["zeta_reader", "Alpha_writer", "alpha_writer"])
            );
        }

        [Test]
        public void It_serializes_exactly_the_delivered_canonical_text() =>
            _json.Should().Be(RestoreManifestSample.CanonicalInventoryText);

        [Test]
        public void It_sorts_schemas_ordinally_and_objects_by_lowercased_type_then_name()
        {
            _inventory.Schemas.Select(schema => schema.SchemaName).Should().Equal("dms", "edfi");
            _inventory
                .Schemas[0]
                .Objects.Should()
                .Equal(
                    new Entry("GetMaxChangeVersion", "function"),
                    new Entry("Document", "table"),
                    new Entry("EffectiveSchema", "table")
                );
        }

        [Test]
        public void It_produces_identical_output_regardless_of_input_order_and_type_casing() =>
            _jsonOfReorderedInput.Should().Be(_json);

        [Test]
        public void It_always_writes_principals_and_sorts_them_ordinally_uppercase_first() =>
            _jsonOfPrincipals
                .Should()
                .EndWith("],\"principals\":[\"Alpha_writer\",\"alpha_writer\",\"zeta_reader\"]}");
    }

    [TestFixture]
    public class Given_names_that_need_escaping
    {
        private string _json = null!;

        [SetUp]
        public void Setup()
        {
            CanonicalInventory inventory = Create(
                [
                    new Schema(
                        "we\"ird" + Backslash + "schema",
                        [
                            new Entry("namé", "table"),
                            new Entry("tab\tname", "table"),
                            new Entry("del\u007f", "table"),
                            new Entry("emoji\U0001F600", "table"),
                            new Entry("tilde~ok", "table"),
                        ]
                    ),
                ],
                ["rôle"]
            );
            _json = CanonicalInventoryJson.Serialize(inventory);
        }

        [Test]
        public void It_escapes_quote_and_backslash_with_a_backslash() =>
            _json
                .Should()
                .StartWith(
                    "{\"schemas\":[{\"schemaName\":\"we"
                        + Backslash
                        + "\"ird"
                        + Backslash
                        + Backslash
                        + "schema\","
                );

        [Test]
        public void It_escapes_every_code_unit_outside_printable_ascii_as_lowercase_u_escapes()
        {
            _json.Should().Contain("\"name\":\"nam" + Backslash + "u00e9\"");
            _json.Should().Contain("\"name\":\"tab" + Backslash + "u0009name\"");
            _json.Should().Contain("\"name\":\"del" + Backslash + "u007f\"");
            _json.Should().Contain("\"name\":\"emoji" + Backslash + "ud83d" + Backslash + "ude00\"");
            _json.Should().Contain("\"principals\":[\"r" + Backslash + "u00f4le\"]");
        }

        [Test]
        public void It_leaves_printable_ascii_alone() => _json.Should().Contain("\"name\":\"tilde~ok\"");

        [Test]
        public void It_is_pure_ascii() =>
            _json.Should().Match(text => text.All(character => character < '\u007f'));
    }

    [TestFixture]
    public class Given_inventories_that_are_not_canonical
    {
        private readonly Dictionary<string, string?> _defects = [];

        [SetUp]
        public void Setup()
        {
            _defects.Clear();
            Record("empty schema name", [new Schema(" ", [])], []);
            Record("duplicate schema", [new Schema("dms", []), new Schema("dms", [])], []);
            Record(
                "duplicate object across type casing",
                [new Schema("dms", [new Entry("Document", "table"), new Entry("Document", "TABLE")])],
                []
            );
            Record("object without a type", [new Schema("dms", [new Entry("x", "")])], []);
            Record("object without a name", [new Schema("dms", [new Entry("", "table")])], []);
            Record("duplicate principal", [new Schema("dms", [])], ["reader", "reader"]);
            Record("empty principal", [new Schema("dms", [])], [" "]);
            Record(
                "same object in two schemas",
                [new Schema("a", [new Entry("x", "table")]), new Schema("b", [new Entry("x", "table")])],
                []
            );
            Record(
                "same name with different types",
                [new Schema("a", [new Entry("x", "table"), new Entry("x", "view")])],
                []
            );
        }

        [Test]
        public void It_names_the_position_of_an_empty_schema_name() =>
            _defects["empty schema name"].Should().Be("schema entry 1 has an empty name.");

        [Test]
        public void It_refuses_a_repeated_schema_name() =>
            _defects["duplicate schema"].Should().Be("schema entry 2 repeats an earlier schema name.");

        [Test]
        public void It_refuses_a_repeated_object_even_when_the_types_differ_in_case() =>
            _defects["duplicate object across type casing"]
                .Should()
                .Be("object entry 2 of schema entry 1 repeats an earlier object of the same type and name.");

        [Test]
        public void It_refuses_an_object_without_both_a_name_and_a_type()
        {
            _defects["object without a type"]
                .Should()
                .Be("object entry 1 of schema entry 1 has an empty name or type.");
            _defects["object without a name"]
                .Should()
                .Be("object entry 1 of schema entry 1 has an empty name or type.");
        }

        [Test]
        public void It_refuses_a_repeated_or_empty_principal()
        {
            _defects["duplicate principal"].Should().Be("principal entry 2 repeats an earlier principal.");
            _defects["empty principal"].Should().Be("principal entry 1 is empty.");
        }

        [Test]
        public void It_scopes_object_uniqueness_to_the_schema_and_the_type()
        {
            _defects["same object in two schemas"].Should().BeNull();
            _defects["same name with different types"].Should().BeNull();
        }

        [Test]
        public void It_throws_an_argument_exception_from_create()
        {
            Action create = () => Create([new Schema("dms", []), new Schema("dms", [])], []);
            create
                .Should()
                .Throw<ArgumentException>()
                .WithMessage(
                    "The inventory is not canonical: schema entry 2 repeats an earlier schema name."
                );
        }

        private void Record(string label, Schema[] schemas, string[] principals)
        {
            TryCreate(schemas, principals, out _, out string? defect);
            _defects[label] = defect;
        }
    }
}
