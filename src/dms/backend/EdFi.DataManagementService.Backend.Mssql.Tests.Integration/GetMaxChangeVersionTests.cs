// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Verifies the emitted [dms].[GetMaxChangeVersion]() function tracks dms.ChangeVersionSequence
/// via sys.sequences.current_value.
/// </summary>
public abstract class GetMaxChangeVersionTestBase
{
    protected long Result { get; set; }

    public static async Task<long> CallFunction()
    {
        await using SqlConnection connection = new(Uuidv5ParityTestBase.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand cmd = new("SELECT [dms].[GetMaxChangeVersion]()", connection);
        var result = await cmd.ExecuteScalarAsync();
        return (long)result!;
    }

    public static async Task AdvanceSequence(int times)
    {
        await using SqlConnection connection = new(Uuidv5ParityTestBase.ConnectionString);
        await connection.OpenAsync();
        for (var i = 0; i < times; i++)
        {
            await using SqlCommand cmd = new(
                "SELECT NEXT VALUE FOR [dms].[ChangeVersionSequence]",
                connection
            );
            await cmd.ExecuteScalarAsync();
        }
    }

    // Resets the sequence so a sibling fixture that advanced it does not contaminate
    // assertions that depend on the fresh start state.
    public static async Task ResetSequenceToStart()
    {
        await using SqlConnection connection = new(Uuidv5ParityTestBase.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand cmd = new("ALTER SEQUENCE [dms].[ChangeVersionSequence] RESTART", connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

[TestFixture]
[NonParallelizable]
[Category(MssqlCiShards.Shard4)]
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
        // Per Microsoft sys.sequences docs, current_value returns START WITH
        // if the sequence has never been used.
        Result.Should().Be(1L);
    }
}

[TestFixture]
[NonParallelizable]
[Category(MssqlCiShards.Shard4)]
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
[Category(MssqlCiShards.Shard4)]
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
