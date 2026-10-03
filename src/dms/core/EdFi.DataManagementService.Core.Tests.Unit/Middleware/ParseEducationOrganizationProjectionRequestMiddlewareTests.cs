// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.EducationOrganizationProjection;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Tests.Unit.EducationOrganizationProjection;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// Shared arrangement. The default request is the contract's worked example: tenant
/// <c>Tenant_255901</c>, route qualifiers <c>255901/2025</c>, data store 3788, and the clock at the
/// worked cursor's walk start.
/// </summary>
public abstract class ParseEducationOrganizationProjectionRequestMiddlewareTests
{
    protected const string Tenant = "Tenant_255901";

    private protected RecordingLogger<ParseEducationOrganizationProjectionRequestMiddleware> Logger
    {
        get;
        private set;
    } = new();

    protected FakeTimeProvider Clock { get; private set; } = new();

    /// <summary>
    /// NUnit reuses one fixture instance for every test in it, so the clock and the log are rebuilt
    /// per test; otherwise a test that advances the clock would move it for the next one.
    /// </summary>
    [SetUp]
    public void ResetClockAndLog()
    {
        Clock = new FakeTimeProvider(
            DateTimeOffset.FromUnixTimeSeconds(ProjectionCursorFixtures.WorkedWalkIssuedAt)
        );
        Logger = new RecordingLogger<ParseEducationOrganizationProjectionRequestMiddleware>();
    }

    private protected RequestInfo RequestInfo { get; private set; } = null!;

    protected bool NextCalled { get; private set; }

