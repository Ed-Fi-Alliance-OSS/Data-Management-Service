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

[TestFixture]
public class Given_ValidateEffectiveSchemaOrThrow_With_Matching_Values
{
    [SetUp]
    public void SetUp()
    {
        var logger = A.Fake<ILogger>();
        var hash = SHA256.HashData([1, 2, 3]);

        SeedValidator.ValidateEffectiveSchemaOrThrow(42, hash, 42, hash, logger);
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
        var hash = SHA256.HashData([1, 2, 3]);

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateEffectiveSchemaOrThrow(42, hash, 50, hash, logger)
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
        _storedHash = SHA256.HashData([1, 2, 3]);
        _expectedHash = SHA256.HashData([4, 5, 6]);

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateEffectiveSchemaOrThrow(42, _storedHash, 42, _expectedHash, logger)
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
        var storedHash = SHA256.HashData([1, 2, 3]);
        var expectedHash = SHA256.HashData([4, 5, 6]);

        _exception = Assert.Catch<InvalidOperationException>(() =>
            SeedValidator.ValidateEffectiveSchemaOrThrow(42, storedHash, 50, expectedHash, logger)
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
