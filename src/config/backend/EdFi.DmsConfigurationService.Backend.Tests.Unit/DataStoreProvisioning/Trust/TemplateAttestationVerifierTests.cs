// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Trust;

public class TemplateAttestationVerifierTests
{
    private const string PackageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0";
    private const string PackageVersion = "1.0.123";
    private const string ProducerName = "local-dev";

    private static readonly byte[] _packageBytes = Encoding.UTF8.GetBytes("fake package bytes\n");
    private static readonly string _packageSha256 = Sha256Of(_packageBytes);

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static TrustVerdict Verify(
        string? document,
        TemplateTrustPolicy policy,
        string? packageSha256 = null,
        string packageId = PackageId,
        string packageVersion = PackageVersion
    ) =>
        TemplateAttestationVerifier.Verify(
            document,
            packageSha256 ?? _packageSha256,
            packageId,
            packageVersion,
            policy
        );

    private static TrustVerdict.Untrusted Refusal(TrustRefusalReason reason) => new(reason);

    [TestFixture]
    public class Given_an_attestation_signed_by_a_trusted_producer
    {
        private TrustVerdict _verdict = null!;
        private TrustVerdict _verdictThroughReadPolicy = null!;
        private TrustVerdict _verdictWithMixedCaseIdentity = null!;
        private TrustVerdict _verdictWithMixedCaseProducer = null!;

        [SetUp]
        public void Setup()
        {
            using EphemeralAttestationSigner signer = new(ProducerName);
            string document = signer.Attest(PackageId, PackageVersion, _packageSha256);
            TemplateTrustPolicy policy = signer.Policy;

            _verdict = Verify(document, policy);
            _verdictThroughReadPolicy = Verify(document, TemplateTrustPolicyReader.Read(signer.PolicyJson()));
            _verdictWithMixedCaseIdentity = Verify(
                document,
                policy,
                packageId: PackageId.ToUpperInvariant(),
                packageVersion: "1.0.123"
            );
            _verdictWithMixedCaseProducer = Verify(
                signer.Attest(PackageId, PackageVersion, _packageSha256, producer: "LOCAL-DEV"),
                policy
            );
        }

        [Test]
        public void It_is_trusted_and_names_the_producer() =>
            _verdict.Should().Be(new TrustVerdict.Trusted(ProducerName));

        [Test]
        public void It_verifies_through_a_policy_the_reader_loaded() =>
            _verdictThroughReadPolicy.Should().Be(new TrustVerdict.Trusted(ProducerName));

        [Test]
        public void It_compares_the_package_identity_ignoring_case() =>
            _verdictWithMixedCaseIdentity.Should().Be(new TrustVerdict.Trusted(ProducerName));

        [Test]
        public void It_compares_the_producer_ignoring_case_and_reports_the_policy_spelling() =>
            _verdictWithMixedCaseProducer.Should().Be(new TrustVerdict.Trusted(ProducerName));
    }

    [TestFixture]
    public class Given_the_package_bytes_changed_by_one_byte
    {
        private TrustVerdict _verdict = null!;
        private TrustVerdict _verdictWithWrongHashAndIdentity = null!;

        [SetUp]
        public void Setup()
        {
            using EphemeralAttestationSigner signer = new(ProducerName);
            string document = signer.Attest(PackageId, PackageVersion, _packageSha256);
            byte[] altered = [.. _packageBytes];
            altered[0] ^= 0x01;

            _verdict = Verify(document, signer.Policy, packageSha256: Sha256Of(altered));
            _verdictWithWrongHashAndIdentity = Verify(
                document,
                signer.Policy,
                packageSha256: Sha256Of(altered),
                packageId: "Other.Package"
            );
        }

        [Test]
        public void It_refuses_with_a_package_hash_mismatch() =>
            _verdict.Should().Be(Refusal(TrustRefusalReason.PackageHashMismatch));

        [Test]
        public void It_checks_the_hash_before_the_identity() =>
            _verdictWithWrongHashAndIdentity.Should().Be(Refusal(TrustRefusalReason.PackageHashMismatch));
    }

    [TestFixture]
    public class Given_the_signed_payload_was_altered
    {
        private TrustVerdict _verdictForAlteredPayload = null!;
        private TrustVerdict _verdictForPayloadThatIsNoLongerJson = null!;

