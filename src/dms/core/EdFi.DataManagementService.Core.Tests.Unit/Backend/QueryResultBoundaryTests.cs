// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

/// <summary>
/// The selected-keyset boundary is a value in its own right, not something inferred from the response
/// body. An empty body cannot distinguish a skipped or empty selection from concurrent deletion after
/// selection, which is why the two are independent.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_A_Query_Success_Result
{
    [Test]
    public void It_has_no_selected_keyset_boundary_by_default()
    {
        new QueryResult.QuerySuccess([], 0).HighestSelectedDocumentId.Should().BeNull();
    }

    [Test]
    public void It_allows_documents_without_a_selected_keyset_boundary()
    {
        QueryResult.QuerySuccess success = new([JsonValue.Create(1)], null);

        success.EdfiDocs.Should().HaveCount(1);
        success.HighestSelectedDocumentId.Should().BeNull();
    }

    [Test]
    public void It_allows_a_selected_keyset_boundary_with_an_empty_body()
    {
        QueryResult.QuerySuccess success = new([], null, 2509);

        success.EdfiDocs.Should().BeEmpty();
        success.HighestSelectedDocumentId.Should().Be(2509);
    }

    [Test]
    public void It_carries_the_boundary_as_a_nullable_long()
    {
        typeof(QueryResult.QuerySuccess)
            .GetProperty(nameof(QueryResult.QuerySuccess.HighestSelectedDocumentId))!
            .PropertyType.Should()
            .Be<long?>();
    }
}
