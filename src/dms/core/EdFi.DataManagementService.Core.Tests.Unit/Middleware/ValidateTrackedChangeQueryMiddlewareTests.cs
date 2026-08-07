// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ValidateTrackedChangeQueryMiddlewareTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_No_Parsed_Query_Elements : ValidateTrackedChangeQueryMiddlewareTests
    {
        private bool _nextCalled;
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = No.RequestInfo("tracked-change-query");

            var sut = new ValidateTrackedChangeQueryMiddleware(NullLogger.Instance);
            await sut.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_continues_the_pipeline() => _nextCalled.Should().BeTrue();

        [Test]
        public void It_does_not_replace_the_response() =>
            _requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Parsed_Resource_Query_Element : ValidateTrackedChangeQueryMiddlewareTests
    {
        private bool _nextCalled;
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = No.RequestInfo("tracked-change-query");
            _requestInfo.QueryElements =
            [
                new(
                    QueryFieldName: "schoolId",
                    DocumentPaths: [new JsonPath("$.schoolId")],
                    Value: "8118601",
                    Type: "number"
                ),
            ];

            var sut = new ValidateTrackedChangeQueryMiddleware(NullLogger.Instance);
            await sut.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_stops_the_pipeline() => _nextCalled.Should().BeFalse();

        [Test]
        public void It_returns_bad_request()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            _requestInfo.FrontendResponse.Headers.Should().BeEmpty();
            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
        }

        [Test]
        public void It_reports_that_resource_query_fields_are_not_valid_for_tracked_change_endpoints()
        {
            _requestInfo.FrontendResponse.Body!["errors"]![0]!
                .GetValue<string>()
                .Should()
                .Be("The query field 'schoolId' is not valid for this Change Query endpoint.");
        }
    }

    /// <summary>
    /// Cursor parameter recognition is operation-scoped: these names are not globally reserved, and
    /// a Change Query endpoint must reject them rather than silently discard them.
    ///
    /// <para>
    /// Both Change Query operations are named because the operation scope is part of the contract.
    /// The rejection itself is not path-dependent: one pipeline serves both operations and this step
    /// never reads the request path, so neither operation can reject while the other accepts.
    /// </para>
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Cursor_Parameters : ValidateTrackedChangeQueryMiddlewareTests
    {
        private const string DeletesPath = "/ed-fi/schools/deletes";

        private const string KeyChangesPath = "/ed-fi/schools/keyChanges";

        private static async Task<(RequestInfo RequestInfo, bool NextCalled)> Execute(
            (string Key, string Value)[] queryParameters,
            QueryElement[]? queryElements = null,
            string path = DeletesPath
        )
        {
            FrontendRequest frontendRequest = new(
                Body: null,
                Form: null,
                Headers: [],
                Path: path,
                QueryParameters: queryParameters.ToDictionary(
                    static parameter => parameter.Key,
                    static parameter => parameter.Value,
                    StringComparer.Ordinal
                ),
                TraceId: new TraceId("tracked-change-query"),
                RouteQualifiers: []
            );

            RequestInfo requestInfo = new(frontendRequest, RequestMethod.GET, No.ServiceProvider)
            {
                QueryElements = queryElements ?? [],
            };

            bool nextCalled = false;
            var sut = new ValidateTrackedChangeQueryMiddleware(NullLogger.Instance);
            await sut.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            return (requestInfo, nextCalled);
        }

        private static string[] ErrorsFrom(RequestInfo requestInfo) =>
            [
                .. requestInfo.FrontendResponse.Body!["errors"]!
                    .AsArray()
                    .Select(error => error!.GetValue<string>()),
            ];

        [TestCase(DeletesPath, "pageToken")]
        [TestCase(DeletesPath, "pageSize")]
        [TestCase(KeyChangesPath, "pageToken")]
        [TestCase(KeyChangesPath, "pageSize")]
        public async Task It_rejects_a_cursor_parameter_by_name(string path, string parameter)
        {
            var (requestInfo, nextCalled) = await Execute([(parameter, "5")], path: path);

            nextCalled.Should().BeFalse();
            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            ErrorsFrom(requestInfo)
                .Should()
                .Equal($"The query field '{parameter}' is not valid for this Change Query endpoint.");
        }

        [Test]
        public async Task It_reports_both_cursor_parameters_in_canonical_order()
        {
            var (requestInfo, _) = await Execute([("pageSize", "5"), ("pageToken", "anything")]);

            ErrorsFrom(requestInfo)
                .Should()
                .Equal(
                    "The query field 'pageToken' is not valid for this Change Query endpoint.",
                    "The query field 'pageSize' is not valid for this Change Query endpoint."
                );
        }

        [Test]
        public async Task It_reports_cursor_parameters_before_resource_query_fields()
        {
            var (requestInfo, _) = await Execute(
                [("pageToken", "anything")],
                [
                    new(
                        QueryFieldName: "schoolId",
                        DocumentPaths: [new JsonPath("$.schoolId")],
                        Value: "8118601",
                        Type: "number"
                    ),
                ]
            );

            ErrorsFrom(requestInfo)
                .Should()
                .Equal(
                    "The query field 'pageToken' is not valid for this Change Query endpoint.",
                    "The query field 'schoolId' is not valid for this Change Query endpoint."
                );
        }

        [Test]
        public async Task It_uses_the_existing_bad_request_shell()
        {
            var (requestInfo, _) = await Execute([("pageToken", "anything")]);

            JsonNode body = requestInfo.FrontendResponse.Body!;

            body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The request could not be processed. See 'errors' for details.");
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
            body["title"]!.GetValue<string>().Should().Be("Bad Request");
            requestInfo.FrontendResponse.Headers.Should().BeEmpty();
        }

        [Test]
        public async Task It_continues_the_pipeline_for_a_supported_change_query()
        {
            var (requestInfo, nextCalled) = await Execute([
                ("minChangeVersion", "1"),
                ("maxChangeVersion", "2"),
                ("limit", "25"),
                ("offset", "0"),
                ("totalCount", "true"),
            ]);

            nextCalled.Should().BeTrue();
            requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }
    }
}