        [SetUp]
        public void Setup()
        {
            using EphemeralAttestationSigner signer = new(ProducerName);
            byte[] originalPayload = EphemeralAttestationSigner.Payload(
                PackageId,
                PackageVersion,
                _packageSha256,
                ProducerName
            );
            byte[] signature = signer.Sign(originalPayload);

            byte[] alteredPayload = EphemeralAttestationSigner.Payload(
                PackageId,
                PackageVersion,
                Sha256Of([1, 2, 3]),
                ProducerName
            );
            _verdictForAlteredPayload = Verify(signer.Document(alteredPayload, signature), signer.Policy);
            _verdictForPayloadThatIsNoLongerJson = Verify(
                signer.Document(Encoding.UTF8.GetBytes("not json"), signature),
                signer.Policy
            );
        }

        [Test]
        public void It_refuses_with_a_bad_signature() =>
            _verdictForAlteredPayload.Should().Be(Refusal(TrustRefusalReason.BadSignature));

        [Test]
        public void It_checks_the_signature_before_parsing_the_payload() =>
            _verdictForPayloadThatIsNoLongerJson.Should().Be(Refusal(TrustRefusalReason.BadSignature));
    }

    [TestFixture]
    public class Given_the_signature_was_altered
    {
        private TrustVerdict _verdictForFlippedByte = null!;
        private TrustVerdict _verdictForTruncatedSignature = null!;
        private TrustVerdict _verdictForEmptySignature = null!;

        [SetUp]
        public void Setup()
        {
            using EphemeralAttestationSigner signer = new(ProducerName);
            byte[] payload = EphemeralAttestationSigner.Payload(
                PackageId,
                PackageVersion,
                _packageSha256,
                ProducerName
            );
            byte[] signature = signer.Sign(payload);

            byte[] flipped = [.. signature];
            flipped[^1] ^= 0x01;
            _verdictForFlippedByte = Verify(signer.Document(payload, flipped), signer.Policy);
            _verdictForTruncatedSignature = Verify(signer.Document(payload, signature[..^1]), signer.Policy);
            _verdictForEmptySignature = Verify(signer.Document(payload, []), signer.Policy);
        }

        [Test]
        public void It_refuses_a_flipped_byte() =>
            _verdictForFlippedByte.Should().Be(Refusal(TrustRefusalReason.BadSignature));

        [Test]
        public void It_refuses_a_truncated_signature() =>
            _verdictForTruncatedSignature.Should().Be(Refusal(TrustRefusalReason.BadSignature));

        [Test]
        public void It_refuses_an_empty_signature() =>
            _verdictForEmptySignature.Should().Be(Refusal(TrustRefusalReason.BadSignature));
    }

    [TestFixture]
    public class Given_a_key_the_policy_does_not_hold
    {
        private TrustVerdict _verdict = null!;
        private TrustVerdict _verdictWhenTheSecondProducerHoldsTheKey = null!;

        [SetUp]
        public void Setup()
        {
            using EphemeralAttestationSigner trusted = new(ProducerName);
            using EphemeralAttestationSigner stranger = new("stranger");
            string document = stranger.Attest(PackageId, PackageVersion, _packageSha256);

            _verdict = Verify(document, trusted.Policy);
            _verdictWhenTheSecondProducerHoldsTheKey = Verify(
                document,
                new TemplateTrustPolicy([trusted.Producer, stranger.Producer])
            );
        }

        [Test]
        public void It_refuses_with_an_unknown_key() =>
            _verdict.Should().Be(Refusal(TrustRefusalReason.UnknownKey));

        [Test]
        public void It_searches_every_producer_for_the_key() =>
            _verdictWhenTheSecondProducerHoldsTheKey.Should().Be(new TrustVerdict.Trusted("stranger"));
    }

    [TestFixture]
    public class Given_the_payload_names_another_producer
    {
        private TrustVerdict _verdict = null!;
        private TrustVerdict _verdictNamingAnotherTrustedProducer = null!;

        [SetUp]
        public void Setup()
        {
            using EphemeralAttestationSigner signer = new(ProducerName);
            using EphemeralAttestationSigner other = new("other-producer");

            _verdict = Verify(
                signer.Attest(PackageId, PackageVersion, _packageSha256, producer: "someone-else"),
                signer.Policy
            );
            _verdictNamingAnotherTrustedProducer = Verify(
                signer.Attest(PackageId, PackageVersion, _packageSha256, producer: other.ProducerName),
                new TemplateTrustPolicy([signer.Producer, other.Producer])
            );
        }

