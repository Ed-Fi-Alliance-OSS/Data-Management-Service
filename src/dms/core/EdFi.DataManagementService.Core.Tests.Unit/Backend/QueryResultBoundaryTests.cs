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

    [Test]
    public void It_allows_a_document_id_continuation_by_default()
    {
        new QueryResult.QuerySuccess([], 0).AllowsDocumentIdContinuation.Should().BeTrue();
    }

    // The two are independent: a page can select keys it cannot anchor a DocumentId continuation on,
    // which is a different state from a page that selected none.
    [Test]
    public void It_allows_a_selected_boundary_that_cannot_anchor_a_document_id_continuation()
    {
        QueryResult.QuerySuccess success = new([], null, 2509) { AllowsDocumentIdContinuation = false };

        success.HighestSelectedDocumentId.Should().Be(2509);
        success.AllowsDocumentIdContinuation.Should().BeFalse();
    }

    /// <summary>
    /// The masked-logging result formatter replaces only the documents, by copy. Rebuilding the record
    /// there would drop any member the rebuild forgot, so this asserts the copy the formatter performs
    /// keeps every other member intact.
    /// </summary>
    [Test]
    public void It_preserves_every_other_member_when_a_copy_replaces_only_the_documents()
    {
        QueryResult.QuerySuccess success = new([JsonValue.Create(1)], 7, 2509)
        {
            AllowsDocumentIdContinuation = false,
        };

        var redacted = success with { EdfiDocs = new JsonArray("REDACTED") };

        redacted.EdfiDocs.Should().ContainSingle().Which!.GetValue<string>().Should().Be("REDACTED");
        redacted.TotalCount.Should().Be(7);
        redacted.HighestSelectedDocumentId.Should().Be(2509);
        redacted.AllowsDocumentIdContinuation.Should().BeFalse();
    }
}
