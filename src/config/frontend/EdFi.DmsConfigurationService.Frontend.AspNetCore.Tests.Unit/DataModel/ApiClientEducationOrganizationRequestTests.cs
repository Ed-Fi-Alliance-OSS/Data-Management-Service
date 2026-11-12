// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.DataModel;

/// <summary>
/// The education organization fields of the API client create and update requests, deserialized
/// from JSON as the endpoints bind them, so an omitted field and an explicit null are told apart.
/// </summary>
public class ApiClientEducationOrganizationRequestTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static string[] InsertErrors(string educationOrganizationProperties) =>
        Errors(
            JsonSerializer.Deserialize<ApiClientInsertCommand>(
                $$"""{ "applicationId": 1, "name": "Client", "isApproved": true{{educationOrganizationProperties}} }""",
                WebOptions
            )!,
            new ApiClientInsertCommand.Validator()
        );

    private static string[] UpdateErrors(string educationOrganizationProperties) =>
        Errors(
            JsonSerializer.Deserialize<ApiClientUpdateCommand>(
                $$"""{ "id": 1, "applicationId": 1, "name": "Client", "isApproved": true{{educationOrganizationProperties}} }""",
                WebOptions
            )!,
            new ApiClientUpdateCommand.Validator()
        );

    private static string[] Errors<T>(T command, FluentValidation.IValidator<T> validator) =>
        [.. validator.Validate(command).Errors.Select(error => error.PropertyName)];

    [TestFixture]
    public class Given_requests_the_rules_allow_without_an_Application
        : ApiClientEducationOrganizationRequestTests
    {
        [TestCase("")]
        [TestCase(""", "educationOrganizationIds": [1001] """)]
        [TestCase(
            """, "educationOrganizationIds": [1001], "inheritsApplicationEducationOrganizations": false """
        )]
        [TestCase(
            """, "educationOrganizationIds": [1001], "inheritsApplicationEducationOrganizations": true """
        )]
        [TestCase(""", "inheritsApplicationEducationOrganizations": true """)]
        public void It_accepts_the_create(string properties) => InsertErrors(properties).Should().BeEmpty();

        [TestCase("")]
        [TestCase(""", "educationOrganizationIds": [1001] """)]
        [TestCase(""", "inheritsApplicationEducationOrganizations": true """)]
        public void It_accepts_the_update(string properties) => UpdateErrors(properties).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_requests_the_rules_reject_without_an_Application
        : ApiClientEducationOrganizationRequestTests
    {
        [TestCase(""", "educationOrganizationIds": [] """, "EducationOrganizationIds")]
        [TestCase(
            """, "educationOrganizationIds": [], "inheritsApplicationEducationOrganizations": true """,
            "EducationOrganizationIds"
        )]
        [TestCase(""", "educationOrganizationIds": null """, "EducationOrganizationIds")]
        [TestCase(
            """, "inheritsApplicationEducationOrganizations": false """,
            "InheritsApplicationEducationOrganizations"
        )]
        [TestCase(
            """, "inheritsApplicationEducationOrganizations": null """,
            "InheritsApplicationEducationOrganizations"
        )]
        public void It_rejects_the_create_on_the_field(string properties, string field) =>
            InsertErrors(properties).Should().Equal(field);

        [TestCase(""", "educationOrganizationIds": [] """, "EducationOrganizationIds")]
        [TestCase(""", "educationOrganizationIds": null """, "EducationOrganizationIds")]
        [TestCase(
            """, "inheritsApplicationEducationOrganizations": false """,
            "InheritsApplicationEducationOrganizations"
        )]
        [TestCase(
            """, "inheritsApplicationEducationOrganizations": null """,
            "InheritsApplicationEducationOrganizations"
        )]
        public void It_rejects_the_update_on_the_field(string properties, string field) =>
            UpdateErrors(properties).Should().Equal(field);
    }

    [TestFixture]
    public class Given_a_request_body : ApiClientEducationOrganizationRequestTests
    {
        [Test]
        public void It_tracks_an_omitted_list_as_not_supplied()
        {
            var command = JsonSerializer.Deserialize<ApiClientInsertCommand>(
                """{ "applicationId": 1, "name": "Client", "isApproved": true }""",
                WebOptions
            )!;

            command.EducationOrganizationIdsSupplied.Should().BeFalse();
            command.InheritsApplicationEducationOrganizationsSupplied.Should().BeFalse();
        }

        [Test]
        public void It_tracks_sent_fields_as_supplied()
        {
            var command = JsonSerializer.Deserialize<ApiClientUpdateCommand>(
                """{ "id": 1, "applicationId": 1, "name": "Client", "isApproved": true, "educationOrganizationIds": [9007199254740993], "inheritsApplicationEducationOrganizations": false }""",
                WebOptions
            )!;

            command.EducationOrganizationIdsSupplied.Should().BeTrue();
            command.EducationOrganizationIds.Should().Equal(9007199254740993);
            command.InheritsApplicationEducationOrganizationsSupplied.Should().BeTrue();
            command.InheritsApplicationEducationOrganizations.Should().BeFalse();
        }

        [Test]
        public void It_does_not_let_the_body_complete_a_pending_synchronization()
        {
            var command = JsonSerializer.Deserialize<ApiClientUpdateCommand>(
                """{ "id": 1, "applicationId": 1, "name": "Client", "isApproved": true, "completesIdentityProviderSync": true, "CompletesIdentityProviderSync": true }""",
                WebOptions
            )!;

            command.CompletesIdentityProviderSync.Should().BeFalse();
        }
    }
}