        [Test]
        public void It_refuses_with_a_producer_mismatch() =>
            _verdict.Should().Be(Refusal(TrustRefusalReason.ProducerMismatch));

        [Test]
        public void It_binds_the_producer_to_the_key_not_to_the_policy_as_a_whole() =>
            _verdictNamingAnotherTrustedProducer.Should().Be(Refusal(TrustRefusalReason.ProducerMismatch));
    }

    [TestFixture]
    public class Given_the_allowlisted_identity_differs
    {
        private TrustVerdict _verdictForAnotherId = null!;
        private TrustVerdict _verdictForAnotherVersion = null!;

        [SetUp]
        public void Setup()
        {
            using EphemeralAttestationSigner signer = new(ProducerName);
            string document = signer.Attest(PackageId, PackageVersion, _packageSha256);

            _verdictForAnotherId = Verify(
                document,
                signer.Policy,
                packageId: "EdFi.Api.Populated.Template.PostgreSql.5.2.0"
            );
            _verdictForAnotherVersion = Verify(document, signer.Policy, packageVersion: "1.0.124");
        }

        [Test]
        public void It_refuses_another_package_id() =>
            _verdictForAnotherId.Should().Be(Refusal(TrustRefusalReason.IdentityMismatch));

        [Test]
        public void It_refuses_another_package_version() =>
            _verdictForAnotherVersion.Should().Be(Refusal(TrustRefusalReason.IdentityMismatch));
    }

    [TestFixture]
    public class Given_hashes_that_are_not_lowercase
    {
        private Action _verifyWithUppercaseArgument = () => { };
        private TrustVerdict _verdictForSignedUppercaseHash = null!;

        [SetUp]
        public void Setup()
        {
            using EphemeralAttestationSigner signer = new(ProducerName);
            TemplateTrustPolicy policy = signer.Policy;
            string uppercase = _packageSha256.ToUpperInvariant();

            string document = signer.Attest(PackageId, PackageVersion, _packageSha256);
            _verifyWithUppercaseArgument = () => Verify(document, policy, packageSha256: uppercase);
            _verdictForSignedUppercaseHash = Verify(
                signer.Attest(PackageId, PackageVersion, uppercase),
                policy
            );
        }

        [Test]
        public void It_treats_an_uppercase_caller_hash_as_a_caller_defect() =>
            _verifyWithUppercaseArgument
                .Should()
                .Throw<ArgumentException>()
                .WithParameterName("packageSha256");

        [Test]
        public void It_refuses_a_signed_uppercase_hash_instead_of_normalizing_it() =>
            _verdictForSignedUppercaseHash.Should().Be(Refusal(TrustRefusalReason.Malformed));
    }

    [TestFixture]
    public class Given_versions_that_are_not_the_integer_one
    {
        private readonly Dictionary<string, TrustVerdict> _verdicts = [];

        [SetUp]
        public void Setup()
        {
            _verdicts.Clear();
            using EphemeralAttestationSigner signer = new(ProducerName);
            TemplateTrustPolicy policy = signer.Policy;
            string valid = signer.Attest(PackageId, PackageVersion, _packageSha256);

            _verdicts["document version \"1\""] = Verify(
                valid.Replace("\"version\":1", "\"version\":\"1\"", StringComparison.Ordinal),
                policy
            );
            _verdicts["document version 2"] = Verify(
                valid.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
                policy
            );
            _verdicts["document version 1.0"] = Verify(
                valid.Replace("\"version\":1", "\"version\":1.0", StringComparison.Ordinal),
                policy
            );
            _verdicts["document version 1e0"] = Verify(
                valid.Replace("\"version\":1", "\"version\":1e0", StringComparison.Ordinal),
                policy
            );
            _verdicts["payload attestationVersion \"1\""] = Verify(
                SignedWithVersion(signer, "\"1\""),
                policy
            );
            _verdicts["payload attestationVersion 1.0"] = Verify(SignedWithVersion(signer, "1.0"), policy);
        }

        private static string SignedWithVersion(EphemeralAttestationSigner signer, string versionJson)
        {
            byte[] payload = Encoding.UTF8.GetBytes(
                $$"""{"attestationVersion":{{versionJson}},"packageId":"{{PackageId}}","packageVersion":"{{PackageVersion}}","packageSha256":"{{_packageSha256}}","producer":"{{ProducerName}}","createdUtc":"2026-11-05T00:00:00.0000000Z"}"""
            );
            return signer.Document(payload, signer.Sign(payload));
        }

