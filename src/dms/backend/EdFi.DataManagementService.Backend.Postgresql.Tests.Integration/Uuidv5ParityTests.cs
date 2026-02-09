// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Be.Vlaanderen.Basisregisters.Generators.Guid;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Parity tests that verify the PostgreSQL dms.uuidv5() helper function produces
/// byte-for-byte identical output to the .NET Core Deterministic.Create() implementation
/// used by ReferentialIdCalculator. Covers the "Parity test (Core vs DB compute)"
/// requirement from the referential-identity-test-plan.
/// </summary>
public abstract class Uuidv5ParityTestBase
{
    /// <summary>
    /// The Ed-Fi namespace UUID used by ReferentialIdCalculator.
    /// </summary>
    public static readonly Guid EdFiNamespace = new("edf1edf1-3df1-3df1-3df1-3df1edf1edf1");

    /// <summary>
    /// The RFC 4122 DNS namespace UUID, used as an alternate namespace for testing.
    /// </summary>
    protected static readonly Guid DnsNamespace = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    private static readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(
        Configuration.DatabaseConnectionString
    );

    protected Guid CoreResult { get; set; }
    protected Guid PgResult { get; set; }

    protected static Guid ComputeCore(Guid namespaceUuid, string name)
    {
        return Deterministic.Create(namespaceUuid, name);
    }

