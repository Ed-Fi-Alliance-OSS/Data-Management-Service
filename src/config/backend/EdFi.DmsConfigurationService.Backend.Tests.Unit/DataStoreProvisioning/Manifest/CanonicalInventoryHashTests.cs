// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest.CanonicalInventory;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Manifest;

public class CanonicalInventoryHashTests
{
    public static string FixtureDirectory =>
        Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "DataStoreProvisioning",
            "Manifest"
        );

    public static string ReadFixtureText(string fileName) =>
        File.ReadAllText(Path.Combine(FixtureDirectory, fileName)).TrimEnd('\r', '\n');

    [TestFixture]
    public class Given_the_pester_sample_inventory
    {
        private CanonicalInventory _inventory = null!;
        private string _hash = null!;
        private string _hashWithAPrincipal = null!;

        [SetUp]
        public void Setup()
        {
            Schema[] schemas =
            [
                new("edfi", [new Entry("School", "table"), new Entry("AcademicWeek", "table")]),
                new(
                    "dms",
                    [
                        new Entry("GetMaxChangeVersion", "function"),
                        new Entry("Document", "TABLE"),
                        new Entry("EffectiveSchema", "table"),
                    ]
                ),
            ];
            _inventory = Create(schemas, []);
            _hash = CanonicalInventoryHash.Compute(_inventory);
            _hashWithAPrincipal = CanonicalInventoryHash.Compute(Create(schemas, ["reader"]));
        }

        [Test]
        public void It_equals_the_hash_the_delivered_powershell_module_computes() =>
            _hash.Should().Be(RestoreManifestSample.InventorySha256);

        [Test]
        public void It_is_the_lowercase_hex_sha256_of_the_canonical_utf8_bytes()
        {
            byte[] expected = SHA256.HashData(
                Encoding.UTF8.GetBytes(CanonicalInventoryJson.Serialize(_inventory))
            );
            _hash.Should().Be(Convert.ToHexString(expected).ToLowerInvariant());
            _hash.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
        }

        [Test]
        public void It_changes_when_any_entry_changes() => _hashWithAPrincipal.Should().NotBe(_hash);
    }

    /// <summary>
    /// <c>Fixtures/DataStoreProvisioning/Manifest/inventory.json</c> is a deliberately unsorted raw inventory
    /// with every escaping class; <c>inventory.canonical.json</c> and <c>inventory.sha256</c> are what the
    /// delivered module (<c>ConvertTo-CanonicalInventoryJson</c>, <c>Get-CanonicalInventoryHash</c>) produced
    /// from it. The C# canonicalization must reproduce both byte for byte.
    /// </summary>
    [TestFixture]
    public class Given_the_PowerShell_produced_inventory_fixture
    {
        private CanonicalInventory _inventory = null!;
        private string _expectedCanonicalJson = null!;
        private string _expectedHash = null!;

        [SetUp]
        public void Setup()
        {
            _inventory = CanonicalInventoryJson.Read(
                RestoreManifestSample.ParseStrictly(ReadFixtureText("inventory.json"))
            );
            _expectedCanonicalJson = ReadFixtureText("inventory.canonical.json");
            _expectedHash = ReadFixtureText("inventory.sha256");
        }

        [Test]
        public void It_reads_every_schema_in_ordinal_order() =>
            _inventory
                .Schemas.Select(schema => schema.SchemaName)
                .Should()
                .Equal("dms", "edfi", "public", "tracked_changes_edfi", "we\"ird\\schema");

        [Test]
        public void It_serializes_the_text_the_module_wrote() =>
            CanonicalInventoryJson.Serialize(_inventory).Should().Be(_expectedCanonicalJson);

        [Test]
        public void It_computes_the_hash_the_module_recorded() =>
            CanonicalInventoryHash.Compute(_inventory).Should().Be(_expectedHash);

        [Test]
        public void It_is_a_real_parity_check_not_a_self_comparison() =>
            _expectedHash.Should().Be("045615fc790c89913b5c72a13a7b4c77dabb87e3e9233bafe32f1cb4b25f3f7d");
    }
}