        [Test]
        public void It_refuses_every_document_version_but_the_integer_one()
        {
            _verdicts["document version \"1\""].Should().Be(Refusal(TrustRefusalReason.Malformed));
            _verdicts["document version 2"].Should().Be(Refusal(TrustRefusalReason.Malformed));
            _verdicts["document version 1.0"].Should().Be(Refusal(TrustRefusalReason.Malformed));
            _verdicts["document version 1e0"].Should().Be(Refusal(TrustRefusalReason.Malformed));
        }

        [Test]
        public void It_refuses_every_signed_payload_version_but_the_integer_one()
        {
            _verdicts["payload attestationVersion \"1\""].Should().Be(Refusal(TrustRefusalReason.Malformed));
            _verdicts["payload attestationVersion 1.0"].Should().Be(Refusal(TrustRefusalReason.Malformed));
        }
    }

    [TestFixture]
    public class Given_a_policy_key_on_p384
    {
        private Action _readPolicy = () => { };
        private Action _verifyWithHandBuiltPolicy = () => { };

        [SetUp]
        public void Setup()
        {
            using EphemeralAttestationSigner p384 = new(ProducerName, ECCurve.NamedCurves.nistP384);
            string policyJson = p384.PolicyJson();
            TemplateTrustPolicy handBuilt = p384.Policy;
            string document = p384.Attest(PackageId, PackageVersion, _packageSha256);

            _readPolicy = () => TemplateTrustPolicyReader.Read(policyJson);
            _verifyWithHandBuiltPolicy = () => Verify(document, handBuilt);
        }

        [Test]
        public void It_is_refused_by_the_reader() =>
            _readPolicy.Should().Throw<TemplateTrustPolicyException>().WithMessage("*P-256*");

        [Test]
        public void It_is_a_policy_defect_at_verification_time_not_a_verdict() =>
            _verifyWithHandBuiltPolicy.Should().Throw<TemplateTrustPolicyException>().WithMessage("*P-256*");
    }

    [TestFixture]
    public class Given_a_policy_without_detached_attestation_producers
    {
        private TrustVerdict _verdictForEmptyPolicy = null!;
        private TrustVerdict _verdictForNugetOnlyPolicy = null!;
        private TrustVerdict _verdictForGarbageDocument = null!;

        [SetUp]
        public void Setup()
        {
            using EphemeralAttestationSigner signer = new(ProducerName);
            string document = signer.Attest(PackageId, PackageVersion, _packageSha256);
            TemplateTrustPolicy empty = new([]);
            TemplateTrustPolicy nugetOnly = new([
                new TemplateTrustPolicy.Producer(
                    "edfi-alliance",
                    TemplateTrustPolicy.NugetAuthorSignatureProvider,
                    []
                ),
            ]);

            _verdictForEmptyPolicy = Verify(document, empty);
            _verdictForNugetOnlyPolicy = Verify(document, nugetOnly);
            _verdictForGarbageDocument = Verify("{ not json", empty);
        }

        [Test]
        public void It_fails_closed_for_an_empty_policy() =>
            _verdictForEmptyPolicy.Should().Be(Refusal(TrustRefusalReason.NoProducers));

        [Test]
        public void It_fails_closed_when_only_reserved_producers_exist() =>
            _verdictForNugetOnlyPolicy.Should().Be(Refusal(TrustRefusalReason.NoProducers));

        [Test]
        public void It_checks_the_producers_before_reading_the_document() =>
            _verdictForGarbageDocument.Should().Be(Refusal(TrustRefusalReason.NoProducers));
    }

    [TestFixture]
    public class Given_malformed_documents
    {
        private readonly Dictionary<string, TrustVerdict> _verdicts = [];

