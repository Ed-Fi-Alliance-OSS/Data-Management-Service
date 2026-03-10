// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class Given_ValidateResourceKeysOrThrow_With_Duplicate_Actual_ResourceKeyId
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();

        List<ResourceKeyRow> actualRows =
        [
            new(1, "Ed-Fi", "Student", "5.1.0"),
            new(1, "Ed-Fi", "School", "5.1.0"),
        ];

        List<ResourceKeyEntry> expectedKeys =
        [
            new(1, new QualifiedResourceName("Ed-Fi", "Student"), "5.1.0", false),
        ];

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateResourceKeysOrThrow(actualRows, expectedKeys, logger)
        );
    }

    [Test]
    public void It_throws_InvalidOperationException()
    {
        _exception.Should().NotBeNull();
    }

    [Test]
    public void It_includes_duplicate_key_in_message()
    {
        _exception!.Message.Should().Contain("Duplicate").And.Contain("ResourceKeyId").And.Contain("1");
    }
}

[TestFixture]
public class Given_ValidateResourceKeysOrThrow_With_Duplicate_Expected_ResourceKeyId
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();

        List<ResourceKeyRow> actualRows = [new(1, "Ed-Fi", "Student", "5.1.0")];

        List<ResourceKeyEntry> expectedKeys =
        [
            new(1, new QualifiedResourceName("Ed-Fi", "Student"), "5.1.0", false),
            new(1, new QualifiedResourceName("Ed-Fi", "School"), "5.1.0", false),
        ];

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateResourceKeysOrThrow(actualRows, expectedKeys, logger)
        );
    }

    [Test]
    public void It_throws_InvalidOperationException()
    {
        _exception.Should().NotBeNull();
    }

    [Test]
    public void It_includes_duplicate_key_in_message()
    {
        _exception!.Message.Should().Contain("Duplicate").And.Contain("ResourceKeyId").And.Contain("1");
    }
}

[TestFixture]
public class Given_ValidateSchemaComponentsOrThrow_With_Duplicate_Actual_ProjectEndpointName
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();

        List<SchemaComponentRow> actualRows =
        [
            new("ed-fi", "Ed-Fi", "5.1.0", false),
            new("ed-fi", "Ed-Fi", "5.2.0", false),
        ];

        List<SchemaComponentInfo> expectedComponents = [new("ed-fi", "Ed-Fi", "5.1.0", false, "abc123")];

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateSchemaComponentsOrThrow(actualRows, expectedComponents, logger)
        );
    }

    [Test]
    public void It_throws_InvalidOperationException()
    {
        _exception.Should().NotBeNull();
    }

    [Test]
    public void It_includes_duplicate_key_in_message()
    {
        _exception!
            .Message.Should()
            .Contain("Duplicate")
            .And.Contain("ProjectEndpointName")
            .And.Contain("ed-fi");
    }
}

[TestFixture]
public class Given_ValidateSchemaComponentsOrThrow_With_Duplicate_Expected_ProjectEndpointName
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();

        List<SchemaComponentRow> actualRows = [new("ed-fi", "Ed-Fi", "5.1.0", false)];

        List<SchemaComponentInfo> expectedComponents =
        [
            new("ed-fi", "Ed-Fi", "5.1.0", false, "abc123"),
            new("ed-fi", "Ed-Fi", "5.2.0", false, "def456"),
        ];

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateSchemaComponentsOrThrow(actualRows, expectedComponents, logger)
        );
    }

    [Test]
    public void It_throws_InvalidOperationException()
    {
        _exception.Should().NotBeNull();
    }

    [Test]
    public void It_includes_duplicate_key_in_message()
    {
        _exception!
            .Message.Should()
            .Contain("Duplicate")
            .And.Contain("ProjectEndpointName")
            .And.Contain("ed-fi");
    }
}

[TestFixture]
public class Given_ValidateResourceKeysOrThrow_With_Punctuation_In_Values
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();

        List<ResourceKeyRow> actualRows = [new(1, "Ed-Fi", "Student (v5)", "5.1.0")];

        List<ResourceKeyEntry> expectedKeys =
        [
            new(1, new QualifiedResourceName("Ed-Fi", "Student [v5]"), "5.1.0", false),
        ];

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateResourceKeysOrThrow(actualRows, expectedKeys, logger)
        );
    }

    [Test]
    public void It_throws_InvalidOperationException()
    {
        _exception.Should().NotBeNull();
    }

    [Test]
    public void It_preserves_punctuation_in_diff_message()
    {
        _exception!.Message.Should().Contain("Student (v5)").And.Contain("Student [v5]");
    }
}

