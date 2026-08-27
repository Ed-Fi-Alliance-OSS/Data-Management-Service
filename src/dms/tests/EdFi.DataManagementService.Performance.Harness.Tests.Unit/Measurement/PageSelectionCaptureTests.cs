// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_A_Single_Query_Keyset
{
    private PageSelectionQueryCapture _capture = null!;

    [SetUp]
    public void Setup()
    {
        PageKeysetSpec.Query query = new(
            new PageDocumentIdSqlPlan(
                "SELECT r.\"DocumentId\" FROM x ORDER BY r.\"DocumentId\" ASC LIMIT @limit OFFSET @offset;",
                TotalCountSql: null,
                PageParametersInOrder: [],
                TotalCountParametersInOrder: null
            ),
            new Dictionary<string, object?> { ["offset"] = 0L, ["limit"] = 25L },
            PageOrderingMode.DocumentId
        );
        _capture = PageSelectionCapture.ExtractSingleQuery([query]);
    }

    [Test]
    public void It_extracts_the_page_selection_sql()
    {
        _capture.PageDocumentIdSql.Should().Contain("LIMIT @limit OFFSET @offset");
    }

    [Test]
    public void It_extracts_the_bound_parameter_values()
    {
        _capture.ParameterValues["offset"].Should().Be(0L);
        _capture.ParameterValues["limit"].Should().Be(25L);
    }

    [Test]
    public void It_hashes_the_sql_as_lowercase_hex()
    {
        _capture.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
        _capture.Sha256.Should().Be(PageSelectionCapture.Sha256Lowercase(_capture.PageDocumentIdSql));
    }
}

[TestFixture]
public class Given_Unexpected_Keyset_Windows
{
    [Test]
    public void It_rejects_an_empty_window()
    {
        FluentActions
            .Invoking(() => PageSelectionCapture.ExtractSingleQuery([]))
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*observed 0*");
    }

    [Test]
    public void It_rejects_multiple_keysets()
    {
        PageKeysetSpec single = new PageKeysetSpec.Single(1);
        FluentActions
            .Invoking(() => PageSelectionCapture.ExtractSingleQuery([single, single]))
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*observed 2*");
    }

    [Test]
    public void It_rejects_a_non_query_keyset()
    {
        FluentActions
            .Invoking(() => PageSelectionCapture.ExtractSingleQuery([new PageKeysetSpec.Single(1)]))
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*Single*");
    }
}

[TestFixture]
public class Given_The_Sha256_Helper
{
    [Test]
    public void It_is_deterministic_and_content_sensitive()
    {
        PageSelectionCapture
            .Sha256Lowercase("SELECT 1")
            .Should()
            .Be(PageSelectionCapture.Sha256Lowercase("SELECT 1"));
        PageSelectionCapture
            .Sha256Lowercase("SELECT 1")
            .Should()
            .NotBe(PageSelectionCapture.Sha256Lowercase("SELECT 2"));
    }
}