        [SetUp]
        public void Setup()
        {
            _verdicts.Clear();
            using EphemeralAttestationSigner signer = new(ProducerName);
            TemplateTrustPolicy policy = signer.Policy;
            byte[] payload = EphemeralAttestationSigner.Payload(
                PackageId,
                PackageVersion,
                _packageSha256,
                ProducerName
            );
            byte[] signature = signer.Sign(payload);
            string valid = signer.Document(payload, signature);

            _verdicts["null"] = Verify(null, policy);
            _verdicts["empty"] = Verify(string.Empty, policy);
            _verdicts["whitespace"] = Verify("  \n", policy);
            _verdicts["not json"] = Verify("{ not json", policy);
            _verdicts["array root"] = Verify("[]", policy);
            _verdicts["no version"] = Verify(
                valid.Replace("\"version\":1,", string.Empty, StringComparison.Ordinal),
                policy
            );
            _verdicts["duplicate payloadB64"] = Verify(
                valid.Replace(
                    "\"payloadB64\":",
                    "\"payloadB64\":\"AA==\",\"payloadB64\":",
                    StringComparison.Ordinal
                ),
                policy
            );
            _verdicts["empty payloadB64"] = Verify(signer.Document([], signature), policy);
            _verdicts["no signature"] = Verify(
                $$"""{"version":1,"payloadB64":"{{Convert.ToBase64String(payload)}}"}""",
                policy
            );
            _verdicts["signature is a string"] = Verify(
                $$"""{"version":1,"payloadB64":"{{Convert.ToBase64String(payload)}}","signature":"x"}""",
                policy
            );
            _verdicts["signature without keyId"] = Verify(
                $$$"""{"version":1,"payloadB64":"{{{Convert.ToBase64String(payload)}}}","signature":{"algorithm":"ECDSA_P256_SHA256","valueB64":"{{{Convert.ToBase64String(signature)}}}"}}""",
                policy
            );
            _verdicts["lowercase algorithm"] = Verify(
                signer.Document(payload, signature, algorithm: "ecdsa_p256_sha256"),
                policy
            );
            _verdicts["other algorithm"] = Verify(
                signer.Document(payload, signature, algorithm: "ECDSA_P384_SHA384"),
                policy
            );
            _verdicts["other algorithm and unknown key"] = Verify(
                signer.Document(payload, signature, algorithm: "RSA_SHA256", keyId: new string('0', 64)),
                policy
            );
            _verdicts["payloadB64 not base64"] = Verify(
                valid.Replace(Convert.ToBase64String(payload), "@@not-base64@@", StringComparison.Ordinal),
                policy
            );
            _verdicts["valueB64 not base64"] = Verify(
                valid.Replace(Convert.ToBase64String(signature), "@@not-base64@@", StringComparison.Ordinal),
                policy
            );
            _verdicts["signed payload without packageId"] = Verify(
                SignedPayload(
                    signer,
                    """{"attestationVersion":1,"packageVersion":"1.0.123","packageSha256":"{0}","producer":"local-dev"}"""
                ),
                policy
            );
            _verdicts["signed payload with numeric packageVersion"] = Verify(
                SignedPayload(
                    signer,
                    """{"attestationVersion":1,"packageId":"EdFi.Api.Minimal.Template.PostgreSql.5.2.0","packageVersion":1,"packageSha256":"{0}","producer":"local-dev"}"""
                ),
                policy
            );
            _verdicts["signed payload with non-hex hash"] = Verify(
                SignedPayload(
                    signer,
                    """{"attestationVersion":1,"packageId":"EdFi.Api.Minimal.Template.PostgreSql.5.2.0","packageVersion":"1.0.123","packageSha256":"not-a-hash","producer":"local-dev"}"""
                ),
                policy
            );
            _verdicts["signed payload that is an array"] = Verify(SignedPayload(signer, "[]"), policy);
        }

        private static string SignedPayload(EphemeralAttestationSigner signer, string payloadTemplate)
        {
            byte[] payload = Encoding.UTF8.GetBytes(
                payloadTemplate.Replace("{0}", _packageSha256, StringComparison.Ordinal)
            );
            return signer.Document(payload, signer.Sign(payload));
        }

        [Test]
        public void It_refuses_text_that_is_not_a_version_1_document()
        {
            foreach (
                string label in new[]
                {
                    "null",
                    "empty",
                    "whitespace",
                    "not json",
                    "array root",
                    "no version",
                    "duplicate payloadB64",
                    "empty payloadB64",
                }
            )
            {
                _verdicts[label].Should().Be(Refusal(TrustRefusalReason.Malformed), label);
            }
        }

        [Test]
        public void It_refuses_a_signature_block_that_is_not_three_strings()
        {
            foreach (
                string label in new[] { "no signature", "signature is a string", "signature without keyId" }
            )
            {
                _verdicts[label].Should().Be(Refusal(TrustRefusalReason.Malformed), label);
            }
        }