    public static async Task<Guid> ComputePostgres(Guid namespaceUuid, string name)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""SELECT dms.uuidv5($1::uuid, $2::text)""", connection);
        cmd.Parameters.AddWithValue(namespaceUuid);
        cmd.Parameters.AddWithValue(name);

        var result = await cmd.ExecuteScalarAsync();
        return (Guid)result!;
    }
}

[TestFixture]
public class Given_Simple_Ascii_Name : Uuidv5ParityTestBase
{
    private const string Name = "hello world";

    [SetUp]
    public async Task Setup()
    {
        CoreResult = ComputeCore(EdFiNamespace, Name);
        PgResult = await ComputePostgres(EdFiNamespace, Name);
    }

    [Test]
    public void It_should_produce_matching_uuids()
    {
        PgResult.Should().Be(CoreResult);
    }

    [Test]
    public void It_should_produce_a_version_5_uuid()
    {
        // Version nibble is bits 12-15 of time_hi_and_version (byte 6, high nibble)
        var bytes = CoreResult.ToByteArray();
        // .NET Guid.ToByteArray() uses mixed-endian; byte index 7 holds time_hi high byte
        var version = (bytes[7] >> 4) & 0x0F;
        version.Should().Be(5);
    }
}

[TestFixture]
public class Given_EdFi_Identity_String : Uuidv5ParityTestBase
{
    // Mimics a realistic ReferentialIdCalculator input:
    // "{ProjectName}{ResourceName}{DocumentIdentityString}"
    private const string Name = "Ed-FiSession$$.sessionName=Spring 2025";

    [SetUp]
    public async Task Setup()
    {
        CoreResult = ComputeCore(EdFiNamespace, Name);
        PgResult = await ComputePostgres(EdFiNamespace, Name);
    }

    [Test]
    public void It_should_produce_matching_uuids()
    {
        PgResult.Should().Be(CoreResult);
    }
}

[TestFixture]
public class Given_Multi_Part_Identity_String : Uuidv5ParityTestBase
{
    // Multi-element identity with '#' separator as used by DocumentIdentityString
    private const string Name =
        "Ed-FiCourseOffering$$.localCourseCode=ALG-1#$$.sessionReference.sessionName=Spring 2025";

    [SetUp]
    public async Task Setup()
    {
        CoreResult = ComputeCore(EdFiNamespace, Name);
        PgResult = await ComputePostgres(EdFiNamespace, Name);
    }

    [Test]
    public void It_should_produce_matching_uuids()
    {
        PgResult.Should().Be(CoreResult);
    }
}

[TestFixture]
public class Given_Empty_Name
{
    /// <summary>
    /// The Basisregisters Deterministic.Create() library rejects empty strings,
    /// so empty name is not a valid parity scenario. This test documents that
    /// the PG function still produces a deterministic result for empty input,
    /// which is acceptable since Core will never call it with empty input.
    /// </summary>
    [Test]
    public void It_should_throw_in_core()
    {
        var act = () => Deterministic.Create(Uuidv5ParityTestBase.EdFiNamespace, string.Empty);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task It_should_still_produce_a_result_in_postgres()
    {
        var result = await Uuidv5ParityTestBase.ComputePostgres(
            Uuidv5ParityTestBase.EdFiNamespace,
            string.Empty
        );
        result.Should().NotBeEmpty();
    }
}

[TestFixture]
public class Given_Unicode_Name : Uuidv5ParityTestBase
{
    // Non-ASCII: accented characters, CJK, and emoji
    private const string Name = "caf\u00e9 \u00fc\u00f1\u00ee\u00e7\u00f6d\u00e9 \u5b66\u6821 \U0001F3EB";

    [SetUp]
    public async Task Setup()
    {
        CoreResult = ComputeCore(EdFiNamespace, Name);
        PgResult = await ComputePostgres(EdFiNamespace, Name);
    }

    [Test]
    public void It_should_produce_matching_uuids()
    {
        PgResult.Should().Be(CoreResult);
    }
}

[TestFixture]
public class Given_Whitespace_And_Special_Characters : Uuidv5ParityTestBase
{
    // Tabs, newlines, leading/trailing spaces, and punctuation
    private const string Name = "  \thello\nworld\r\n  !@#$%^&*()  ";

    [SetUp]
    public async Task Setup()
    {
        CoreResult = ComputeCore(EdFiNamespace, Name);
        PgResult = await ComputePostgres(EdFiNamespace, Name);
    }

    [Test]
    public void It_should_produce_matching_uuids()
    {
        PgResult.Should().Be(CoreResult);
    }
}

[TestFixture]
public class Given_Numeric_Identity_Values : Uuidv5ParityTestBase
{
    // Numeric identity parts (school IDs, dates, decimals, leading zeros)
    private const string Name = "Ed-FiLocation$$.schoolReference.schoolId=255901001";

    [SetUp]
    public async Task Setup()
    {
        CoreResult = ComputeCore(EdFiNamespace, Name);
        PgResult = await ComputePostgres(EdFiNamespace, Name);
    }

    [Test]
    public void It_should_produce_matching_uuids()
    {
        PgResult.Should().Be(CoreResult);
    }
}

[TestFixture]
public class Given_Alternate_Namespace : Uuidv5ParityTestBase
{
    // Verifies parity is not hard-coded to the Ed-Fi namespace
    private const string Name = "www.example.com";

    [SetUp]
    public async Task Setup()
    {
        CoreResult = ComputeCore(DnsNamespace, Name);
        PgResult = await ComputePostgres(DnsNamespace, Name);
    }

    [Test]
    public void It_should_produce_matching_uuids()
    {
        PgResult.Should().Be(CoreResult);
    }
}

[TestFixture]
public class Given_Long_Name : Uuidv5ParityTestBase
{
    private static readonly string _longName = new('x', 10_000);

    [SetUp]
    public async Task Setup()
    {
        CoreResult = ComputeCore(EdFiNamespace, _longName);
        PgResult = await ComputePostgres(EdFiNamespace, _longName);
    }

    [Test]
    public void It_should_produce_matching_uuids()
    {
        PgResult.Should().Be(CoreResult);
    }
}

[TestFixture]
public class Given_Formatting_Edge_Cases : Uuidv5ParityTestBase
{
    // Dates, decimals, leading zeros, trailing whitespace — all in one string
    private const string Name =
        "Ed-FiGradingPeriod$$.beginDate=2025-01-01#$$.gradingPeriodDescriptor=uri://ed-fi.org/GradingPeriodDescriptor#First Six Weeks#$$.schoolId=00255901";

    [SetUp]
    public async Task Setup()
    {
        CoreResult = ComputeCore(EdFiNamespace, Name);
        PgResult = await ComputePostgres(EdFiNamespace, Name);
    }

    [Test]
    public void It_should_produce_matching_uuids()
    {
        PgResult.Should().Be(CoreResult);
    }
}

/// <summary>
/// Determinism test: calling the function twice with the same inputs produces the same result.
/// </summary>
[TestFixture]
public class Given_Same_Input_Called_Twice
{
    private Guid _firstCall;
    private Guid _secondCall;

    private const string Name = "determinism check";

    [SetUp]
    public async Task Setup()
    {
        _firstCall = await Uuidv5ParityTestBase.ComputePostgres(Uuidv5ParityTestBase.EdFiNamespace, Name);
        _secondCall = await Uuidv5ParityTestBase.ComputePostgres(Uuidv5ParityTestBase.EdFiNamespace, Name);
    }

    [Test]
    public void It_should_return_identical_results()
    {
        _secondCall.Should().Be(_firstCall);
    }
}
