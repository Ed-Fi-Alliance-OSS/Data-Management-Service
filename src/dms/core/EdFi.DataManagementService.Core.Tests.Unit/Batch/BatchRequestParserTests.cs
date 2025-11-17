// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Batch;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Batch;

[TestFixture]
public class BatchRequestParserTests
{
    private static RequestInfo CreateRequest(string? body)
    {
        var frontendRequest = new FrontendRequest(
            Path: "/batch",
            Body: body,
            Headers: new Dictionary<string, string>(),
            QueryParameters: new Dictionary<string, string>(),
            TraceId: new TraceId(Guid.NewGuid().ToString())
        );

        return new RequestInfo(frontendRequest, RequestMethod.POST);
    }

    private static async Task<BatchRequestException> ExpectFailureAsync(RequestInfo requestInfo)
    {
        var assertion = await FluentActions
            .Invoking(() => BatchRequestParser.ParseAsync(requestInfo))
            .Should()
            .ThrowAsync<BatchRequestException>();

        return assertion.Which;
    }

    [Test]
    public async Task Given_Empty_Body_Throws_BatchRequestException()
    {
        var ex = await ExpectFailureAsync(CreateRequest(string.Empty));
        ex.Response.StatusCode.Should().Be(400);
    }

    [Test]
    public async Task Given_Non_Array_Body_Throws_BatchRequestException()
    {
        var requestInfo = CreateRequest("""{"op":"create"}""");
        var ex = await ExpectFailureAsync(requestInfo);
        ex.Response.StatusCode.Should().Be(400);
        ex.Response.Body?["detail"]?.GetValue<string>().Should().Contain("must be a JSON array");
    }

    [Test]
    public async Task Given_Invalid_Operation_Value_Throws()
    {
        var body = """[{ "op": "invalid", "resource": "students", "document": {} }]""";
        var ex = await ExpectFailureAsync(CreateRequest(body));
        ex.Response.StatusCode.Should().Be(400);
        ex.Response.Body?["detail"]?.GetValue<string>().Should().Contain("invalid 'op'");
    }

    [Test]
    public async Task Given_Missing_Resource_Throws()
    {
        var body = """[{ "op": "create", "document": {} }]""";
        var ex = await ExpectFailureAsync(CreateRequest(body));
        ex.Response.StatusCode.Should().Be(400);
        ex.Response.Body?["detail"]?.GetValue<string>().Should().Contain("resource");
    }

    [Test]
    public async Task Given_Both_DocumentId_And_NaturalKey_Throws()
    {
        var body = """
            [
              {
                "op": "update",
                "resource": "students",
                "documentId": "a1111111-1111-1111-1111-111111111111",
                "naturalKey": { "studentUniqueId": "1" },
                "document": { "studentUniqueId": "1", "_etag": "etag" }
              }
            ]
            """;
        var ex = await ExpectFailureAsync(CreateRequest(body));
        ex.Response.StatusCode.Should().Be(400);
        ex.Response.Body?["detail"]?.GetValue<string>()
            .Should()
            .Contain("exactly one of 'documentId' or 'naturalKey'");
    }

    [Test]
    public async Task Given_Invalid_DocumentId_Format_Throws()
    {
        var body = """
            [
              {
                "op": "delete",
                "resource": "students",
                "documentId": "not-a-guid"
              }
            ]
            """;
        var ex = await ExpectFailureAsync(CreateRequest(body));
        ex.Response.StatusCode.Should().Be(400);
        ex.Response.Body?["detail"]?.GetValue<string>().Should().Contain("invalid 'documentId'");
    }

    [Test]
    public async Task Given_Valid_Request_Returns_Parsed_Operations()
    {
        var body = """
            [
              {
                "op": "create",
                "resource": "students",
                "document": { "studentUniqueId": "100", "_etag": "etag" }
              },
              {
                "op": "delete",
                "resource": "students",
                "naturalKey": { "studentUniqueId": "100" }
              }
            ]
            """;

        var operations = await BatchRequestParser.ParseAsync(CreateRequest(body));

        operations.Should().HaveCount(2);
        operations[0].OperationType.Should().Be(BatchOperationType.Create);
        operations[0].Document.Should().NotBeNull();
        operations[1].OperationType.Should().Be(BatchOperationType.Delete);
        operations[1].NaturalKey.Should().NotBeNull();
    }

    [Test]
    public async Task Given_Malformed_NaturalKey_Throws()
    {
        var body = """
            [
              {
                "op": "delete",
                "resource": "students",
                "naturalKey": "not-an-object"
              }
            ]
            """;

        var ex = await ExpectFailureAsync(CreateRequest(body));
        ex.Response.StatusCode.Should().Be(400);
        ex.Response.Body?["detail"]?.GetValue<string>().Should().Contain("naturalKey");
    }
}
