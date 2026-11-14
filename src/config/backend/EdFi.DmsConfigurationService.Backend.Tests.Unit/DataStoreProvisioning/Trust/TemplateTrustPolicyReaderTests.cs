// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Trust;

public class TemplateTrustPolicyReaderTests
{
    private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Policy(params string[] producersJson) =>
        $$"""{ "_comment": "operator notes {{WellFormedPair}}", "version": 1, "producers": [ {{string.Join(",", producersJson)}} ] }""";

    /// <summary>A well-formed surrogate pair (U+1F600) in an ignored member: valid non-ASCII text must still load.</summary>
    private const string WellFormedPair = "😀";

    private static string NugetProducer(
        string name = "edfi-alliance",
        string fingerprints = $"\"{Fingerprint}\""
    ) =>
        $$"""{ "name": "{{name}}", "provider": "nuget-author-signature", "certificateFingerprints": [ {{fingerprints}} ] }""";

    [TestFixture]
    public class Given_the_tracked_schema_with_a_comment_and_both_provider_kinds
    {
        private string _producerName = null!;
        private string _keyId = null!;
        private TemplateTrustPolicy _policy = null!;

        [SetUp]
        public void Setup()
        {
            using EphemeralAttestationSigner signer = new("local-dev");
            _producerName = signer.ProducerName;
            _keyId = signer.KeyId;
            _policy = TemplateTrustPolicyReader.Read(
                Policy(
                    signer.ProducerJson(),
                    NugetProducer(fingerprints: $"\"{Fingerprint.ToUpperInvariant()}\"")
                )
            );
        }

        [Test]
        public void It_keeps_every_producer_in_document_order()
        {
            _policy.Producers.Should().HaveCount(2);
            _policy.Producers[0].Name.Should().Be(_producerName);
            _policy.Producers[1].Name.Should().Be("edfi-alliance");
        }

        [Test]
        public void It_exposes_only_the_detached_attestation_producer_to_verification()
        {
            TemplateTrustPolicy.Producer producer = _policy
                .DetachedAttestationProducers.Should()
                .ContainSingle()
                .Which;
            producer.Name.Should().Be(_producerName);
            producer.PublicKeys.Should().ContainSingle().Which.KeyId.Should().Be(_keyId);
        }

