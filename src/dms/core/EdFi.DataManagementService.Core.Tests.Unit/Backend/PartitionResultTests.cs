// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

/// <summary>
/// Partition boundaries cross the backend seam as typed ranges. Token text is created only at Core's
/// HTTP contract boundary, so these shapes must have nowhere to carry it.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_A_Partition_Success_Result
{
    private PartitionResult.PartitionSuccess _result = null!;

    [SetUp]
    public void Setup()
    {
        _result = new PartitionResult.PartitionSuccess([
            new CursorRange(1, 2500),
            new CursorRange(2501, long.MaxValue),
        ]);
    }

    [Test]
    public void It_carries_the_typed_ranges_in_order()
    {
        _result.Ranges.Should().Equal(new CursorRange(1, 2500), new CursorRange(2501, long.MaxValue));
    }

    [Test]
    public void It_is_a_partition_result()
    {
        _result.Should().BeAssignableTo<PartitionResult>();
    }

    [Test]
    public void It_exposes_ranges_as_a_read_only_list_of_cursor_ranges()
    {
        typeof(PartitionResult.PartitionSuccess)
            .GetProperty(nameof(PartitionResult.PartitionSuccess.Ranges))!
            .PropertyType.Should()
            .Be<IReadOnlyList<CursorRange>>();
    }

    [Test]
    public void It_represents_no_accessible_candidates_as_an_empty_range_list()
    {
        new PartitionResult.PartitionSuccess([]).Ranges.Should().BeEmpty();
    }
}

[TestFixture]
[Parallelizable]
public class Given_The_Cursor_Paging_And_Partition_Contracts
{
    /// <summary>
    /// The contracts DMS-1383 owns. Deliberately not the whole result hierarchy: a later partition
    /// failure diagnostic may legitimately carry strings.
    /// </summary>
    private static readonly Type[] _ownedContracts =
    [
        typeof(CursorRange),
        typeof(PageSize),
        typeof(CollectionPaging),
        typeof(CollectionPaging.Traditional),
        typeof(CollectionPaging.Cursor),
        typeof(PartitionResult.PartitionSuccess),
    ];

    [TestCaseSource(nameof(_ownedContracts))]
    public void It_exposes_no_token_text(Type contract)
    {
        contract
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should()
            .NotContain(property => property.PropertyType == typeof(string));
    }

    [Test]
    public void It_closes_the_partition_result_hierarchy()
    {
        typeof(PartitionResult)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Should()
            .BeEmpty();

        // Asserts the closure property rather than the arm count, so a later story can add a failure
        // alternative without this test failing for the wrong reason.
        typeof(PartitionResult)
            .Assembly.GetTypes()
            .Where(type => typeof(PartitionResult).IsAssignableFrom(type) && type != typeof(PartitionResult))
            .Should()
            .Contain(typeof(PartitionResult.PartitionSuccess))
            .And.OnlyContain(type => type.IsSealed && type.DeclaringType == typeof(PartitionResult));
    }
}
