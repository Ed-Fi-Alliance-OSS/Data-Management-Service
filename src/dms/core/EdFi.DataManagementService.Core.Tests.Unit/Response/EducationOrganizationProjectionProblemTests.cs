// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Response;

/// <summary>
/// Each projection-owned problem body must equal its contract example
/// (reference/design/edorg-projection-DMS-1440/contract/examples) apart from the correlation id.
/// The literals are restated here rather than read from the problem catalog, so a wording change in
/// the catalog fails a test instead of silently changing the contract.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Education_Organization_Projection_Problems
{
    private const string Prefix = "urn:ed-fi:api:education-organization-projection";

    private static readonly TraceId _traceId = new("projection-problem");

    private static IEnumerable<TestCaseData> ContractExamples()
    {
        TestCaseData Case(
            EducationOrganizationProjectionProblem problem,
            int status,
            string type,
            string title,
            string detail
        ) =>
            new TestCaseData(problem, status, type, title, detail).SetName(
                $"It_renders_{title.Replace(' ', '_')}"
            );

        yield return Case(
            EducationOrganizationProjectionProblem.InvalidCursor,
            400,
            $"{Prefix}:invalid-cursor",
            "Invalid Cursor",
            "The cursor is not valid for this request. Restart the read without a cursor."
        );
        yield return Case(
            EducationOrganizationProjectionProblem.UnsupportedContractVersion,
            400,
            $"{Prefix}:unsupported-contract-version",
            "Unsupported Contract Version",
            "The requested contract version is not supported."
        );
        yield return Case(
            EducationOrganizationProjectionProblem.TargetNotFound,
            404,
            $"{Prefix}:target-not-found",
            "Target Not Found",
            "The data store could not be found."
        );
        yield return Case(
            EducationOrganizationProjectionProblem.TargetProviderUnsupported,
            409,
            $"{Prefix}:target-provider-unsupported",
            "Target Provider Unsupported",
            "The data store uses a database provider that this Ed-Fi API deployment does not serve."
        );
        yield return Case(
            EducationOrganizationProjectionProblem.TargetSchemaIncompatible,
            409,
            $"{Prefix}:target-schema-incompatible",
            "Target Schema Incompatible",
            "The data store's database schema is not compatible with this Ed-Fi API deployment."
        );
        yield return Case(
            EducationOrganizationProjectionProblem.ProjectionUnsupported,
            409,
            $"{Prefix}:projection-unsupported",
            "Projection Unsupported",
            "The education organization projection is not supported by the data model in use."
        );
        yield return Case(
            EducationOrganizationProjectionProblem.ProjectionTooLarge,
            409,
            $"{Prefix}:projection-too-large",
            "Projection Too Large",
            "The education organization set is larger than this deployment is configured to project."
        );
        yield return Case(
            EducationOrganizationProjectionProblem.ProjectionDataInvalid,
            409,
            $"{Prefix}:projection-data-invalid",
            "Projection Data Invalid",
            "The education organization data contains duplicate identifiers or contradictory relationships."
        );
        yield return Case(
            EducationOrganizationProjectionProblem.ProjectionChanged,
            409,
            $"{Prefix}:projection-changed",
            "Projection Changed",
            "The education organization set changed during the read. Restart the read without a cursor."
        );
        yield return Case(
            EducationOrganizationProjectionProblem.TargetUnavailable,
            503,
            $"{Prefix}:target-unavailable",
            "Target Unavailable",
            "The data store's database is temporarily unavailable. Retry the request later."
        );
        yield return Case(
            EducationOrganizationProjectionProblem.ServiceConfigurationError,
            503,
            "urn:ed-fi:api:service-configuration-error",
            "Service Configuration Error",
            "The data store's database connection is not configured."
        );
        yield return Case(
            EducationOrganizationProjectionProblem.DatabaseNotProvisioned,
            503,
            "urn:ed-fi:api:database-not-provisioned",
            "Database Not Provisioned",
            "The data store's database has not been provisioned."
        );
    }

    [TestCaseSource(nameof(ContractExamples))]
    public void It_renders_the_contract_example_body(
        object problemValue,
        int status,
        string type,
        string title,
        string detail
    )
    {
        // Typed as object because the problem record is internal and a test method is public.
        var problem = (EducationOrganizationProjectionProblem)problemValue;
        problem.Status.Should().Be(status);

        JsonNode expected = new JsonObject
        {
            ["detail"] = detail,
            ["type"] = type,
            ["title"] = title,
            ["status"] = status,
            ["correlationId"] = _traceId.Value,
            ["validationErrors"] = new JsonObject(),
            ["errors"] = new JsonArray(),
        };

        JsonNode actual = FailureResponse.ForEducationOrganizationProjection(problem, _traceId);

        JsonNode.DeepEquals(actual, expected).Should().BeTrue(actual.ToJsonString());
    }

    [Test]
    public void It_lists_every_problem_once()
    {
        EducationOrganizationProjectionProblem.All.Should().HaveCount(ContractExamples().Count());
        EducationOrganizationProjectionProblem
            .All.Select(problem => problem.Type)
            .Should()
            .OnlyHaveUniqueItems();
    }
}
