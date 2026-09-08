// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Response;

/// <summary>
/// The snapshot mutation 405 body. Every member is pinned here rather than at the call site, because
/// this is the one factory the response is built from and the wording is a published contract.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_FailureResponse_For_Snapshot_Method_Not_Allowed
{
    private static readonly TraceId _traceId = new("snapshot-405-trace");

    private JsonNode _response = default!;

    [SetUp]
    public void Setup()
    {
        _response = FailureResponse.ForSnapshotMethodNotAllowed(_traceId);
    }

    [Test]
    public void It_has_the_snapshot_method_not_allowed_type()
    {
        _response["type"]!.ToString().Should().Be("urn:ed-fi:api:snapshots:method-not-allowed");
    }

    [Test]
    public void It_has_the_snapshot_method_not_allowed_title()
    {
        _response["title"]!.ToString().Should().Be("Method Not Allowed with Snapshots");
    }

    [Test]
    public void It_has_status_405()
    {
        _response["status"]!.GetValue<int>().Should().Be(405);
    }

    /// <summary>
    /// The detail describes the read-only target rather than the request construction, which is what
    /// makes it usable for an otherwise-valid mutation route as well as an invalid one.
    /// </summary>
    [Test]
    public void It_renders_the_read_only_detail()
    {
        _response["detail"]!
            .ToString()
            .Should()
            .Be("An attempt was made to modify data in a Snapshot, but this data is read-only.");
    }

    [Test]
    public void It_carries_the_supplied_correlation_id()
    {
        _response["correlationId"]!.ToString().Should().Be(_traceId.Value);
    }

    /// <summary>
    /// An empty object rather than an absent member or an empty array: the shared envelope always
    /// carries validationErrors, and a client reading it must not have to handle two shapes.
    /// </summary>
    [Test]
    public void It_has_an_empty_validation_errors_object()
    {
        _response["validationErrors"]!.GetValueKind().Should().Be(JsonValueKind.Object);
        _response["validationErrors"]!.AsObject().Count.Should().Be(0);
    }

    /// <summary>
    /// Nothing is added to errors. There is one reason for this rejection and the detail already
    /// states it, so a per-method sentence would only repeat the request back to the caller.
    /// </summary>
    [Test]
    public void It_has_an_empty_errors_array()
    {
        _response["errors"]!.GetValueKind().Should().Be(JsonValueKind.Array);
        _response["errors"]!.AsArray().Count.Should().Be(0);
    }

    [Test]
    public void It_emits_only_the_shared_envelope_members()
    {
        _response
            .AsObject()
            .Select(static member => member.Key)
            .Should()
            .BeEquivalentTo(
                "detail",
                "type",
                "title",
                "status",
                "correlationId",
                "validationErrors",
                "errors"
            );
    }

    /// <summary>
    /// The two 405 bodies share a status code, so type, title, and detail are the only things a
    /// client can distinguish them by. Asserted as a difference, not just as literals, so neither
    /// factory can be edited into agreement with the other without failing here.
    /// </summary>
    [Test]
    public void It_differs_from_the_generic_method_not_allowed_body_in_type_title_and_detail()
    {
        JsonNode generic = FailureResponse.ForMethodNotAllowed(["any error"], _traceId);

        _response["type"]!.ToString().Should().NotBe(generic["type"]!.ToString());
        _response["title"]!.ToString().Should().NotBe(generic["title"]!.ToString());
        _response["detail"]!.ToString().Should().NotBe(generic["detail"]!.ToString());
    }
}
