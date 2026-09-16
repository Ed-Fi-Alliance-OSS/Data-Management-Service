// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Validation;
using EdFi.DataManagementService.SchemaTools.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

/// <summary>
/// What the schema tooling tells an operator when loading fails, and what it exits with.
/// </summary>
/// <remarks>
/// Covers the reserved query parameter collision, which is the arm DMS-1442 added. The CLI reaches
/// normalization through <see cref="ApiSchemaFileLoader"/> rather than through DMS startup, so without
/// this arm a colliding schema would exit with "Unknown normalization failure" while DMS itself named
/// the resource and the field.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class LoadResultErrorHandlerTests
{
    private static ApiSchemaFileLoadResult CollisionResult(params string[] queryFieldNames) =>
        new ApiSchemaFileLoadResult.NormalizationFailureResult(
            new ApiSchemaNormalizationResult.ReservedQueryParameterCollisionResult([
                .. queryFieldNames.Select(
                    queryFieldName => new ApiSchemaNormalizationResult.ReservedQueryParameterCollision(
                        "extension[0]",
                        "tpdm",
                        "candidates",
                        queryFieldName,
                        ReservedQueryParameters.All.Single(reserved =>
                            string.Equals(reserved.Name, queryFieldName, StringComparison.OrdinalIgnoreCase)
                        )
                    )
                ),
            ])
        );

    /// <summary>
    /// Runs the handler with stderr captured, because the operator-facing text is what this arm
    /// exists to produce and it is written there rather than returned.
    /// </summary>
    private static (int ExitCode, string StandardError) Handle(ApiSchemaFileLoadResult result)
    {
        TextWriter original = Console.Error;
        using StringWriter captured = new();

        try
        {
            Console.SetError(captured);

            int exitCode = LoadResultErrorHandler.Handle(NullLogger.Instance, result);

            return (exitCode, captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_A_Reserved_Query_Parameter_Collision : LoadResultErrorHandlerTests
    {
        private int _exitCode;
        private string _standardError = string.Empty;

        [SetUp]
        public void Setup()
        {
            (_exitCode, _standardError) = Handle(CollisionResult("pageSize"));
        }

        [Test]
        public void It_fails_the_command()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_names_the_schema_the_resource_and_the_field()
        {
            _standardError
                .Should()
                .Contain(
                    "Schema 'extension[0]' resource 'tpdm/candidates' declares query field 'pageSize'",
                    "stderr preserves the quoting and the bracketed schema index that the structured-log "
                        + "whitelist would strip"
                );
        }

        [Test]
        public void It_says_what_the_name_is_reserved_for_and_what_to_do()
        {
            _standardError
                .Should()
                .Contain("the cursor paging page size")
                .And.Contain("Rename the colliding property in the MetaEd model");
        }

        [Test]
        public void It_does_not_report_the_generic_unknown_failure()
        {
            _standardError
                .Should()
                .NotContain(
                    "Unknown normalization failure",
                    "the collision has its own arm, so the CLI names the fault the same way DMS startup "
                        + "does rather than leaving an operator with nothing to act on"
                );
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_Several_Reserved_Query_Parameter_Collisions : LoadResultErrorHandlerTests
    {
        [Test]
        public void It_reports_every_one()
        {
            (_, string standardError) = Handle(CollisionResult("pageSize", "number", "minChangeVersion"));

            standardError
                .Should()
                .Contain("'pageSize'")
                .And.Contain("'number'")
                .And.Contain("'minChangeVersion'");
        }

        [Test]
        public void It_keeps_each_collision_on_its_own_line()
        {
            (_, string standardError) = Handle(CollisionResult("pageSize", "number", "minChangeVersion"));

            standardError
                .Split('\n')
                .Where(line => line.Contains("declares query field", StringComparison.Ordinal))
                .Should()
                .HaveCount(3, "stderr output preserves the line breaks the description writes");
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_An_Endpoint_Name_Collision : LoadResultErrorHandlerTests
    {
        /// <summary>
        /// The neighbouring arm, asserted so the new one cannot be shown to work by a change that
        /// broke the existing reporting.
        /// </summary>
        [Test]
        public void It_still_reports_the_endpoint_name_collision()
        {
            (int exitCode, string standardError) = Handle(
                new ApiSchemaFileLoadResult.NormalizationFailureResult(
                    new ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult([
                        new ApiSchemaNormalizationResult.EndpointNameCollision("tpdm", ["extension[0]"]),
                    ])
                )
            );

            exitCode.Should().Be(1);
            standardError.Should().Contain("Endpoint name collision");
        }
    }
}