    protected static Dictionary<string, string> Query(params (string Name, string Value)[] parameters) =>
        parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value);

    protected async Task Execute(
        Dictionary<string, string> query,
        string? tenant = Tenant,
        Dictionary<RouteQualifierName, RouteQualifierValue>? routeQualifiers = null,
        string[]? repeatedNames = null,
        EducationOrganizationProjectionSettings? settings = null
    )
    {
        FrontendRequest frontendRequest = new(
            Path: "/management/education-organizations",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: query,
            TraceId: new TraceId("projection-parse"),
            RouteQualifiers: routeQualifiers ?? ProjectionCursorFixtures.WorkedQualifiers(),
            Tenant: tenant
        )
        {
            RepeatedQueryParameterNames = repeatedNames ?? [],
        };

        RequestInfo = new RequestInfo(frontendRequest, RequestMethod.GET, No.ServiceProvider);
        NextCalled = false;

        ParseEducationOrganizationProjectionRequestMiddleware middleware = new(
            settings ?? new EducationOrganizationProjectionSettings(),
            Clock,
            Logger
        );

        await middleware.Execute(
            RequestInfo,
            () =>
            {
                NextCalled = true;
                return Task.CompletedTask;
            }
        );
    }

    protected JsonNode Body => RequestInfo.FrontendResponse.Body!;

    protected string[] Errors =>
        Body["errors"]!.AsArray().Select(error => error!.GetValue<string>()).ToArray();

    protected void AssertRejectedWith(int status, string type)
    {
        NextCalled.Should().BeFalse();
        RequestInfo.EducationOrganizationProjectionRequest.Should().BeNull();
        RequestInfo.FrontendResponse.StatusCode.Should().Be(status);
        Body["type"]!.GetValue<string>().Should().Be(type);
    }

    protected void AssertParameterValidationFailed(params string[] errors)
    {
        AssertRejectedWith(400, "urn:ed-fi:api:bad-request:parameter-validation-failed");
        Errors.Should().Equal(errors);
    }

    protected void AssertInvalidCursor() =>
        AssertRejectedWith(400, "urn:ed-fi:api:education-organization-projection:invalid-cursor");

    protected void AssertUnsupportedContractVersion() =>
        AssertRejectedWith(
            400,
            "urn:ed-fi:api:education-organization-projection:unsupported-contract-version"
        );

    protected static string LimitMessage(int maximumPageSize) =>
        $"Limit must be omitted or set to a numeric value between 1 and {maximumPageSize}.";

    protected const string DataStoreIdMessage =
        "DataStoreId must be set to a numeric value between 1 and 2147483647.";

    [TestFixture]
    [Parallelizable]
    public class Given_A_First_Page_Request_With_Only_A_Data_Store_Id
        : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [SetUp]
        public async Task Setup() => await Execute(Query(("dataStoreId", "3788")));

        [Test]
        public void It_continues_the_pipeline() => NextCalled.Should().BeTrue();

        [Test]
        public void It_records_the_request_with_the_deployment_page_size_and_default_version()
        {
            RequestInfo
                .EducationOrganizationProjectionRequest.Should()
                .Be(
                    new EducationOrganizationProjectionRequest(
                        3788,
                        EducationOrganizationProjectionSettings.MaximumPageSizeDefault,
                        ProjectionContractVersions.V1,
                        ProjectionCursorFixtures.WorkedBinding,
                        Cursor: null
                    )
                );
        }

        [Test]
        public void It_starts_the_walk_without_a_position_so_no_id_is_excluded()
        {
            // No sentinel position stands in for "before the first item": zero and negative ids
            // belong on the first page.
            RequestInfo.EducationOrganizationProjectionRequest!.Cursor.Should().BeNull();
        }

        [Test]
        public void It_does_not_write_a_response() =>
            RequestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Every_Parameter_In_Another_Letter_Case
        : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [SetUp]
        public async Task Setup() =>
            await Execute(
                Query(
                    ("DATASTOREID", "3788"),
                    ("Limit", "2"),
                    ("ContractVersion", ProjectionContractVersions.V1),
                    ("CURSOR", ProjectionCursorFixtures.WorkedNextCursor)
                )
            );

        [Test]
        public void It_accepts_them()
        {
            NextCalled.Should().BeTrue();
            RequestInfo.EducationOrganizationProjectionRequest!.Limit.Should().Be(2);
            RequestInfo.EducationOrganizationProjectionRequest.Cursor.Should().NotBeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Unknown_Parameters : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [SetUp]
        public async Task Setup() =>
            await Execute(Query(("dataStoreId", "3788"), ("offset", "abc"), ("totalCount", "true")));

        [Test]
        public void It_ignores_them() => NextCalled.Should().BeTrue();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repeated_Parameter_Reported_In_Another_Letter_Case
        : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [SetUp]
        public async Task Setup() =>
            await Execute(Query(("dataStoreId", "3788"), ("limit", "2")), repeatedNames: ["LIMIT"]);

        [Test]
        public void It_rejects_the_request_naming_the_parameter() =>
            AssertParameterValidationFailed("Limit must not be supplied more than once.");
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Two_Letter_Case_Spellings_Of_One_Parameter_In_The_Query
        : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [SetUp]
        public async Task Setup() =>
            await Execute(Query(("dataStoreId", "3788"), ("cursor", "a"), ("Cursor", "b")));

        [Test]
        public void It_treats_them_as_a_repeat() =>
            AssertParameterValidationFailed("Cursor must not be supplied more than once.");
    }

    [TestFixture]
    [Parallelizable]
    public class Given_All_Four_Parameters_Repeated
        : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [SetUp]
        public async Task Setup() =>
            await Execute(
                Query(("dataStoreId", "3788")),
                repeatedNames: ["contractversion", "Cursor", "LIMIT", "DataStoreID"]
            );

        [Test]
        public void It_names_each_in_parameter_order() =>
            AssertParameterValidationFailed(
                "DataStoreId must not be supplied more than once.",
                "Limit must not be supplied more than once.",
                "Cursor must not be supplied more than once.",
                "ContractVersion must not be supplied more than once."
            );
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repeated_Unknown_Parameter
        : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [SetUp]
        public async Task Setup() => await Execute(Query(("dataStoreId", "3788")), repeatedNames: ["offset"]);

        [Test]
        public void It_ignores_the_repeat() => NextCalled.Should().BeTrue();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Repeat_And_An_Invalid_Value_Together
        : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [SetUp]
        public async Task Setup() =>
            await Execute(Query(("limit", "0"), ("contractVersion", "v9")), repeatedNames: ["limit"]);

        [Test]
        public void It_answers_the_repeat_alone() =>
            AssertParameterValidationFailed("Limit must not be supplied more than once.");
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Invalid_Data_Store_Id : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [TestCase(null, TestName = "It_rejects_a_missing_data_store_id")]
        [TestCase("", TestName = "It_rejects_an_empty_data_store_id")]
        [TestCase("0", TestName = "It_rejects_a_zero_data_store_id")]
        [TestCase("-1", TestName = "It_rejects_a_negative_data_store_id")]
        [TestCase("+1", TestName = "It_rejects_a_plus_signed_data_store_id")]
        [TestCase(" 1", TestName = "It_rejects_whitespace_in_the_data_store_id")]
        [TestCase("1.0", TestName = "It_rejects_a_decimal_data_store_id")]
        [TestCase("abc", TestName = "It_rejects_a_non_numeric_data_store_id")]
        [TestCase("2147483648", TestName = "It_rejects_a_data_store_id_beyond_int32")]
        public async Task It_rejects_the_request(string? dataStoreId)
        {
            await Execute(dataStoreId is null ? Query() : Query(("dataStoreId", dataStoreId)));

            AssertParameterValidationFailed(DataStoreIdMessage);
        }

        [Test]
        public async Task It_accepts_the_int32_maximum()
        {
            await Execute(Query(("dataStoreId", "2147483647")));

            RequestInfo.EducationOrganizationProjectionRequest!.DataStoreId.Should().Be(int.MaxValue);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Limit : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [TestCase("", TestName = "It_rejects_an_empty_limit")]
        [TestCase("0", TestName = "It_rejects_a_zero_limit")]
        [TestCase("-1", TestName = "It_rejects_a_negative_limit")]
        [TestCase("2001", TestName = "It_rejects_a_limit_above_the_default_maximum_page_size")]
        [TestCase("abc", TestName = "It_rejects_a_non_numeric_limit")]
        public async Task It_rejects_an_out_of_range_value(string limit)
        {
            await Execute(Query(("dataStoreId", "3788"), ("limit", limit)));

            AssertParameterValidationFailed(LimitMessage(2000));
        }

        [TestCase("1", 1)]
        [TestCase("2000", 2000)]
        public async Task It_accepts_each_inclusive_bound(string limit, int expected)
        {
            await Execute(Query(("dataStoreId", "3788"), ("limit", limit)));

            RequestInfo.EducationOrganizationProjectionRequest!.Limit.Should().Be(expected);
        }

        [Test]
        public async Task It_bounds_the_limit_by_the_configured_maximum_page_size()
        {
            EducationOrganizationProjectionSettings settings = new() { MaximumPageSize = 5 };

            await Execute(Query(("dataStoreId", "3788"), ("limit", "6")), settings: settings);
            AssertParameterValidationFailed(LimitMessage(5));

            await Execute(Query(("dataStoreId", "3788")), settings: settings);
            RequestInfo.EducationOrganizationProjectionRequest!.Limit.Should().Be(5);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Invalid_Data_Store_Id_And_Limit_Together
        : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [SetUp]
        public async Task Setup() => await Execute(Query(("dataStoreId", "0"), ("limit", "0")));

        [Test]
        public void It_names_both_in_one_response() =>
            AssertParameterValidationFailed(DataStoreIdMessage, LimitMessage(2000));
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Contract_Version : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [TestCase("", TestName = "It_rejects_an_empty_contract_version")]
        [TestCase("educationOrganizationProjection.v2", TestName = "It_rejects_an_unserved_contract_version")]
        [TestCase("EducationOrganizationProjection.v1", TestName = "It_rejects_another_spelling_of_v1")]
        [TestCase("educationOrganizationProjection.v1 ", TestName = "It_rejects_trailing_whitespace")]
        public async Task It_rejects_an_unsupported_value(string contractVersion)
        {
            await Execute(Query(("dataStoreId", "3788"), ("contractVersion", contractVersion)));

            AssertUnsupportedContractVersion();
            Errors.Should().BeEmpty();
        }

        [Test]
        public async Task It_accepts_the_served_version()
        {
            await Execute(Query(("dataStoreId", "3788"), ("contractVersion", ProjectionContractVersions.V1)));

            RequestInfo
                .EducationOrganizationProjectionRequest!.ContractVersion.Should()
                .Be(ProjectionContractVersions.V1);
        }
    }

    /// <summary>
    /// Order of operations: a request wrong in several ways is answered by the earliest failing group.
    /// Each case would be answered differently if the groups ran in another order.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Faults_In_More_Than_One_Group
        : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [Test]
        public async Task It_answers_a_parameter_fault_before_an_unsupported_contract_version()
        {
            await Execute(Query(("dataStoreId", "3788"), ("limit", "0"), ("contractVersion", "v9")));

            AssertParameterValidationFailed(LimitMessage(2000));
        }

        [Test]
        public async Task It_answers_a_parameter_fault_before_an_invalid_cursor()
        {
            await Execute(Query(("dataStoreId", "0"), ("cursor", "garbage")));

            AssertParameterValidationFailed(DataStoreIdMessage);
        }

        [Test]
        public async Task It_answers_an_unsupported_contract_version_before_an_invalid_cursor()
        {
            await Execute(Query(("dataStoreId", "3788"), ("contractVersion", "v9"), ("cursor", "garbage")));

            AssertUnsupportedContractVersion();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Continuation_Cursor_Later_In_The_Walk
        : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            Clock.Advance(TimeSpan.FromMinutes(30));

            await Execute(
                Query(
                    ("dataStoreId", "3788"),
                    ("limit", "2"),
                    ("cursor", ProjectionCursorFixtures.WorkedNextCursor)
                )
            );
        }

        [Test]
        public void It_continues_the_pipeline() => NextCalled.Should().BeTrue();

        [Test]
        public void It_records_the_decoded_cursor()
        {
            RequestInfo
                .EducationOrganizationProjectionRequest!.Cursor.Should()
                .Be(
                    new ProjectionCursor(
                        3788,
                        10,
                        ProjectionCursorFixtures.WorkedDigest,
                        ProjectionCursorFixtures.WorkedWalkIssuedAt,
                        ProjectionCursorFixtures.WorkedBinding
                    )
                );
        }

        [Test]
        public void It_preserves_the_original_walk_timestamp_rather_than_the_current_time()
        {
            RequestInfo
                .EducationOrganizationProjectionRequest!.Cursor!.WalkIssuedAtUnixSeconds.Should()
                .Be(ProjectionCursorFixtures.WorkedWalkIssuedAt)
                .And.NotBe(Clock.GetUtcNow().ToUnixTimeSeconds());
        }
    }

    /// <summary>
    /// The binding is computed from the request being answered, not from anything the cursor says, so
    /// replaying a cursor in another tenant or route context is refused.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Cursor_Replayed_In_Another_Context
        : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        private static Dictionary<string, string> CursorQuery() =>
            Query(("dataStoreId", "3788"), ("cursor", ProjectionCursorFixtures.WorkedNextCursor));

        [Test]
        public async Task It_accepts_it_in_the_context_that_issued_it_in_any_letter_case()
        {
            Dictionary<RouteQualifierName, RouteQualifierValue> upperCaseKeys = new()
            {
                [new RouteQualifierName("DISTRICTID")] = new RouteQualifierValue("255901"),
                [new RouteQualifierName("SchoolYear")] = new RouteQualifierValue("2025"),
            };

            await Execute(CursorQuery(), tenant: "TENANT_255901", routeQualifiers: upperCaseKeys);

            NextCalled.Should().BeTrue();
        }

        [Test]
        public async Task It_rejects_it_for_another_tenant()
        {
            await Execute(CursorQuery(), tenant: "Tenant_255902");

            AssertInvalidCursor();
        }

        [Test]
        public async Task It_rejects_it_in_single_tenant_mode()
        {
            await Execute(CursorQuery(), tenant: null);

            AssertInvalidCursor();
        }

        [Test]
        public async Task It_rejects_it_for_another_route_qualifier_value()
        {
            Dictionary<RouteQualifierName, RouteQualifierValue> otherYear =
                ProjectionCursorFixtures.WorkedQualifiers();
            otherYear[new RouteQualifierName("schoolYear")] = new RouteQualifierValue("2024");

            await Execute(CursorQuery(), routeQualifiers: otherYear);

            AssertInvalidCursor();
        }

        [Test]
        public async Task It_rejects_it_without_route_qualifiers()
        {
            await Execute(CursorQuery(), routeQualifiers: []);

            AssertInvalidCursor();
        }

        [Test]
        public async Task It_rejects_it_for_another_data_store()
        {
            await Execute(
                Query(("dataStoreId", "3789"), ("cursor", ProjectionCursorFixtures.WorkedNextCursor))
            );

            AssertInvalidCursor();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Cursor_And_The_Clock : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        private Task ExecuteWithWorkedCursor(EducationOrganizationProjectionSettings? settings = null) =>
            Execute(
                Query(("dataStoreId", "3788"), ("cursor", ProjectionCursorFixtures.WorkedNextCursor)),
                settings: settings
            );

        /// <summary>
        /// The worked cursor with its walk timestamp moved, because the fake clock cannot go back.
        /// </summary>
        private Task ExecuteWithCursorIssuedAt(long walkIssuedAt) =>
            Execute(
                Query(
                    ("dataStoreId", "3788"),
                    (
                        "cursor",
                        ProjectionCursorFixtures.CursorFor(
                            ProjectionCursorFixtures.ValidPayload(
                                walkIssuedAt: walkIssuedAt.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture
                                )
                            )
                        )
                    )
                )
            );

        [Test]
        public async Task It_accepts_the_cursor_at_the_end_of_the_default_lifetime()
        {
            Clock.Advance(TimeSpan.FromMinutes(60));

            await ExecuteWithWorkedCursor();

            NextCalled.Should().BeTrue();
        }

        [Test]
        public async Task It_rejects_the_cursor_one_second_after_the_default_lifetime()
        {
            Clock.Advance(TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(1));

            await ExecuteWithWorkedCursor();

            AssertInvalidCursor();
        }

        [Test]
        public async Task It_uses_the_configured_cursor_lifetime()
        {
            Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

            await ExecuteWithWorkedCursor(
                new EducationOrganizationProjectionSettings { CursorLifetimeMinutes = 5 }
            );

            AssertInvalidCursor();
        }

        [Test]
        public async Task It_rejects_a_cursor_dated_more_than_sixty_seconds_ahead()
        {
            await ExecuteWithCursorIssuedAt(ProjectionCursorFixtures.WorkedWalkIssuedAt + 61);

            AssertInvalidCursor();
        }

        [Test]
        public async Task It_accepts_a_cursor_dated_sixty_seconds_ahead()
        {
            await ExecuteWithCursorIssuedAt(ProjectionCursorFixtures.WorkedWalkIssuedAt + 60);

            NextCalled.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Undecodable_Cursor : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        private const string HostileCursor = "Server=db;Password=hunter2";

        [SetUp]
        public async Task Setup() => await Execute(Query(("dataStoreId", "3788"), ("cursor", HostileCursor)));

        [Test]
        public void It_answers_the_fixed_invalid_cursor_problem()
        {
            AssertInvalidCursor();
            JsonNode
                .DeepEquals(
                    Body,
                    JsonNode.Parse(
                        """
                        {
                          "detail": "The cursor is not valid for this request. Restart the read without a cursor.",
                          "type": "urn:ed-fi:api:education-organization-projection:invalid-cursor",
                          "title": "Invalid Cursor",
                          "status": 400,
                          "correlationId": "projection-parse",
                          "validationErrors": {},
                          "errors": []
                        }
                        """
                    )
                )
                .Should()
                .BeTrue();
        }

        [Test]
        public void It_logs_the_reason_but_never_the_cursor_text()
        {
            Logger
                .Records.Should()
                .Contain(record =>
                    record.Properties.ContainsKey("CursorRejection")
                    && Equals(record.Properties["CursorRejection"], ProjectionCursorRejection.Malformed)
                );
            Logger.Records.Should().NotContain(record => record.Message.Contains("hunter2"));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Empty_Cursor : ParseEducationOrganizationProjectionRequestMiddlewareTests
    {
        [SetUp]
        public async Task Setup() => await Execute(Query(("dataStoreId", "3788"), ("cursor", "")));

        [Test]
        public void It_rejects_it_rather_than_starting_a_new_walk() => AssertInvalidCursor();
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_FrontendRequest_Built_Without_Repeated_Parameter_Names
{
    [Test]
    public void It_defaults_to_no_repeated_names_so_existing_callers_are_unchanged()
    {
        FrontendRequest request = new(
            Path: "/ed-fi/schools",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("t"),
            RouteQualifiers: []
        );

        request.RepeatedQueryParameterNames.Should().BeEmpty();
        (request with { Path = "/ed-fi/students" }).RepeatedQueryParameterNames.Should().BeEmpty();
    }

    [Test]
    public void It_carries_supplied_names_through_a_with_expression()
    {
        FrontendRequest request = new(
            Path: "/management/education-organizations",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("t"),
            RouteQualifiers: []
        )
        {
            RepeatedQueryParameterNames = ["Limit"],
        };

        (request with { Tenant = "a" }).RepeatedQueryParameterNames.Should().Equal("Limit");
    }
}
