// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_HydratedRowOrdering
{
    [Test]
    public void It_should_leave_empty_row_lists_unchanged()
    {
        List<Row> rows = [];

        var act = () => HydratedRowOrdering.EnsureOrdinalOrder(rows, row => row.Ordinal);

        act.Should().NotThrow();
        rows.Should().BeEmpty();
    }

    [Test]
    public void It_should_leave_already_ordered_rows_in_place()
    {
        List<Row> rows = [new("first", 1), new("second", 2), new("third", 3)];

        HydratedRowOrdering.EnsureOrdinalOrder(rows, row => row.Ordinal);

        rows.Select(row => row.Name).Should().Equal("first", "second", "third");
    }

    [Test]
    public void It_should_sort_rows_by_resolved_ordinal_after_ordering_drift()
    {
        List<Row> rows = [new("third", 3), new("first", 1), new("second", 2)];

        HydratedRowOrdering.EnsureOrdinalOrder(rows, row => row.Ordinal);

        rows.Select(row => row.Name).Should().Equal("first", "second", "third");
    }

    private sealed record Row(string Name, int Ordinal);
}