[TestFixture]
public class Given_ValidateSchemaComponentsOrThrow_With_Control_Chars_In_Values
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();

        List<SchemaComponentRow> actualRows = [new("ed-fi", "Evil\nProject", "5.1.0", false)];

        List<SchemaComponentInfo> expectedComponents =
        [
            new("ed-fi", "GoodProject", "5.1.0", false, "abc123"),
        ];

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateSchemaComponentsOrThrow(actualRows, expectedComponents, logger)
        );
    }

    [Test]
    public void It_throws_InvalidOperationException()
    {
        _exception.Should().NotBeNull();
    }

    [Test]
    public void It_strips_control_chars_from_diff_message()
    {
        _exception!.Message.Should().Contain("EvilProject").And.NotContain("Evil\nProject");
    }
}

internal static class EffectiveSchemaValidationTestData
{
    internal static readonly byte[] ValidResourceKeySeedHash = Enumerable
        .Range(0, 32)
        .Select(i => (byte)i)
        .ToArray();

    internal static EffectiveSchemaInfo BuildExpectedSchema() =>
        new(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: new string('a', 64),
            ResourceKeyCount: 42,
            ResourceKeySeedHash: ValidResourceKeySeedHash,
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.1.0", false, new string('b', 64)),
            ],
            ResourceKeysInIdOrder:
            [
                new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "Student"), "5.1.0", false),
            ]
        );
}

[TestFixture]
public class Given_ValidateEffectiveSchemaOrThrow_With_Matching_Values
{
    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();
        var expectedSchema = EffectiveSchemaValidationTestData.BuildExpectedSchema();

        SeedValidator.ValidateEffectiveSchemaOrThrow(
            1,
            expectedSchema.ApiSchemaFormatVersion,
            expectedSchema.EffectiveSchemaHash,
            expectedSchema.ResourceKeyCount,
            expectedSchema.ResourceKeySeedHash,
            expectedSchema,
            logger
        );
    }

    [Test]
    public void It_does_not_throw()
    {
        // If we reached here, no exception was thrown
        Assert.Pass();
    }
}

[TestFixture]
public class Given_ValidateEffectiveSchemaOrThrow_With_Mismatched_ResourceKeyCount
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();
        var expectedSchema = EffectiveSchemaValidationTestData.BuildExpectedSchema();

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateEffectiveSchemaOrThrow(
                1,
                expectedSchema.ApiSchemaFormatVersion,
                expectedSchema.EffectiveSchemaHash,
                expectedSchema.ResourceKeyCount,
                expectedSchema.ResourceKeySeedHash,
                expectedSchema with
                {
                    ResourceKeyCount = 50,
                },
                logger
            )
        );
    }

    [Test]
    public void It_throws_InvalidOperationException()
    {
        _exception.Should().NotBeNull();
    }

    [Test]
    public void It_includes_both_values_in_message()
    {
        _exception!.Message.Should().Contain("stored=42").And.Contain("expected=50");
    }
}

[TestFixture]
public class Given_ValidateEffectiveSchemaOrThrow_With_Mismatched_ResourceKeySeedHash
{
    private InvalidOperationException? _exception;
    private byte[] _storedHash = null!;
    private byte[] _expectedHash = null!;

    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();
        var expectedSchema = EffectiveSchemaValidationTestData.BuildExpectedSchema();
        _storedHash = expectedSchema.ResourceKeySeedHash;
        _expectedHash = SHA256.HashData([4, 5, 6]);

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateEffectiveSchemaOrThrow(
                1,
                expectedSchema.ApiSchemaFormatVersion,
                expectedSchema.EffectiveSchemaHash,
                expectedSchema.ResourceKeyCount,
                _storedHash,
                expectedSchema with
                {
                    ResourceKeySeedHash = _expectedHash,
                },
                logger
            )
        );
    }

    [Test]
    public void It_throws_InvalidOperationException()
    {
        _exception.Should().NotBeNull();
    }

    [Test]
    public void It_includes_hex_hashes_in_message()
    {
        _exception!
            .Message.Should()
            .Contain(Convert.ToHexString(_storedHash))
            .And.Contain(Convert.ToHexString(_expectedHash));
    }
}

[TestFixture]
public class Given_ValidateEffectiveSchemaOrThrow_With_Both_Mismatched
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();
        var expectedSchema = EffectiveSchemaValidationTestData.BuildExpectedSchema();
        var expectedHash = SHA256.HashData([4, 5, 6]);

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateEffectiveSchemaOrThrow(
                1,
                expectedSchema.ApiSchemaFormatVersion,
                expectedSchema.EffectiveSchemaHash,
                expectedSchema.ResourceKeyCount,
                expectedSchema.ResourceKeySeedHash,
                expectedSchema with
                {
                    ResourceKeyCount = 50,
                    ResourceKeySeedHash = expectedHash,
                },
                logger
            )
        );
    }

    [Test]
    public void It_throws_InvalidOperationException()
    {
        _exception.Should().NotBeNull();
    }

    [Test]
    public void It_includes_both_issues_in_message()
    {
        _exception!.Message.Should().Contain("ResourceKeyCount").And.Contain("ResourceKeySeedHash");
    }
}

