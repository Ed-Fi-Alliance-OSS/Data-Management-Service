// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
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
public class IdentityErrorProjectionTests
{
    private static readonly TraceId _traceId = new("identity-projection-trace");

    [TestFixture]
    public class Given_A_Dotted_Path_Error
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            IdentityError[] errors = [new() { Path = "$.firstName", Message = "First name is required." }];

            _response = IdentityErrorProjection.Project(errors, _traceId);
        }

        [Test]
        public void It_becomes_a_verbatim_validationErrors_key()
        {
            _response["validationErrors"]!["$.firstName"]!
                .AsArray()
                .Select(node => node!.ToString())
                .Should()
                .Equal("First name is required.");
        }
    }

    [TestFixture]
    public class Given_An_Array_Indexed_Path_Error
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            IdentityError[] errors =
            [
                new() { Path = "$[2].firstName", Message = "First name is required for item 2." },
            ];

            _response = IdentityErrorProjection.Project(errors, _traceId);
        }

        [Test]
        public void It_is_kept_as_a_verbatim_key_with_no_renumbering()
        {
            _response["validationErrors"]!.AsObject().Should().ContainKey("$[2].firstName");
        }

        [Test]
        public void It_holds_the_message_in_the_key_s_array()
        {
            _response["validationErrors"]!["$[2].firstName"]!
                .AsArray()
                .Select(node => node!.ToString())
                .Should()
                .Equal("First name is required for item 2.");
        }
    }

    [TestFixture]
    public class Given_A_Null_Path_Error
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            IdentityError[] errors = [new() { Path = null, Message = "The request could not be evaluated." }];

            _response = IdentityErrorProjection.Project(errors, _traceId);
        }

        [Test]
        public void It_is_appended_to_the_document_level_errors_collection()
        {
            _response["errors"]!
                .AsArray()
                .Select(node => node!.ToString())
                .Should()
                .Equal("The request could not be evaluated.");
        }

        [Test]
        public void It_leaves_validationErrors_empty()
        {
            _response["validationErrors"]!.AsObject().Count.Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_A_Blank_Path_Error
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            IdentityError[] errors =
            [
                new() { Path = "   ", Message = "The request could not be evaluated." },
            ];

            _response = IdentityErrorProjection.Project(errors, _traceId);
        }

        [Test]
        public void It_is_appended_to_the_document_level_errors_collection()
        {
            _response["errors"]!
                .AsArray()
                .Select(node => node!.ToString())
                .Should()
                .Equal("The request could not be evaluated.");
        }

        [Test]
        public void It_leaves_validationErrors_empty()
        {
            _response["validationErrors"]!.AsObject().Count.Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_Multiple_Document_Level_Errors
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            IdentityError[] errors =
            [
                new() { Path = null, Message = "first" },
                new() { Path = "", Message = "second" },
                new() { Path = null, Message = "third" },
            ];

            _response = IdentityErrorProjection.Project(errors, _traceId);
        }

        [Test]
        public void It_appends_them_in_provider_order()
        {
            _response["errors"]!
                .AsArray()
                .Select(node => node!.ToString())
                .Should()
                .Equal("first", "second", "third");
        }
    }

    [TestFixture]
    public class Given_Two_Errors_At_The_Same_Path
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            IdentityError[] errors =
            [
                new() { Path = "$.firstName", Message = "First name is required." },
                new() { Path = "$.firstName", Message = "First name must not exceed 75 characters." },
            ];

            _response = IdentityErrorProjection.Project(errors, _traceId);
        }

        [Test]
        public void It_groups_them_under_one_key()
        {
            _response["validationErrors"]!.AsObject().Count.Should().Be(1);
        }

        [Test]
        public void It_preserves_provider_order_within_the_group()
        {
            _response["validationErrors"]!["$.firstName"]!
                .AsArray()
                .Select(node => node!.ToString())
                .Should()
                .Equal("First name is required.", "First name must not exceed 75 characters.");
        }
    }

    [TestFixture]
    public class Given_Errors_Interleaved_With_A_Second_Path
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            IdentityError[] errors =
            [
                new() { Path = "$.firstName", Message = "a" },
                new() { Path = "$.lastName", Message = "b" },
                new() { Path = "$.firstName", Message = "c" },
            ];

            _response = IdentityErrorProjection.Project(errors, _traceId);
        }

        [Test]
        public void It_keeps_paths_in_first_seen_provider_order()
        {
            _response["validationErrors"]!
                .AsObject()
                .Select(pair => pair.Key)
                .Should()
                .Equal("$.firstName", "$.lastName");
        }
    }

    [TestFixture]
    public class Given_A_Non_Empty_Errors_Array
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            IdentityError[] errors =
            [
                new() { Path = null, Message = "document-level failure" },
                new() { Path = "$.firstName", Message = "also present, but errors still wins" },
            ];

            _response = IdentityErrorProjection.Project(errors, _traceId);
        }

        [Test]
        public void It_selects_ForBadRequest_with_the_errors_arm_detail()
        {
            _response["detail"]!.ToString().Should().Be(FailureResponse.ErrorsArmDetail);
        }

        [Test]
        public void It_uses_the_bad_request_type()
        {
            _response["type"]!.ToString().Should().Be("urn:ed-fi:api:bad-request");
        }

        [Test]
        public void It_returns_400()
        {
            _response["status"]!.GetValue<int>().Should().Be(400);
        }
    }

    [TestFixture]
    public class Given_An_Empty_Errors_Array_With_Only_Path_Scoped_Entries
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            IdentityError[] errors = [new() { Path = "$.firstName", Message = "First name is required." }];

            _response = IdentityErrorProjection.Project(errors, _traceId);
        }

        [Test]
        public void It_selects_ForDataValidation_with_the_validationErrors_arm_detail()
        {
            _response["detail"]!.ToString().Should().Be(FailureResponse.ValidationErrorsArmDetail);
        }

        [Test]
        public void It_uses_the_data_validation_bad_request_type()
        {
            _response["type"]!.ToString().Should().Be("urn:ed-fi:api:bad-request:data-validation-failed");
        }

        [Test]
        public void It_returns_400()
        {
            _response["status"]!.GetValue<int>().Should().Be(400);
        }
    }

    [TestFixture]
    public class Given_A_Projected_Response
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            IdentityError[] errors = [new() { Path = null, Message = "failure" }];

            _response = IdentityErrorProjection.Project(errors, _traceId);
        }

        [Test]
        public void It_carries_the_correlation_id_from_the_supplied_trace_id()
        {
            _response["correlationId"]!.ToString().Should().Be(_traceId.Value);
        }
    }
}