        [Test]
        public void It_refuses_any_algorithm_label_but_the_exact_contract_label()
        {
            _verdicts["lowercase algorithm"].Should().Be(Refusal(TrustRefusalReason.Malformed));
            _verdicts["other algorithm"].Should().Be(Refusal(TrustRefusalReason.Malformed));
        }

        [Test]
        public void It_checks_the_algorithm_before_looking_up_the_key() =>
            _verdicts["other algorithm and unknown key"].Should().Be(Refusal(TrustRefusalReason.Malformed));

        [Test]
        public void It_refuses_base64_that_does_not_decode()
        {
            _verdicts["payloadB64 not base64"].Should().Be(Refusal(TrustRefusalReason.Malformed));
            _verdicts["valueB64 not base64"].Should().Be(Refusal(TrustRefusalReason.Malformed));
        }

        [Test]
        public void It_refuses_a_verified_payload_that_is_not_in_the_version_1_shape()
        {
            foreach (
                string label in new[]
                {
                    "signed payload without packageId",
                    "signed payload with numeric packageVersion",
                    "signed payload with non-hex hash",
                    "signed payload that is an array",
                }
            )
            {
                _verdicts[label].Should().Be(Refusal(TrustRefusalReason.Malformed), label);
            }
        }
    }

    /// <summary>
    /// <see cref="System.Text.Json.JsonDocument"/> accepts an unpaired surrogate escape and invalid UTF-8 bytes
    /// inside strings and throws only when the text is read, so without the parse-time decode these inputs would
    /// escape as exceptions instead of verdicts. Every case here is also a verification-order pin: the signed
    /// payload cases carry valid signatures, so <c>Malformed</c> (not <c>BadSignature</c>) proves the payload was
    /// judged after the signature.
    /// </summary>
    [TestFixture]
    public class Given_documents_and_payloads_with_malformed_unicode
    {
        private const string Escape = "\\uD800";

        private readonly Dictionary<string, TrustVerdict> _verdicts = [];

        [SetUp]
        public void Setup()
        {
            _verdicts.Clear();
            using EphemeralAttestationSigner signer = new(ProducerName);
            TemplateTrustPolicy policy = signer.Policy;
            byte[] payload = EphemeralAttestationSigner.Payload(
                PackageId,
                PackageVersion,
                _packageSha256,
                ProducerName
            );
            string valid = signer.Document(payload, signer.Sign(payload));

            _verdicts["escape in a document value"] = Verify(
                valid.Replace(Convert.ToBase64String(payload), Escape, StringComparison.Ordinal),
                policy
            );
            _verdicts["escape as a document member name"] = Verify(
                valid.Replace("{\"version\"", "{\"" + Escape + "\":1,\"version\"", StringComparison.Ordinal),
                policy
            );
            _verdicts["escape as duplicate member names"] = Verify(
                valid.Replace(
                    "{\"version\"",
                    "{\"" + Escape + "\":1,\"" + Escape + "\":2,\"version\"",
                    StringComparison.Ordinal
                ),
                policy
            );
            _verdicts["lone surrogate character in the document"] = Verify(
                valid.Replace("{\"version\"", "{\"note\":\"\uD800\",\"version\"", StringComparison.Ordinal),
                policy
            );

            _verdicts["escape inside a signed packageId"] = Verify(
                Signed(signer, PackageId, PackageId[..4] + Escape + PackageId[4..]),
                policy
            );
            _verdicts["escape as a signed member name"] = Verify(
                Signed(signer, "{\"attestationVersion\"", "{\"" + Escape + "\":1,\"attestationVersion\""),
                policy
            );
            byte[] invalidUtf8 = [.. payload];
            invalidUtf8[payload.AsSpan().IndexOf("Minimal"u8)] = 0xFF;
            _verdicts["invalid UTF-8 inside a signed packageId"] = Verify(
                signer.Document(invalidUtf8, signer.Sign(invalidUtf8)),
                policy
            );
            _verdicts["well-formed surrogate pair in a signed member"] = Verify(
                Signed(signer, "{\"attestationVersion\"", "{\"note\":\"😀\",\"attestationVersion\""),
                policy
            );
        }

        /// <summary>A correctly signed payload whose text has <paramref name="find"/> replaced by <paramref name="replace"/>.</summary>
        private static string Signed(EphemeralAttestationSigner signer, string find, string replace)
        {
            string payloadText = Encoding.UTF8.GetString(
                EphemeralAttestationSigner.Payload(PackageId, PackageVersion, _packageSha256, ProducerName)
            );
            byte[] payload = Encoding.UTF8.GetBytes(
                payloadText.Replace(find, replace, StringComparison.Ordinal)
            );
            return signer.Document(payload, signer.Sign(payload));
        }