[TestFixture]
public class Given_ValidateEffectiveSchemaOrThrow_With_An_Empty_Stored_ApiSchemaFormatVersion
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();
        var expectedSchema = EffectiveSchemaValidationTestData.BuildExpectedSchema();

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateEffectiveSchemaOrThrow(
                1,
                " ",
                expectedSchema.EffectiveSchemaHash,
                expectedSchema.ResourceKeyCount,
                expectedSchema.ResourceKeySeedHash,
                expectedSchema,
                logger
            )
        );
    }

    [Test]
    public void It_rejects_runtime_invalid_stored_metadata()
    {
        _exception!.Message.Should().Contain("dms.EffectiveSchema.ApiSchemaFormatVersion must not be empty.");
    }
}

[TestFixture]
public class Given_ValidateEffectiveSchemaOrThrow_With_An_Invalid_Expected_EffectiveSchemaHash
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();
        var expectedSchema = EffectiveSchemaValidationTestData.BuildExpectedSchema();

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateEffectiveSchemaOrThrow(
                1,
                expectedSchema.ApiSchemaFormatVersion,
                expectedSchema.EffectiveSchemaHash,
                expectedSchema.ResourceKeyCount,
                expectedSchema.ResourceKeySeedHash,
                expectedSchema with
                {
                    EffectiveSchemaHash = $"{new string('a', 63)}G",
                },
                logger
            )
        );
    }

    [Test]
    public void It_rejects_runtime_invalid_expected_metadata()
    {
        _exception!
            .Message.Should()
            .Contain(
                "Expected provisioning metadata invalid: dms.EffectiveSchema.EffectiveSchemaHash must be 64 lowercase hex characters."
            );
    }
}

[TestFixture]
public class Given_ValidateResourceKeysOrThrow_With_Multiple_Mismatches_Reports_In_Sorted_Order
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();

        List<ResourceKeyRow> actualRows =
        [
            new(99, "Ed-Fi", "Surprise", "5.1.0"),
            new(10, "Ed-Fi", "WrongName10", "5.1.0"),
            new(5, "Ed-Fi", "WrongName5", "5.1.0"),
        ];

        List<ResourceKeyEntry> expectedKeys =
        [
            new(10, new QualifiedResourceName("Ed-Fi", "School"), "5.1.0", false),
            new(5, new QualifiedResourceName("Ed-Fi", "Student"), "5.1.0", false),
            new(1, new QualifiedResourceName("Ed-Fi", "Section"), "5.1.0", false),
        ];

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateResourceKeysOrThrow(actualRows, expectedKeys, logger)
        );
    }

    [Test]
    public void It_throws_InvalidOperationException()
    {
        _exception.Should().NotBeNull();
    }

    [Test]
    public void It_lists_missing_keys_in_ascending_order()
    {
        _exception!.Message.Should().Contain("Missing rows (expected but not in database): [1]");
    }

    [Test]
    public void It_lists_unexpected_keys_in_ascending_order()
    {
        _exception!.Message.Should().Contain("Unexpected rows (in database but not expected): [99]");
    }

    [Test]
    public void It_lists_modified_rows_in_ascending_key_order()
    {
        var message = _exception!.Message;
        message
            .IndexOf("ResourceKeyId=5", StringComparison.Ordinal)
            .Should()
            .BeLessThan(message.IndexOf("ResourceKeyId=10", StringComparison.Ordinal));
    }
}

[TestFixture]
public class Given_ValidateSchemaComponentsOrThrow_With_Multiple_Mismatches_Reports_In_Sorted_Order
{
    private InvalidOperationException? _exception;

    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();

        List<SchemaComponentRow> actualRows =
        [
            new("tpdm", "TPDM", "2.0.0", false),
            new("ed-fi", "Ed-Fi", "6.0.0", false),
            new("zzz-extra", "Extra", "1.0.0", false),
        ];

        List<SchemaComponentInfo> expectedComponents =
        [
            new("tpdm", "TPDM", "1.0.0", false, "hash1"),
            new("ed-fi", "Ed-Fi", "5.1.0", false, "hash2"),
            new("abc-missing", "ABC", "1.0.0", false, "hash3"),
        ];

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateSchemaComponentsOrThrow(actualRows, expectedComponents, logger)
        );
    }

    [Test]
    public void It_throws_InvalidOperationException()
    {
        _exception.Should().NotBeNull();
    }

    [Test]
    public void It_lists_missing_keys_in_ordinal_order()
    {
        _exception!.Message.Should().Contain("Missing rows (expected but not in database): [abc-missing]");
    }

    [Test]
    public void It_lists_unexpected_keys_in_ordinal_order()
    {
        _exception!.Message.Should().Contain("Unexpected rows (in database but not expected): [zzz-extra]");
    }

    [Test]
    public void It_lists_modified_rows_in_ordinal_order()
    {
        var message = _exception!.Message;
        message
            .IndexOf("ProjectEndpointName=ed-fi", StringComparison.Ordinal)
            .Should()
            .BeLessThan(message.IndexOf("ProjectEndpointName=tpdm", StringComparison.Ordinal));
    }
}
