// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Identity;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Identity;

/// <summary>
/// Pins <see cref="IdentityErrorProjection.Project" /> (design.md:896-927, story B7): a path is used
/// verbatim as a <c>validationErrors</c> key, a blank or null path routes to <c>errors</c>, two
/// messages at one path are grouped in provider order, and the 400 selects between
/// <see cref="FailureResponse.ForBadRequest" /> and <see cref="FailureResponse.ForDataValidation" />
/// exactly as <see cref="Middleware.ValidateDocumentMiddleware" /> does.
/// </summary>
[TestFixture]
public class IdentityErrorProjectionTests
{
    private static readonly TraceId _traceId = new("identity-projection-trace");

    [Test]
    public void A_dotted_path_becomes_a_verbatim_validationErrors_key()
    {
        IdentityError[] errors = [new() { Path = "$.firstName", Message = "First name is required." }];

        var response = IdentityErrorProjection.Project(errors, _traceId);

        response["validationErrors"]!["$.firstName"]!
            .AsArray()
            .Select(node => node!.ToString())
            .Should()
            .Equal("First name is required.");
    }

    [Test]
    public void An_array_indexed_path_becomes_a_verbatim_validationErrors_key_with_no_renumbering()
    {
        IdentityError[] errors =
        [
            new() { Path = "$[2].firstName", Message = "First name is required for item 2." },
        ];

        var response = IdentityErrorProjection.Project(errors, _traceId);

        response["validationErrors"]!.AsObject().Should().ContainKey("$[2].firstName");
        response["validationErrors"]!["$[2].firstName"]!
            .AsArray()
            .Select(node => node!.ToString())
            .Should()
            .Equal("First name is required for item 2.");
    }

    [Test]
    public void A_null_path_is_appended_to_the_document_level_errors_collection()
    {
        IdentityError[] errors = [new() { Path = null, Message = "The request could not be evaluated." }];

        var response = IdentityErrorProjection.Project(errors, _traceId);

        response["errors"]!
            .AsArray()
            .Select(node => node!.ToString())
            .Should()
            .Equal("The request could not be evaluated.");
        response["validationErrors"]!.AsObject().Count.Should().Be(0);
    }

    [Test]
    public void A_blank_path_is_appended_to_the_document_level_errors_collection()
    {
        IdentityError[] errors = [new() { Path = "   ", Message = "The request could not be evaluated." }];

        var response = IdentityErrorProjection.Project(errors, _traceId);

        response["errors"]!
            .AsArray()
            .Select(node => node!.ToString())
            .Should()
            .Equal("The request could not be evaluated.");
        response["validationErrors"]!.AsObject().Count.Should().Be(0);
    }

    [Test]
    public void Multiple_document_level_errors_are_appended_in_provider_order()
    {
        IdentityError[] errors =
        [
            new() { Path = null, Message = "first" },
            new() { Path = "", Message = "second" },
            new() { Path = null, Message = "third" },
        ];

        var response = IdentityErrorProjection.Project(errors, _traceId);

        response["errors"]!
            .AsArray()
            .Select(node => node!.ToString())
            .Should()
            .Equal("first", "second", "third");
    }

    [Test]
    public void Two_errors_at_the_same_path_are_grouped_under_one_key_in_provider_order()
    {
        IdentityError[] errors =
        [
            new() { Path = "$.firstName", Message = "First name is required." },
            new() { Path = "$.firstName", Message = "First name must not exceed 75 characters." },
        ];

        var response = IdentityErrorProjection.Project(errors, _traceId);

        response["validationErrors"]!.AsObject().Count.Should().Be(1);
        response["validationErrors"]!["$.firstName"]!
            .AsArray()
            .Select(node => node!.ToString())
            .Should()
            .Equal("First name is required.", "First name must not exceed 75 characters.");
    }

    [Test]
    public void Paths_are_kept_in_first_seen_provider_order_when_interleaved_with_a_second_path()
    {
        IdentityError[] errors =
        [
            new() { Path = "$.firstName", Message = "a" },
            new() { Path = "$.lastName", Message = "b" },
            new() { Path = "$.firstName", Message = "c" },
        ];

        var response = IdentityErrorProjection.Project(errors, _traceId);

        response["validationErrors"]!
            .AsObject()
            .Select(pair => pair.Key)
            .Should()
            .Equal("$.firstName", "$.lastName");
    }

    [Test]
    public void A_non_empty_errors_array_selects_ForBadRequest_with_the_errors_arm_detail()
    {
        IdentityError[] errors =
        [
            new() { Path = null, Message = "document-level failure" },
            new() { Path = "$.firstName", Message = "also present, but errors still wins" },
        ];

        var response = IdentityErrorProjection.Project(errors, _traceId);

        response["detail"]!.ToString().Should().Be(FailureResponse.ErrorsArmDetail);
        response["type"]!.ToString().Should().Be("urn:ed-fi:api:bad-request");
        response["status"]!.GetValue<int>().Should().Be(400);
    }

    [Test]
    public void An_empty_errors_array_with_only_path_scoped_entries_selects_ForDataValidation_with_the_validationErrors_arm_detail()
    {
        IdentityError[] errors = [new() { Path = "$.firstName", Message = "First name is required." }];

        var response = IdentityErrorProjection.Project(errors, _traceId);

        response["detail"]!.ToString().Should().Be(FailureResponse.ValidationErrorsArmDetail);
        response["type"]!.ToString().Should().Be("urn:ed-fi:api:bad-request:data-validation-failed");
        response["status"]!.GetValue<int>().Should().Be(400);
    }

    [Test]
    public void Correlation_id_is_carried_from_the_supplied_trace_id()
    {
        IdentityError[] errors = [new() { Path = null, Message = "failure" }];

        var response = IdentityErrorProjection.Project(errors, _traceId);

        response["correlationId"]!.ToString().Should().Be(_traceId.Value);
    }
}