        [Test]
        public void It_keeps_no_keys_for_the_reserved_producer() =>
            _policy.Producers[1].PublicKeys.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_empty_producers_array
    {
        private TemplateTrustPolicy _policy = null!;

        [SetUp]
        public void Setup() =>
            _policy = TemplateTrustPolicyReader.Read("""{ "version": 1, "producers": [] }""");

        [Test]
        public void It_is_a_valid_policy_with_no_trust_anchors()
        {
            _policy.Producers.Should().BeEmpty();
            _policy.DetachedAttestationProducers.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_documents_that_violate_a_schema_rule
    {
        private const string SentinelName = "sentinel-producer-7f3a";

        private readonly Dictionary<string, string?> _messages = [];
        private string _keyId = null!;
        private string _publicKeySpkiB64 = null!;

        [SetUp]
        public void Setup()
        {
            _messages.Clear();
            using EphemeralAttestationSigner signer = new(SentinelName);
            using EphemeralAttestationSigner second = new("second");
            using EphemeralAttestationSigner p384 = new("p384", ECCurve.NamedCurves.nistP384);
            _keyId = signer.KeyId;
            _publicKeySpkiB64 = signer.PublicKeySpkiB64;
            string valid = signer.ProducerJson();
            string rsaSpki;
            using (RSA rsa = RSA.Create(2048))
            {
                rsaSpki = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
            }

            Record("empty", string.Empty);
            Record("whitespace", " \n ");
            Record("not json", "{ not json");
            Record("array root", "[]");
            Record("duplicate version", """{ "version": 1, "version": 1, "producers": [] }""");
            Record("no version", """{ "producers": [] }""");
            Record("string version", """{ "version": "1", "producers": [] }""");
            Record("version 2", """{ "version": 2, "producers": [] }""");
            Record("fractional version", """{ "version": 1.0, "producers": [] }""");
            Record("no producers", """{ "version": 1 }""");
            Record("null producers", """{ "version": 1, "producers": null }""");
            Record("producers object", $$"""{ "version": 1, "producers": {{valid}} }""");
            Record("null producer", """{ "version": 1, "producers": [ null ] }""");
            Record("scalar producer", """{ "version": 1, "producers": [ "x" ] }""");
            Record(
                "no name",
                valid.Replace($"\"name\":\"{SentinelName}\"", "\"nickname\":\"x\"", StringComparison.Ordinal)
            );
            Record(
                "blank name",
                valid.Replace($"\"name\":\"{SentinelName}\"", "\"name\":\"  \"", StringComparison.Ordinal)
            );
            Record(
                "no provider",
                valid.Replace(
                    "\"provider\":\"detached-attestation\"",
                    "\"kind\":\"x\"",
                    StringComparison.Ordinal
                )
            );
            Record(
                "unknown provider",
                valid.Replace("detached-attestation", "detached-signature", StringComparison.Ordinal)
            );
            Record(
                "wrong-case provider",
                valid.Replace("detached-attestation", "Detached-Attestation", StringComparison.Ordinal)
            );
            Record(
                "no publicKeys",
                $$"""{ "name": "{{SentinelName}}", "provider": "detached-attestation" }"""
            );
            Record(
                "null publicKeys",
                $$"""{ "name": "{{SentinelName}}", "provider": "detached-attestation", "publicKeys": null }"""
            );
            Record(
                "empty publicKeys",
                $$"""{ "name": "{{SentinelName}}", "provider": "detached-attestation", "publicKeys": [] }"""
            );
            Record(
                "publicKeys object",
                valid
                    .Replace("\"publicKeys\":[", "\"publicKeys\":", StringComparison.Ordinal)
                    .Replace("}]}", "}}", StringComparison.Ordinal)
            );
            Record(
                "scalar key",
                $$"""{ "name": "{{SentinelName}}", "provider": "detached-attestation", "publicKeys": [ "x" ] }"""
            );
            Record("uppercase keyId", signer.ProducerJson(keyId: signer.KeyId.ToUpperInvariant()));
            Record("short keyId", signer.ProducerJson(keyId: signer.KeyId[..63]));
            Record("mismatched keyId", signer.ProducerJson(keyId: second.KeyId));
            Record("lowercase algorithm", signer.ProducerJson(algorithm: "ecdsa_p256_sha256"));
            Record("other algorithm", signer.ProducerJson(algorithm: "ECDSA_P384_SHA384"));
            Record("spki not base64", signer.ProducerJson(publicKeySpkiB64: "@@not-base64@@"));
            Record(
                "spki not a key",
                signer.ProducerJson(
                    keyId: Sha256Of("not a key"u8.ToArray()),
                    publicKeySpkiB64: Convert.ToBase64String("not a key"u8.ToArray())
                )
            );
            Record(
                "spki is RSA",
                signer.ProducerJson(
                    keyId: Sha256Of(Convert.FromBase64String(rsaSpki)),
                    publicKeySpkiB64: rsaSpki
                )
            );
            Record("spki on P-384", p384.ProducerJson());
            Record(
                "nuget without fingerprints",
                $$"""{ "name": "{{SentinelName}}", "provider": "nuget-author-signature" }"""
            );
            Record(
                "nuget empty fingerprints",
                NugetProducer(SentinelName, fingerprints: string.Empty)
                    .Replace("[  ]", "[]", StringComparison.Ordinal)
            );
            Record("nuget short fingerprint", NugetProducer(SentinelName, fingerprints: "\"abc\""));
            Record("nuget numeric fingerprint", NugetProducer(SentinelName, fingerprints: "1234"));
            Record(
                "duplicate names ignoring case",
                Policy(valid, NugetProducer(SentinelName.ToUpperInvariant())),
                wrap: false
            );

            // Text that is not well-formed Unicode: JsonDocument accepts it and throws only when the text is
            // read, so each case must become a document defect, never an escaped exception or a read value.
            Record(
                "surrogate escape in name",
                valid.Replace(
                    $"\"name\":\"{SentinelName}\"",
                    "\"name\":\"" + Escape + "\"",
                    StringComparison.Ordinal
                )
            );
            Record(
                "surrogate escape in keyId",
                valid.Replace(signer.KeyId, Escape, StringComparison.Ordinal)
            );
            Record(
                "surrogate escape as a member name",
                $$$"""{ "version": 1, "{{{Escape}}}": 1, "producers": [ {{{valid}}} ] }""",
                wrap: false
            );
            Record(
                "lone surrogate character in text",
                $$$"""{ "version": 1, "_comment": "{{{LoneSurrogate}}}", "producers": [ {{{valid}}} ] }""",
                wrap: false
            );
            Record(
                "surrogate escape in fingerprint",
                NugetProducer(SentinelName, fingerprints: "\"" + Escape + "\"")
            );
        }

        /// <summary>The six-character JSON escape of an unpaired high surrogate.</summary>
        private const string Escape = "\\uD800";

        /// <summary>An unpaired high surrogate character itself.</summary>
        private const string LoneSurrogate = "\uD800";

        private void Record(string label, string json, bool wrap = true)
        {
            string document =
                wrap
                && json.TrimStart().StartsWith('{')
                && !json.Contains("\"producers\"", StringComparison.Ordinal)
                    ? Policy(json)
                    : json;
            try
            {
                TemplateTrustPolicyReader.Read(document);
                _messages[label] = null;
            }
            catch (TemplateTrustPolicyException exception)
            {
                _messages[label] = exception.Message;
            }
        }

        private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        private void ShouldReject(params string[] labels)
        {
            foreach (string label in labels)
            {
                _messages.Should().ContainKey(label);
                _messages[label].Should().NotBeNull($"'{label}' must be rejected");
            }
        }

        [Test]
        public void It_rejects_text_that_is_not_a_json_object() =>
            ShouldReject("empty", "whitespace", "not json", "array root", "duplicate version");

        [Test]
        public void It_requires_the_integer_version_one() =>
            ShouldReject("no version", "string version", "version 2", "fractional version");

        [Test]
        public void It_requires_a_producers_array() =>
            ShouldReject("no producers", "null producers", "producers object");

        [Test]
        public void It_requires_each_producer_to_be_an_object_with_a_name_and_a_known_provider() =>
            ShouldReject(
                "null producer",
                "scalar producer",
                "no name",
                "blank name",
                "no provider",
                "unknown provider",
                "wrong-case provider"
            );

        [Test]
        public void It_requires_a_detached_attestation_producer_to_list_at_least_one_key() =>
            ShouldReject(
                "no publicKeys",
                "null publicKeys",
                "empty publicKeys",
                "publicKeys object",
                "scalar key"
            );

        [Test]
        public void It_requires_each_key_to_be_a_canonical_self_consistent_p256_key() =>
            ShouldReject(
                "uppercase keyId",
                "short keyId",
                "mismatched keyId",
                "lowercase algorithm",
                "other algorithm",
                "spki not base64",
                "spki not a key",
                "spki is RSA",
                "spki on P-384"
            );

        [Test]
        public void It_requires_a_reserved_producer_to_list_hex_fingerprints() =>
            ShouldReject(
                "nuget without fingerprints",
                "nuget empty fingerprints",
                "nuget short fingerprint",
                "nuget numeric fingerprint"
            );

        [Test]
        public void It_rejects_duplicate_producer_names_ignoring_case() =>
            ShouldReject("duplicate names ignoring case");

        [Test]
        public void It_rejects_text_that_is_not_well_formed_unicode_as_a_document_defect()
        {
            foreach (
                string label in new[]
                {
                    "surrogate escape in name",
                    "surrogate escape in keyId",
                    "surrogate escape as a member name",
                    "lone surrogate character in text",
                    "surrogate escape in fingerprint",
                }
            )
            {
                _messages[label].Should().Contain("well-formed Unicode", label);
            }
        }

        [Test]
        public void It_names_the_rule_and_position_but_never_a_value()
        {
            string[] messages = [.. _messages.Values.Where(message => message is not null)!];
            messages.Should().NotBeEmpty();
            foreach (string message in messages)
            {
                message
                    .Should()
                    .NotContain(SentinelName)
                    .And.NotContain(_keyId)
                    .And.NotContain(_publicKeySpkiB64);
            }

            _messages["mismatched keyId"].Should().Contain("producer 0 key 0");
            _messages["duplicate names ignoring case"].Should().Contain("producer 1");
        }
    }
}
