// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend.OpenSearch.Tests.Unit;

[TestFixture]
public class QueryOpenSearchTests
{
    [Test]
    public void BuildQueryTerms_WithSingleElementAndSinglePath_ReturnsCorrectJson()
    {
        // Arrange
        List<QueryElement> queryElements =
        [
            new(
                "path.to.field", // QueryFieldName
                [new JsonPath("$.path.to.field")], // DocumentPaths
                "testValue" // Value
            ),
        ];

        // Act
        JsonArray result = QueryOpenSearch.BuildQueryTerms(queryElements);

        // Assert
        JsonNode expectedJson = JsonNode.Parse(
            @"[
                {
                    ""match_phrase"": {
                        ""edfidoc.path.to.field"": ""testValue""
                    }
                }
            ]"
        )!;

        result.ToJsonString().Should().Be(expectedJson!.ToJsonString());
    }

    [Test]
    public void BuildQueryTerms_WithMultipleElements_ReturnsCorrectJson()
    {
        // Arrange
        List<QueryElement> queryElements =
        [
            new("path.to.field1", [new JsonPath("$.path.to.field1")], "value1"),
            new("path.to.field2", [new JsonPath("$.path.to.field2")], "value2"),
        ];

        // Act
        JsonArray result = QueryOpenSearch.BuildQueryTerms(queryElements);

        // Assert
        string expectedJson = """
            [
                {
                    "match_phrase": {
                        "edfidoc.path.to.field1": "value1"
                    }
                },
                {
                    "match_phrase": {
                        "edfidoc.path.to.field2": "value2"
                    }
                }
            ]
            """;
        JsonNode? expected = JsonNode.Parse(expectedJson);
        result.ToJsonString().Should().Be(expected!.ToJsonString());
    }

    [Test]
    public void BuildAuthorizationFilters_WithNoSecurableInfo_ReturnsEmptyList()
    {
        // Arrange
        IQueryRequest queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo).Returns([]);
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns([]);

        // Act
        List<JsonObject> result = QueryOpenSearch.BuildAuthorizationFilters(
            queryRequest,
            NullLogger.Instance
        );

        // Assert
        result.Should().BeEmpty();
    }

    [Test]
    public void BuildAuthorizationFilters_WithNamespaceSecurableKey_ReturnsMatchPhraseFilter()
    {
        // Arrange
        AuthorizationSecurableInfo securableInfo = new AuthorizationSecurableInfo("Namespace");
        AuthorizationStrategyEvaluator strategyEvaluator = new AuthorizationStrategyEvaluator(
            "NamespaceBased",
            [new AuthorizationFilter.Namespace("uri://ed-fi.org")],
            FilterOperator.Or
        );
        IQueryRequest queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo).Returns([securableInfo]);
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns([strategyEvaluator]);
        string expectedJson = """
            {
                "match_phrase": {
                    "securityelements.Namespace": "uri://ed-fi.org"
                }
            }
            """;

        // Act
        List<JsonObject> result = QueryOpenSearch.BuildAuthorizationFilters(
            queryRequest,
            NullLogger.Instance
        );

        // Assert
        result.Should().ContainSingle();
        JsonObject? expected = JsonNode.Parse(expectedJson) as JsonObject;
        expected.Should().NotBeNull();
        result[0].ToJsonString().Should().Be(expected!.ToJsonString());
    }

    [Test]
    public void BuildAuthorizationFilters_WithMultipleNamespaces_ReturnsTermsFilter()
    {
        // Arrange
        AuthorizationSecurableInfo securableInfo = new AuthorizationSecurableInfo("Namespace");
        AuthorizationStrategyEvaluator strategyEvaluator = new AuthorizationStrategyEvaluator(
            "NamespaceBased",
            [
                new AuthorizationFilter.Namespace("uri://ed-fi.org"),
                new AuthorizationFilter.Namespace("uri://other.org"),
            ],
            FilterOperator.Or
        );
        IQueryRequest queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo).Returns([securableInfo]);
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns([strategyEvaluator]);
        string expectedJson = """
            {
                "bool": {
                    "should": [
                        {
                            "match_phrase": {
                                "securityelements.Namespace": "uri://ed-fi.org"
                            }
                        },
                        {
                            "match_phrase": {
                                "securityelements.Namespace": "uri://other.org"
                            }
                        }
                    ]
                }
            }
            """;

        // Act
        List<JsonObject> result = QueryOpenSearch.BuildAuthorizationFilters(
            queryRequest,
            NullLogger.Instance
        );

        // Assert
        result.Should().ContainSingle();
        JsonObject? expected = JsonNode.Parse(expectedJson) as JsonObject;
        expected.Should().NotBeNull();
        result[0].ToJsonString().Should().Be(expected!.ToJsonString());
    }

    [Test]
    public void BuildAuthorizationFilters_WithEducationOrganizationSecurableKey_ReturnsTermsFilter()
    {
        // Arrange
        AuthorizationSecurableInfo securableInfo = new AuthorizationSecurableInfo("EducationOrganization");
        AuthorizationStrategyEvaluator strategyEvaluator = new AuthorizationStrategyEvaluator(
            "EdOrgBased",
            [new AuthorizationFilter.EducationOrganization("6001")],
            FilterOperator.Or
        );
        IQueryRequest queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo).Returns([securableInfo]);
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns([strategyEvaluator]);
        string expectedJson = """
            {
                "terms": {
                    "securityelements.EducationOrganization.Id": {
                        "index": "edfi.dms.educationorganizationhierarchytermslookup",
                        "id": "6001",
                        "path": "hierarchy.array"
                    }
                }
            }
            """;

        // Act
        List<JsonObject> result = QueryOpenSearch.BuildAuthorizationFilters(
            queryRequest,
            NullLogger.Instance
        );

        // Assert
        result.Should().ContainSingle();
        JsonObject? expected = JsonNode.Parse(expectedJson) as JsonObject;
        expected.Should().NotBeNull();
        result[0].ToJsonString().Should().Be(expected!.ToJsonString());
    }

    [Test]
    public void BuildAuthorizationFilters_WithStudentUniqueIdSecurableKey_ReturnsStudentTermsFilter()
    {
        // Arrange
        AuthorizationSecurableInfo securableInfo = new AuthorizationSecurableInfo("StudentUniqueId");
        AuthorizationStrategyEvaluator strategyEvaluator = new AuthorizationStrategyEvaluator(
            "StudentBased",
            [new AuthorizationFilter.EducationOrganization("6001")],
            FilterOperator.Or
        );
        IQueryRequest queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo).Returns([securableInfo]);
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns([strategyEvaluator]);
        string expectedJson = """
            {
                "terms": {
                    "studentschoolauthorizationedorgids.array": {
                        "index": "edfi.dms.educationorganizationhierarchytermslookup",
                        "id": "6001",
                        "path": "hierarchy.array"
                    }
                }
            }
            """;

        // Act
        List<JsonObject> result = QueryOpenSearch.BuildAuthorizationFilters(
            queryRequest,
            NullLogger.Instance
        );

        // Assert
        result.Should().ContainSingle();
        JsonObject? expected = JsonNode.Parse(expectedJson) as JsonObject;
        expected.Should().NotBeNull();
        result[0].ToJsonString().Should().Be(expected!.ToJsonString());
    }

    [Test]
    public void BuildAuthorizationFilters_WithContactUniqueIdSecurableKey_ReturnsContactTermsFilter()
    {
        // Arrange
        AuthorizationSecurableInfo securableInfo = new AuthorizationSecurableInfo("ContactUniqueId");
        AuthorizationStrategyEvaluator strategyEvaluator = new AuthorizationStrategyEvaluator(
            "ContactBased",
            [new AuthorizationFilter.EducationOrganization("6001")],
            FilterOperator.Or
        );
        IQueryRequest queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo).Returns([securableInfo]);
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns([strategyEvaluator]);
        string expectedJson = """
            {
                "terms": {
                    "contactstudentschoolauthorizationedorgids.array": {
                        "index": "edfi.dms.educationorganizationhierarchytermslookup",
                        "id": "6001",
                        "path": "hierarchy.array"
                    }
                }
            }
            """;

        // Act
        List<JsonObject> result = QueryOpenSearch.BuildAuthorizationFilters(
            queryRequest,
            NullLogger.Instance
        );

        // Assert
        result.Should().ContainSingle();
        JsonObject? expected = JsonNode.Parse(expectedJson) as JsonObject;
        expected.Should().NotBeNull();
        result[0].ToJsonString().Should().Be(expected!.ToJsonString());
    }

    [Test]
    public void BuildAuthorizationFilters_WithStaffUniqueIdSecurableKey_ReturnsStaffTermsFilter()
    {
        // Arrange
        AuthorizationSecurableInfo securableInfo = new AuthorizationSecurableInfo("StaffUniqueId");
        AuthorizationStrategyEvaluator strategyEvaluator = new AuthorizationStrategyEvaluator(
            "StaffBased",
            [new AuthorizationFilter.EducationOrganization("6001")],
            FilterOperator.Or
        );
        IQueryRequest queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo).Returns([securableInfo]);
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns([strategyEvaluator]);
        string expectedJson = """
            {
                "terms": {
                    "staffeducationorganizationauthorizationedorgids.array": {
                        "index": "edfi.dms.educationorganizationhierarchytermslookup",
                        "id": "6001",
                        "path": "hierarchy.array"
                    }
                }
            }
            """;

        // Act
        List<JsonObject> result = QueryOpenSearch.BuildAuthorizationFilters(
            queryRequest,
            NullLogger.Instance
        );

        // Assert
        result.Should().ContainSingle();
        JsonObject? expected = JsonNode.Parse(expectedJson) as JsonObject;
        expected.Should().NotBeNull();
        result[0].ToJsonString().Should().Be(expected!.ToJsonString());
    }

    [Test]
    public void BuildAuthorizationFilters_WithStaffUniqueIdSecurableKeyAndMultipleEdOrgs_ReturnsStaffBoolShouldFilter()
    {
        // Arrange
        AuthorizationSecurableInfo securableInfo = new AuthorizationSecurableInfo("StaffUniqueId");
        AuthorizationStrategyEvaluator strategyEvaluator = new AuthorizationStrategyEvaluator(
            "StaffBased",
            [
                new AuthorizationFilter.EducationOrganization("6001"),
                new AuthorizationFilter.EducationOrganization("7002"),
            ],
            FilterOperator.Or
        );
        IQueryRequest queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo).Returns([securableInfo]);
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns([strategyEvaluator]);
        string expectedJson = """
            {
                "bool": {
                    "should": [
                        {
                            "terms": {
                                "staffeducationorganizationauthorizationedorgids.array": {
                                    "index": "edfi.dms.educationorganizationhierarchytermslookup",
                                    "id": "6001",
                                    "path": "hierarchy.array"
                                }
                            }
                        },
                        {
                            "terms": {
                                "staffeducationorganizationauthorizationedorgids.array": {
                                    "index": "edfi.dms.educationorganizationhierarchytermslookup",
                                    "id": "7002",
                                    "path": "hierarchy.array"
                                }
                            }
                        }
                    ]
                }
            }
            """;

        // Act
        List<JsonObject> result = QueryOpenSearch.BuildAuthorizationFilters(
            queryRequest,
            NullLogger.Instance
        );

        // Assert
        result.Should().ContainSingle();
        JsonObject? expected = JsonNode.Parse(expectedJson) as JsonObject;
        expected.Should().NotBeNull();
        result[0].ToJsonString().Should().Be(expected!.ToJsonString());
    }

    [Test]
    public void BuildQueryObject_WithBasicQueryAndPagination_ReturnsExpectedJson()
    {
        // Arrange
        var queryElements = new[]
        {
            new QueryElement("field1", new[] { new JsonPath("$.field1") }, "value1"),
            new QueryElement("field2", new[] { new JsonPath("$.field2") }, "value2"),
        };
        var pagination = new PaginationParameters(
            Limit: 10,
            Offset: 5,
            TotalCount: true,
            MaximumPageSize: 500
        );
        var resourceInfo = A.Fake<ResourceInfo>();
        var queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.QueryElements).Returns(queryElements);
        A.CallTo(() => queryRequest.PaginationParameters).Returns(pagination);
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo).Returns([]);
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns([]);
        A.CallTo(() => queryRequest.ResourceInfo).Returns(resourceInfo);

        // Act
        JsonObject result = QueryOpenSearch.BuildQueryObject(queryRequest, NullLogger.Instance);

        // Assert
        string expectedJson = """
            {
                "query": {
                    "bool": {
                        "must": [
                            {
                                "match_phrase": {
                                    "edfidoc.field1": "value1"
                                }
                            },
                            {
                                "match_phrase": {
                                    "edfidoc.field2": "value2"
                                }
                            }
                        ]
                    }
                },
                "sort": [
                    {
                        "_doc": {
                            "order": "asc"
                        }
                    }
                ],
                "track_total_hits":true,
                "size": 10,
                "from": 5
            }
            """;
        JsonNode? expected = JsonNode.Parse(expectedJson);
        result.ToJsonString().Should().Be(expected!.ToJsonString());

        // Assert
        result["track_total_hits"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public void BuildQueryObject_WithPagination_WithNoLimit_ReturnsConfiguredDefaultLimit()
    {
        // Arrange
        var pagination = new PaginationParameters(
            Limit: null,
            Offset: 5,
            TotalCount: true,
            MaximumPageSize: 500
        );
        var resourceInfo = A.Fake<ResourceInfo>();
        var queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.QueryElements).Returns([]);
        A.CallTo(() => queryRequest.PaginationParameters).Returns(pagination);
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo).Returns([]);
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns([]);
        A.CallTo(() => queryRequest.ResourceInfo).Returns(resourceInfo);

        // Act
        JsonObject result = QueryOpenSearch.BuildQueryObject(queryRequest, NullLogger.Instance);

        // Assert
        string expectedJson = """
            {
                "query": {
                    "bool": {
                        "must": []
                    }
                },
                "sort": [
                    {
                        "_doc": {
                            "order": "asc"
                        }
                    }
                ],
                "track_total_hits": true,
                "size": 500,
                "from": 5
            }
            """;
        JsonNode? expected = JsonNode.Parse(expectedJson);
        result.ToJsonString().Should().Be(expected!.ToJsonString());

        // Assert
        result["track_total_hits"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public void BuildQueryObject_WithPagination_WithNoOffset_ReturnsConfiguredDefaultOffset()
    {
        // Arrange
        var pagination = new PaginationParameters(
            Limit: 10,
            Offset: null,
            TotalCount: true,
            MaximumPageSize: 500
        );
        var resourceInfo = A.Fake<ResourceInfo>();
        var queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.QueryElements).Returns([]);
        A.CallTo(() => queryRequest.PaginationParameters).Returns(pagination);
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo).Returns([]);
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns([]);
        A.CallTo(() => queryRequest.ResourceInfo).Returns(resourceInfo);

        // Act
        JsonObject result = QueryOpenSearch.BuildQueryObject(queryRequest, NullLogger.Instance);

        // Assert
        string expectedJson = """
            {
                "query": {
                    "bool": {
                        "must": []
                    }
                },
                "sort": [
                    {
                        "_doc": {
                            "order": "asc"
                        }
                    }
                ],
                "track_total_hits": true,
                "size": 10
            }
            """;
        JsonNode? expected = JsonNode.Parse(expectedJson);
        result.ToJsonString().Should().Be(expected!.ToJsonString());

        // Assert
        result["track_total_hits"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public void BuildQueryObject_WithAuthorizationFilters_AddsShouldClause()
    {
        // Arrange
        var queryElements = new[]
        {
            new QueryElement("field1", new[] { new JsonPath("$.field1") }, "value1"),
        };
        var pagination = new PaginationParameters(
            Limit: null,
            Offset: null,
            TotalCount: false,
            MaximumPageSize: 500
        );
        var resourceInfo = A.Fake<ResourceInfo>();
        var securableInfo = new AuthorizationSecurableInfo("Namespace");
        var strategyEvaluator = new AuthorizationStrategyEvaluator(
            "NamespaceBased",
            new[] { new AuthorizationFilter.Namespace("uri://ed-fi.org") },
            FilterOperator.Or
        );
        var queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.QueryElements).Returns(queryElements);
        A.CallTo(() => queryRequest.PaginationParameters).Returns(pagination);
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo).Returns([securableInfo]);
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns([strategyEvaluator]);
        A.CallTo(() => queryRequest.ResourceInfo).Returns(resourceInfo);

        // Act
        JsonObject result = QueryOpenSearch.BuildQueryObject(queryRequest, NullLogger.Instance);

        // Assert
        string expectedJson = """
            {
                "query": {
                    "bool": {
                        "must": [
                            {
                                "match_phrase": {
                                    "edfidoc.field1": "value1"
                                }
                            },
                            {
                                "bool": {
                                    "must": [
                                        {
                                            "match_phrase": {
                                                "securityelements.Namespace": "uri://ed-fi.org"
                                            }
                                        }
                                    ]
                                }
                            }
                        ]
                    }
                },
                "sort": [
                    {
                        "_doc": {
                            "order": "asc"
                        }
                    }
                ],
                "size": 500
            }
            """;
        JsonNode? expected = JsonNode.Parse(expectedJson);
        result.ToJsonString().Should().Be(expected!.ToJsonString());
    }

    [Test]
    public void BuildQueryObject_WithTotalCountTrueAndManyElements_IncludesTrackTotalHitsTrue()
    {
        // Arrange
        const int ElementCount = 15000; // Simulates over than 10,000 elements
        var queryElements = Enumerable
            .Range(1, ElementCount)
            .Select(i => new QueryElement($"field{i}", new[] { new JsonPath($"$.field{i}") }, $"value{i}"))
            .ToArray();

        var pagination = new PaginationParameters(
            Limit: 10,
            Offset: 0,
            TotalCount: true,
            MaximumPageSize: 500
        );
        var resourceInfo = A.Fake<ResourceInfo>();
        var queryRequest = A.Fake<IQueryRequest>();
        A.CallTo(() => queryRequest.QueryElements).Returns(queryElements);
        A.CallTo(() => queryRequest.PaginationParameters).Returns(pagination);
        A.CallTo(() => queryRequest.AuthorizationSecurableInfo).Returns([]);
        A.CallTo(() => queryRequest.AuthorizationStrategyEvaluators).Returns([]);
        A.CallTo(() => queryRequest.ResourceInfo).Returns(resourceInfo);

        // Act
        JsonObject result = QueryOpenSearch.BuildQueryObject(queryRequest, NullLogger.Instance);

        // Assert: Query must include track_total_hits set to true
        result["track_total_hits"]!.GetValue<bool>().Should().BeTrue();

        var mustArray = result["query"]?["bool"]?["must"] as JsonArray;
        mustArray.Should().NotBeNull();
        mustArray!.Count.Should().Be(ElementCount);
    }
}
