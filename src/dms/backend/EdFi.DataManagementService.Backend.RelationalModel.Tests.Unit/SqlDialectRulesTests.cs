// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for pgsql dialect rules with identifier at the limit.
/// </summary>
[TestFixture]
public class Given_PgsqlDialectRules_With_Identifier_At_The_Limit
{
    private string _identifier = default!;
    private string _result = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var rules = new PgsqlDialectRules();
        _identifier = new string('a', rules.MaxIdentifierLength);
        _result = rules.ShortenIdentifier(_identifier);
    }

    /// <summary>
    /// It should leave the identifier unchanged.
    /// </summary>
    [Test]
    public void It_should_leave_the_identifier_unchanged()
    {
        _result.Should().Be(_identifier);
    }
}

/// <summary>
/// Test fixture for pgsql dialect rules with identifier above the limit.
/// </summary>
[TestFixture]
public class Given_PgsqlDialectRules_With_Identifier_Above_The_Limit
{
    private string _identifier = default!;
    private string _result = default!;
    private string _secondResult = default!;
    private int _maxLength;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var rules = new PgsqlDialectRules();
        _maxLength = rules.MaxIdentifierLength;
        _identifier = new string('a', _maxLength + 1);
        _result = rules.ShortenIdentifier(_identifier);
        _secondResult = rules.ShortenIdentifier(_identifier);
    }

    /// <summary>
    /// It should shorten deterministically.
    /// </summary>
    [Test]
    public void It_should_shorten_deterministically()
    {
        _result.Should().Be(_secondResult);
    }

    /// <summary>
    /// It should produce an identifier within the byte limit.
    /// </summary>
    [Test]
    public void It_should_produce_an_identifier_within_the_byte_limit()
    {
        Encoding.UTF8.GetByteCount(_result).Should().BeLessOrEqualTo(_maxLength);
    }

    /// <summary>
    /// It should change the identifier.
    /// </summary>
    [Test]
    public void It_should_change_the_identifier()
    {
        _result.Should().NotBe(_identifier);
    }
}

/// <summary>
/// Test fixture for pgsql dialect rules with a multibyte identifier above the byte limit.
/// </summary>
[TestFixture]
public class Given_PgsqlDialectRules_With_A_Multibyte_Identifier_Above_The_Byte_Limit
{
    private string _identifier = default!;
    private string _result = default!;
    private int _maxLength;
    private int _byteCount;
    private int _characterCount;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var rules = new PgsqlDialectRules();
        _maxLength = rules.MaxIdentifierLength;
        _identifier = new string('\u00E9', 40);
        _byteCount = Encoding.UTF8.GetByteCount(_identifier);
        _characterCount = _identifier.Length;
        _result = rules.ShortenIdentifier(_identifier);
    }

    /// <summary>
    /// It should consider utf8 byte length.
    /// </summary>
    [Test]
    public void It_should_consider_utf8_byte_length()
    {
        _characterCount.Should().BeLessOrEqualTo(_maxLength);
        _byteCount.Should().BeGreaterThan(_maxLength);
        _result.Should().NotBe(_identifier);
    }

    /// <summary>
    /// It should produce an identifier within the byte limit.
    /// </summary>
    [Test]
    public void It_should_produce_an_identifier_within_the_byte_limit()
    {
        Encoding.UTF8.GetByteCount(_result).Should().BeLessOrEqualTo(_maxLength);
    }
}

/// <summary>
/// Test fixture for mssql dialect rules with identifier at the limit.
/// </summary>
[TestFixture]
public class Given_MssqlDialectRules_With_Identifier_At_The_Limit
{
    private string _identifier = default!;
    private string _result = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var rules = new MssqlDialectRules();
        _identifier = new string('a', rules.MaxIdentifierLength);
        _result = rules.ShortenIdentifier(_identifier);
    }

    /// <summary>
    /// It should leave the identifier unchanged.
    /// </summary>
    [Test]
    public void It_should_leave_the_identifier_unchanged()
    {
        _result.Should().Be(_identifier);
    }
}

/// <summary>
/// Test fixture for mssql dialect rules with identifier above the limit.
/// </summary>
[TestFixture]
public class Given_MssqlDialectRules_With_Identifier_Above_The_Limit
{
    private string _identifier = default!;
    private string _result = default!;
    private string _secondResult = default!;
    private int _maxLength;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var rules = new MssqlDialectRules();
        _maxLength = rules.MaxIdentifierLength;
        _identifier = new string('a', _maxLength + 1);
        _result = rules.ShortenIdentifier(_identifier);
        _secondResult = rules.ShortenIdentifier(_identifier);
    }

    /// <summary>
    /// It should shorten deterministically.
    /// </summary>
    [Test]
    public void It_should_shorten_deterministically()
    {
        _result.Should().Be(_secondResult);
    }

    /// <summary>
    /// It should produce an identifier within the character limit.
    /// </summary>
    [Test]
    public void It_should_produce_an_identifier_within_the_character_limit()
    {
        _result.Length.Should().BeLessOrEqualTo(_maxLength);
    }

    /// <summary>
    /// It should change the identifier.
    /// </summary>
    [Test]
    public void It_should_change_the_identifier()
    {
        _result.Should().NotBe(_identifier);
    }
}

/// <summary>
/// Test fixture for mssql dialect rules with a multibyte identifier within the character limit.
/// </summary>
[TestFixture]
public class Given_MssqlDialectRules_With_A_Multibyte_Identifier_Within_The_Character_Limit
{
    private string _identifier = default!;
    private string _result = default!;
    private int _maxLength;
    private int _byteCount;
    private int _characterCount;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var rules = new MssqlDialectRules();
        _maxLength = rules.MaxIdentifierLength;
        _identifier = new string('\u00E9', 100);
        _byteCount = Encoding.UTF8.GetByteCount(_identifier);
        _characterCount = _identifier.Length;
        _result = rules.ShortenIdentifier(_identifier);
    }

    /// <summary>
    /// It should consider character length.
    /// </summary>
    [Test]
    public void It_should_consider_character_length()
    {
        _characterCount.Should().BeLessOrEqualTo(_maxLength);
        _byteCount.Should().BeGreaterThan(_maxLength);
        _result.Should().Be(_identifier);
    }
}
