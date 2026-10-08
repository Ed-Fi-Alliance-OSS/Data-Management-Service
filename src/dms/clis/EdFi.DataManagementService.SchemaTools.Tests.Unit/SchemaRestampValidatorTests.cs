// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using EdFi.DataManagementService.SchemaTools.Restamping;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class Given_SchemaRestamp_Compatibility
{
    private const string OldHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TargetHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly byte[] SeedHash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private EffectiveSchemaInfo _target = null!;
    private SchemaRestampSnapshot _stored = null!;
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _target = new EffectiveSchemaInfo(
            "1.0.0",
            "v3",
            TargetHash,
            2,
            SeedHash,
            [
                new("ed-fi", "Ed-Fi", "5.1.0", false, new string('c', 64)),
                new("tpdm", "TPDM", "1.0.0", true, new string('d', 64)),
            ],
            [
                new(1, new QualifiedResourceName("Ed-Fi", "Student"), "5.1.0", false),
                new(2, new QualifiedResourceName("TPDM", "Widget"), "1.0.0", false),
            ]
        );
        _stored = new SchemaRestampSnapshot(
            [new(1, "1.0.0", OldHash, 2, SeedHash.ToArray())],
            [new(1, "Ed-Fi", "Student", "5.1.0"), new(2, "TPDM", "Widget", "1.0.0")],
            [
                new(OldHash, new("ed-fi", "Ed-Fi", "5.1.0", false)),
                new(OldHash, new("tpdm", "TPDM", "1.0.0", true)),
            ]
        );
        _logger = A.Fake<ILogger>();
    }

    [Test]
    public void It_accepts_a_different_hash_with_compatible_metadata() =>
        InvokingValidation().Should().NotThrow();

    [Test]
    public void It_accepts_an_identical_valid_hash()
    {
        _stored = _stored with
        {
            Fingerprints = [_stored.Fingerprints[0] with { EffectiveSchemaHash = TargetHash }],
            SchemaComponents = _stored
                .SchemaComponents.Select(component => component with { EffectiveSchemaHash = TargetHash })
                .ToArray(),
        };

        InvokingValidation().Should().NotThrow();
    }

    [Test]
    public void It_rejects_corrupt_metadata_even_when_the_hash_matches()
    {
        _stored = _stored with
        {
            Fingerprints = [_stored.Fingerprints[0] with { EffectiveSchemaHash = TargetHash }],
        };

        AssertValidationFailure("SchemaComponent", "EffectiveSchemaHash");
    }

    [Test]
    public void It_rejects_an_absent_singleton()
    {
        _stored = _stored with { Fingerprints = [] };
        AssertValidationFailure("EffectiveSchema", "singleton");
    }

    [Test]
    public void It_rejects_multiple_singletons()
    {
        _stored = _stored with { Fingerprints = [.. _stored.Fingerprints, _stored.Fingerprints[0]] };
        AssertValidationFailure("EffectiveSchema", "singleton");
    }

    [Test]
    public void It_rejects_the_wrong_singleton_id()
    {
        ChangeFingerprint(fingerprint => fingerprint with { SingletonId = 2 });
        AssertValidationFailure("EffectiveSchema", "EffectiveSchemaSingletonId");
    }

    [TestCase("", "ApiSchemaFormatVersion")]
    [TestCase(" ", "ApiSchemaFormatVersion")]
    [TestCase("1.0.1", "ApiSchemaFormatVersion")]
    [TestCase("1.0.0 ", "ApiSchemaFormatVersion")]
    [TestCase("INVALID", "ApiSchemaFormatVersion")]
    public void It_rejects_incompatible_format_values(string format, string field)
    {
        ChangeFingerprint(fingerprint => fingerprint with { ApiSchemaFormatVersion = format });
        AssertValidationFailure("EffectiveSchema", field);
    }

    [TestCase("short")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void It_rejects_malformed_hashes(string hash)
    {
        ChangeFingerprint(fingerprint => fingerprint with { EffectiveSchemaHash = hash });
        AssertValidationFailure("EffectiveSchema", "EffectiveSchemaHash");
    }

    [TestCase(-1)]
    [TestCase(1)]
    public void It_rejects_invalid_or_unequal_resource_counts(short count)
    {
        ChangeFingerprint(fingerprint => fingerprint with { ResourceKeyCount = count });
        AssertValidationFailure("EffectiveSchema", "ResourceKeyCount");
    }

    [Test]
    public void It_rejects_an_invalid_seed_length()
    {
        ChangeFingerprint(fingerprint => fingerprint with { ResourceKeySeedHash = [1] });
        AssertValidationFailure("EffectiveSchema", "ResourceKeySeedHash");
    }

    [Test]
    public void It_rejects_unequal_seed_bytes()
    {
        ChangeFingerprint(fingerprint => fingerprint with { ResourceKeySeedHash = new byte[32] });
        AssertValidationFailure("EffectiveSchema", "ResourceKeySeedHash");
    }

    [TestCase("ProjectName")]
    [TestCase("ResourceName")]
    [TestCase("ResourceVersion")]
    public void It_compares_resource_text_ordinally(string field)
    {
        var resource = _stored.ResourceKeys[0];
        var changed = field switch
        {
            "ProjectName" => resource with { ProjectName = "ed-fi" },
            "ResourceName" => resource with { ResourceName = "student" },
            _ => resource with { ResourceVersion = "5.1.0 " },
        };
        _stored = _stored with { ResourceKeys = [changed, _stored.ResourceKeys[1]] };
        AssertValidationFailure("ResourceKey", field);
    }

    [Test]
    public void It_rejects_missing_resource_ids()
    {
        _stored = _stored with { ResourceKeys = [_stored.ResourceKeys[0]] };
        AssertValidationFailure("ResourceKey", "ResourceKeyId");
    }

    [Test]
    public void It_rejects_extra_resource_ids()
    {
        _stored = _stored with { ResourceKeys = [.. _stored.ResourceKeys, new(3, "X", "Y", "1")] };
        AssertValidationFailure("ResourceKey", "ResourceKeyId");
    }

    [Test]
    public void It_rejects_duplicate_resource_ids()
    {
        _stored = _stored with { ResourceKeys = [.. _stored.ResourceKeys, _stored.ResourceKeys[0]] };
        AssertValidationFailure("ResourceKey", "ResourceKeyId");
    }

    [TestCase("ProjectEndpointName")]
    [TestCase("ProjectName")]
    [TestCase("ProjectVersion")]
    [TestCase("IsExtensionProject")]
    public void It_compares_component_fields_exactly(string field)
    {
        var component = _stored.SchemaComponents[0];
        var changedPayload = field switch
        {
            "ProjectEndpointName" => component.Payload with { ProjectEndpointName = "Ed-Fi" },
            "ProjectName" => component.Payload with { ProjectName = "ed-fi" },
            "ProjectVersion" => component.Payload with { ProjectVersion = "5.1.0 " },
            _ => component.Payload with { IsExtensionProject = true },
        };
        _stored = _stored with
        {
            SchemaComponents = [component with { Payload = changedPayload }, _stored.SchemaComponents[1]],
        };
        AssertValidationFailure("SchemaComponent", field);
    }

    [Test]
    public void It_rejects_missing_component_endpoints()
    {
        _stored = _stored with { SchemaComponents = [_stored.SchemaComponents[0]] };
        AssertValidationFailure("SchemaComponent", "ProjectEndpointName");
    }

    [Test]
    public void It_rejects_extra_component_endpoints()
    {
        _stored = _stored with
        {
            SchemaComponents = [.. _stored.SchemaComponents, new(OldHash, new("extra", "X", "1", false))],
        };
        AssertValidationFailure("SchemaComponent", "ProjectEndpointName");
    }

    [Test]
    public void It_rejects_duplicate_component_endpoints()
    {
        _stored = _stored with
        {
            SchemaComponents = [.. _stored.SchemaComponents, _stored.SchemaComponents[0]],
        };
        AssertValidationFailure("SchemaComponent", "ProjectEndpointName");
    }

    [Test]
    public void It_rejects_a_child_associated_with_another_hash()
    {
        _stored = _stored with
        {
            SchemaComponents =
            [
                _stored.SchemaComponents[0] with
                {
                    EffectiveSchemaHash = TargetHash,
                },
                _stored.SchemaComponents[1],
            ],
        };
        AssertValidationFailure("SchemaComponent", "EffectiveSchemaHash");
    }

    [Test]
    public void It_does_not_reveal_client_or_stored_payloads()
    {
        _stored = _stored with
        {
            ResourceKeys =
            [
                _stored.ResourceKeys[0] with
                {
                    ResourceName = "secret\r\nvalue",
                },
                _stored.ResourceKeys[1],
            ],
        };
        var exception = InvokingValidation().Should().Throw<SchemaRestampException>().Which;
        exception.Failure.Should().Be(SchemaRestampFailure.Validation);
        exception.Message.Should().Contain("ResourceKey").And.Contain("ResourceName");
        exception
            .Message.Should()
            .NotContain("secret")
            .And.NotContain("value")
            .And.NotContain("\r")
            .And.NotContain("\n");
    }

    private void ChangeFingerprint(Func<SchemaRestampFingerprint, SchemaRestampFingerprint> change) =>
        _stored = _stored with { Fingerprints = [change(_stored.Fingerprints[0])] };

    private Action InvokingValidation() =>
        () => SchemaRestampValidator.ValidateOrThrow(_stored, _target, _logger);

    private void AssertValidationFailure(string table, string field)
    {
        var exception = InvokingValidation().Should().Throw<SchemaRestampException>().Which;
        exception.Failure.Should().Be(SchemaRestampFailure.Validation);
        exception.Message.Should().Contain(table).And.Contain(field);
    }
}
