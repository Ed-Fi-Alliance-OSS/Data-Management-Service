// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Response;

[TestFixture]
[Parallelizable]
public class Given_Failure_Response_For_Data_Conflict
{
    private const string ExpectedType = "urn:ed-fi:api:data-conflict:dependent-item-exists";
    private const string ExpectedTitle = "Dependent Item Exists";
    private const int ExpectedStatus = 409;

    private static readonly TraceId _traceId = new("fr-trace");

    [Test]
    public void It_renders_a_generic_fallback_detail_when_the_dependent_item_names_array_is_empty()
    {
        // DMS-1011: the relational delete path surfaces an empty array when the FK violation's
        // constraint name is missing (pgsql null ConstraintName / mssql localized 547) or
        // cannot be mapped to a resource in the compiled model. The response layer owns the
        // user-facing fallback string so the backend contract can stay honest.
        var response = FailureResponse.ForDataConflict([], _traceId);

        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "The requested action cannot be performed because this item is referenced by existing item(s)."
            );
        AssertSharedEnvelope(response);
    }

    [Test]
    public void It_preserves_the_existing_single_name_ods_api_phrasing_byte_for_byte()
    {
        // Regression: the byte-for-byte string here is asserted by E2E scenarios in
        // DeleteReferenceValidation.feature. If this test fails, the E2E regression will too.
        var response = FailureResponse.ForDataConflict(["Calendar"], _traceId);

        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "The requested action cannot be performed because this item is referenced by existing Calendar item(s)."
            );
        AssertSharedEnvelope(response);
    }

    [Test]
    public void It_joins_multiple_dependent_item_names_with_a_comma_and_space()
    {
        var response = FailureResponse.ForDataConflict(["Calendar", "School"], _traceId);

        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "The requested action cannot be performed because this item is referenced by existing Calendar, School item(s)."
            );
        AssertSharedEnvelope(response);
    }

    private static void AssertSharedEnvelope(System.Text.Json.Nodes.JsonNode response)
    {
        // The empty-array branch must not diverge on type / title / status / correlationId /
        // validationErrors / errors — only the rendered detail differs.
        response["type"]!.ToString().Should().Be(ExpectedType);
        response["title"]!.ToString().Should().Be(ExpectedTitle);
        response["status"]!.GetValue<int>().Should().Be(ExpectedStatus);
        response["correlationId"]!.ToString().Should().Be(_traceId.Value);
        response["validationErrors"]!.AsObject().Count.Should().Be(0);
        response["errors"]!.AsArray().Count.Should().Be(0);
    }
}
