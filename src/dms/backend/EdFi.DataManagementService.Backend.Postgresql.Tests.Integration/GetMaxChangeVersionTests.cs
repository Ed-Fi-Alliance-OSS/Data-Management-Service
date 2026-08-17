// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Verifies the emitted "dms"."GetMaxChangeVersion"() function tracks dms.ChangeVersionSequence.
/// </summary>
public abstract class GetMaxChangeVersionTestBase
{
    protected long Result { get; set; }

    public static async Task<long> CallFunction()
    {
        await using var connection = await Uuidv5ParityTestBase.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""SELECT "dms"."GetMaxChangeVersion"()""", connection);
        var result = await cmd.ExecuteScalarAsync();
        return (long)result!;
    }

    public static async Task AdvanceSequence(int times)
    {
        await using var connection = await Uuidv5ParityTestBase.DataSource.OpenConnectionAsync();
        for (var i = 0; i < times; i++)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT nextval('\"dms\".\"ChangeVersionSequence\"')",
                connection
            );
            await cmd.ExecuteScalarAsync();
        }
    }

    // Resets the sequence so a sibling fixture that advanced it does not contaminate
    // assertions that depend on the fresh start state.
    public static async Task ResetSequenceToStart()
    {
        await using var connection = await Uuidv5ParityTestBase.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "ALTER SEQUENCE \"dms\".\"ChangeVersionSequence\" RESTART",
            connection
        );
        await cmd.ExecuteNonQueryAsync();
    }
}

[TestFixture]
[NonParallelizable]
public class Given_Fresh_ChangeVersionSequence : GetMaxChangeVersionTestBase
{
    [SetUp]
    public async Task Setup()
    {
        await ResetSequenceToStart();
        Result = await CallFunction();
    }

    [Test]
    public void It_should_return_start_value_one()
    {
        // PG last_value on a sequence with is_called=false returns START WITH.
        // dms.ChangeVersionSequence is created with START WITH 1.
        Result.Should().Be(1L);
    }
}

[TestFixture]
[NonParallelizable]
public class Given_ChangeVersionSequence_Advanced_Three_Times : GetMaxChangeVersionTestBase
{
    [SetUp]
    public async Task Setup()
    {
        await ResetSequenceToStart();
        await AdvanceSequence(3);
        Result = await CallFunction();
    }

    [Test]
    public void It_should_return_the_last_allocated_value()
    {
        Result.Should().Be(3L);
    }
}

[TestFixture]
[NonParallelizable]
public class Given_Two_Consecutive_Calls_Without_Advancing : GetMaxChangeVersionTestBase
{
    private long _first;
    private long _second;

    [SetUp]
    public async Task Setup()
    {
        _first = await CallFunction();
        _second = await CallFunction();
    }

    [Test]
    public void It_should_return_the_same_value_on_both_calls()
    {
        _second.Should().Be(_first);
    }
}