        [Test]
        public void It_refuses_a_document_whose_text_is_not_well_formed_unicode()
        {
            foreach (
                string label in new[]
                {
                    "escape in a document value",
                    "escape as a document member name",
                    "escape as duplicate member names",
                    "lone surrogate character in the document",
                }
            )
            {
                _verdicts[label].Should().Be(Refusal(TrustRefusalReason.Malformed), label);
            }
        }

        [Test]
        public void It_refuses_a_correctly_signed_payload_whose_text_is_not_well_formed_unicode()
        {
            foreach (
                string label in new[]
                {
                    "escape inside a signed packageId",
                    "escape as a signed member name",
                    "invalid UTF-8 inside a signed packageId",
                }
            )
            {
                _verdicts[label].Should().Be(Refusal(TrustRefusalReason.Malformed), label);
            }
        }

        [Test]
        public void It_accepts_well_formed_non_ascii_text() =>
            _verdicts["well-formed surrogate pair in a signed member"]
                .Should()
                .Be(new TrustVerdict.Trusted(ProducerName));
    }

    /// <summary>
    /// The committed fixture under <c>Fixtures/DataStoreProvisioning/Trust</c> was produced once by the delivered
    /// PowerShell tooling (<c>new-template-dev-trust.ps1 -Purpose Dev</c> and <c>Write-TemplatePackageAttestation</c>
    /// over a tiny package); the C# reader and verifier must accept exactly what that tooling wrote.
    /// </summary>
    [TestFixture]
    public class Given_the_PowerShell_produced_parity_fixture
    {
        private const string FixtureProducer = "dms-1438-parity";
        private const string FixturePackageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0";
        private const string FixturePackageVersion = "0.0.1-dms1438-parity";

        private TemplateTrustPolicy _policy = null!;
        private TrustVerdict _verdict = null!;
        private TrustVerdict _verdictForAlteredPackage = null!;
        private TrustVerdict _verdictForAnotherVersion = null!;

        [SetUp]
        public void Setup()
        {
            string directory = Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "DataStoreProvisioning",
                "Trust"
            );
            string packageFileName = $"{FixturePackageId}.{FixturePackageVersion}.nupkg";
            byte[] packageBytes = File.ReadAllBytes(Path.Combine(directory, packageFileName));
            string attestationJson = File.ReadAllText(
                Path.Combine(directory, $"{packageFileName}.attestation.json")
            );
            _policy = TemplateTrustPolicyReader.Read(
                File.ReadAllText(Path.Combine(directory, "template-trust-policy.json"))
            );

            string packageSha256 = Sha256Of(packageBytes);
            _verdict = TemplateAttestationVerifier.Verify(
                attestationJson,
                packageSha256,
                FixturePackageId,
                FixturePackageVersion,
                _policy
            );

            byte[] altered = [.. packageBytes];
            altered[^1] ^= 0x01;
            _verdictForAlteredPackage = TemplateAttestationVerifier.Verify(
                attestationJson,
                Sha256Of(altered),
                FixturePackageId,
                FixturePackageVersion,
                _policy
            );
            _verdictForAnotherVersion = TemplateAttestationVerifier.Verify(
                attestationJson,
                packageSha256,
                FixturePackageId,
                "0.0.2",
                _policy
            );
        }

        [Test]
        public void It_loads_the_policy_the_tooling_wrote()
        {
            _policy.Producers.Should().ContainSingle();
            _policy
                .DetachedAttestationProducers.Should()
                .ContainSingle()
                .Which.Name.Should()
                .Be(FixtureProducer);
            _policy.Producers[0].PublicKeys.Should().ContainSingle();
        }

        [Test]
        public void It_trusts_the_attestation_the_tooling_wrote() =>
            _verdict.Should().Be(new TrustVerdict.Trusted(FixtureProducer));

        [Test]
        public void It_binds_the_attestation_to_the_exact_package_bytes() =>
            _verdictForAlteredPackage.Should().Be(Refusal(TrustRefusalReason.PackageHashMismatch));

        [Test]
        public void It_binds_the_attestation_to_the_package_version() =>
            _verdictForAnotherVersion.Should().Be(Refusal(TrustRefusalReason.IdentityMismatch));
    }
}
