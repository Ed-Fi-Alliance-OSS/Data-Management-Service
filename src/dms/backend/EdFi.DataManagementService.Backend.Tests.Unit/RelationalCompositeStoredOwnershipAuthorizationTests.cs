// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Owns how the stored ownership check joins a composite command: where its statement lands relative to the
/// other stored checks, what happens when it does not fit the command's parameter budget, and which family
/// claims a provider failure the command aborted with.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Composite_Stored_Ownership_Authorization
{
    // The allocator issues statement-scoped names, and the builder refuses a parameter it did not issue, so
    // the capture predicate has to reference the allocated name.
    private const string TargetPredicate = "d.\"DocumentUuid\" = @documentUuid_s0";
    private const string CustomViewStrategyName = "SchoolWithCompositeOwnershipTest";

    /// <summary>
    /// Statement order is precedence order, because the command aborts at its first AUTH1. auth.md puts the
    /// custom views and NamespaceBased ahead of OwnershipBased, and OwnershipBased ahead of the relationship
    /// OR group, so the emitted order has to be exactly this.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_emits_ownership_after_the_and_filters_and_before_the_relationship_check(SqlDialect dialect)
    {
        var builder = CreateBuilderWithCapture(dialect);
        var mappingSet = CreateMappingSet(dialect);

        RelationalCompositeStoredAuthorization.AppendCustomViewRun(
            builder,
            builder.Carrier,
            mappingSet,
            CreateCustomViewChecks()
        );
        RelationalCompositeStoredAuthorization
            .TryAppendNamespace(
                builder,
                builder.Carrier,
                mappingSet,
                CreateNamespaceAuthorization(dialect),
                out _
            )
            .Should()
            .BeTrue();
        RelationalCompositeStoredAuthorization
            .TryAppendOwnership(
                builder,
                builder.Carrier,
                mappingSet,
                CreateOwnershipAuthorization(dialect),
                out var ownershipPlan
            )
            .Should()
            .BeTrue();

        RelationalCompositeStoredAuthorization
            .TryAppendRelationship(
                builder,
                builder.Carrier,
                mappingSet,
                RelationalCompositeStoredAuthorization.Classify(CreateAuthorizedRelationship(dialect)),
                emittedAuth1Index: 0,
                DefaultRelationalParameterConfigurator.Instance,
                out var relationshipPlan
            )
            .Should()
            .BeTrue();

        ownershipPlan.Should().NotBeNull();
        relationshipPlan.Disposition.Should().Be(StoredRelationshipDisposition.Emitted);
        builder
            .Seal()
            .StatementsInOrder.Select(static statement => statement.Label)
            .Should()
            .ContainInOrder(
                RelationalCompositeStoredAuthorization.CustomViewLabel,
                RelationalCompositeStoredAuthorization.NamespaceLabel,
                RelationalCompositeStoredAuthorization.OwnershipLabel,
                RelationalCompositeStoredAuthorization.RelationshipLabel
            );
    }

    /// <summary>
    /// The check carries the carrier's row guard, which is what makes it vacuous when the capture observed
    /// no target — the property a create relies on to never be denied by ownership.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_guards_the_emitted_ownership_statement_with_the_captured_target_predicate(
        SqlDialect dialect
    )
    {
        var builder = CreateBuilderWithCapture(dialect);

        RelationalCompositeStoredAuthorization
            .TryAppendOwnership(
                builder,
                builder.Carrier,
                CreateMappingSet(dialect),
                CreateOwnershipAuthorization(dialect),
                out _
            )
            .Should()
            .BeTrue();

        var statement = builder
            .Seal()
            .StatementsInOrder.Single(static statement =>
                statement.Label == RelationalCompositeStoredAuthorization.OwnershipLabel
            );
        statement.Sql.Should().Contain(builder.Carrier.CapturedTargetPresentPredicate);
        // The DocumentId parameter is substituted away by the carrier expression, so the statement binds
        // only its ownership tokens.
        statement
            .Parameters.Should()
            .OnlyContain(parameter =>
                !parameter.Name.Contains("documentId", StringComparison.OrdinalIgnoreCase)
            );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_appends_nothing_when_no_ownership_check_was_planned(SqlDialect dialect)
    {
        var builder = CreateBuilderWithCapture(dialect);
        var statementsBefore = builder.StatementCount;

        RelationalCompositeStoredAuthorization
            .TryAppendOwnership(builder, builder.Carrier, CreateMappingSet(dialect), null, out var plan)
            .Should()
            .BeTrue();

        plan.Should().BeNull();
        builder.StatementCount.Should().Be(statementsBefore);
    }

    /// <summary>
    /// A token list that does not fit the remaining budget reports false rather than throwing, so the caller
    /// can run ownership as an ordered segment. That degradation is safe here and not for a custom-view run:
    /// every other AND filter precedes ownership either way, so a segment after the command keeps the order.
    /// </summary>
    [Test]
    public void It_reports_a_non_fit_rather_than_throwing()
    {
        // SQL Server binds one scalar per token. The capture statement's own parameter already occupies the
        // single available slot, so no token scalar can fit.
        var builder = CreateBuilderWithCapture(
            SqlDialect.Mssql,
            new RelationalCommandBudget(MaxParametersPerCommand: 1, MaxRowsPerStatement: 1000)
        );

        RelationalCompositeStoredAuthorization
            .TryAppendOwnership(
                builder,
                builder.Carrier,
                CreateMappingSet(SqlDialect.Mssql),
                CreateOwnershipAuthorization(SqlDialect.Mssql, [3, 5, 7]),
                out var plan
            )
            .Should()
            .BeFalse();

        plan.Should().BeNull();
        builder
            .Seal()
            .StatementsInOrder.Should()
            .NotContain(statement =>
                statement.Label == RelationalCompositeStoredAuthorization.OwnershipLabel
            );
    }

    // ----- Denial classification --------------------------------------------

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_classifies_an_ownership_mismatch_payload_as_an_ownership_denial(SqlDialect dialect)
    {
        var denial = Classify(
            dialect,
            OwnershipPayload(1, OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch),
            ownershipPlan: new StoredOwnershipStatementPlan(new OwnershipAuthorizationCheckSpec(1))
        );

        denial
            .Should()
            .BeOfType<StoredAuthorizationDenial.OwnershipNotAuthorized>()
            .Which.Failure.FailureKind.Should()
            .Be(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch);
    }

    [Test]
    public void It_classifies_an_uninitialized_stored_token_payload_as_its_own_denial_kind()
    {
        var denial = Classify(
            SqlDialect.Pgsql,
            OwnershipPayload(0, OwnershipAuthorizationAuth1FailureKind.StoredOwnershipTokenUninitialized),
            ownershipPlan: new StoredOwnershipStatementPlan(new OwnershipAuthorizationCheckSpec(0))
        );

        denial
            .Should()
            .BeOfType<StoredAuthorizationDenial.OwnershipNotAuthorized>()
            .Which.Failure.FailureKind.Should()
            .Be(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized);
    }

    /// <summary>
    /// Unreachable for a locked write or delete, which row-locks the target before the check, so it is
    /// classified as the shared stale-target denial the caller already maps to its own conflict outcome
    /// rather than being reported as an ownership 403 for a row that is gone.
    /// </summary>
    [Test]
    public void It_classifies_a_stale_target_payload_as_the_shared_stale_target_denial()
    {
        var denial = Classify(
            SqlDialect.Pgsql,
            OwnershipPayload(0, OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing),
            ownershipPlan: new StoredOwnershipStatementPlan(new OwnershipAuthorizationCheckSpec(0))
        );

        denial.Should().BeOfType<StoredAuthorizationDenial.StaleTarget>();
    }

    /// <summary>
    /// The gap this step exists to close. A malformed own1 payload is claimed by ownership rather than
    /// reported as a namespace invalid-metadata failure, even on a command carrying both families' checks.
    /// Both outcomes are a 500, so only the diagnostic distinguishes them — which is exactly what an
    /// operator would use to find the defect.
    /// </summary>
    [TestCase("own1|")]
    [TestCase("own1|x|m")]
    [TestCase("own1|0|zzz")]
    public void It_attributes_a_malformed_ownership_payload_to_ownership_not_namespace(
        string malformedPayload
    )
    {
        var dialect = SqlDialect.Pgsql;
        var denial = Classify(
            dialect,
            malformedPayload,
            namespacePlan: new StoredNamespaceStatementPlan(
                CreateNamespaceAuthorization(dialect).Checks,
                CreateNamespaceAuthorization(dialect).NamespacePrefixParameterization
            ),
            ownershipPlan: new StoredOwnershipStatementPlan(new OwnershipAuthorizationCheckSpec(0))
        );

        denial
            .Should()
            .BeOfType<StoredAuthorizationDenial.SecurityConfiguration>()
            .Which.Diagnostics.Should()
            .ContainSingle()
            .Which.Should()
            .Match<SecurityConfigurationFailureDiagnostic>(diagnostic =>
                diagnostic.ProviderOrPlannerFailureKind
                    == AuthorizationSecurityConfigurationDiagnostics.OwnershipAuth1PayloadMappingFailed
                && diagnostic.ConfiguredStrategyNames.Contains(
                    AuthorizationStrategyNameConstants.OwnershipBased
                )
            );
    }

    /// <summary>
    /// The same malformed payload with no ownership check planned. It still cannot be namespace's — only
    /// ownership emits own1 — so it fails closed under ownership's diagnostic rather than being filed
    /// against NamespaceBased.
    /// </summary>
    [Test]
    public void It_attributes_a_malformed_ownership_payload_to_ownership_even_with_no_ownership_plan()
    {
        var dialect = SqlDialect.Pgsql;
        var namespaceAuthorization = CreateNamespaceAuthorization(dialect);

        var denial = Classify(
            dialect,
            "own1|x|m",
            namespacePlan: new StoredNamespaceStatementPlan(
                namespaceAuthorization.Checks,
                namespaceAuthorization.NamespacePrefixParameterization
            ),
            ownershipPlan: null
        );

        denial
            .Should()
            .BeOfType<StoredAuthorizationDenial.SecurityConfiguration>()
            .Which.Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.OwnershipAuth1PayloadMappingFailed);
    }

    /// <summary>
    /// Consulting ownership first must not let it claim another family's payload. A namespace mismatch stays
    /// a namespace denial with an ownership plan present.
    /// </summary>
    [Test]
    public void It_leaves_a_namespace_payload_to_namespace_even_with_an_ownership_plan()
    {
        var dialect = SqlDialect.Pgsql;
        var namespaceAuthorization = CreateNamespaceAuthorization(dialect);

        var denial = Classify(
            dialect,
            NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
                new NamespaceAuthorizationAuth1FailurePayload(
                    0,
                    NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
                )
            ),
            namespacePlan: new StoredNamespaceStatementPlan(
                namespaceAuthorization.Checks,
                namespaceAuthorization.NamespacePrefixParameterization
            ),
            ownershipPlan: new StoredOwnershipStatementPlan(new OwnershipAuthorizationCheckSpec(0))
        );

        denial.Should().BeOfType<StoredAuthorizationDenial.NamespaceNotAuthorized>();
    }

    /// <summary>
    /// A payload with no recognizable discriminator keeps its existing home. Ownership declines it, so the
    /// namespace catch-all still reports it and the diagnostic is not lost to a family that yielded.
    /// </summary>
    [Test]
    public void It_leaves_a_payload_no_family_owns_to_the_namespace_catch_all()
    {
        var dialect = SqlDialect.Pgsql;
        var namespaceAuthorization = CreateNamespaceAuthorization(dialect);

        var denial = Classify(
            dialect,
            "zzz|0|m",
            namespacePlan: new StoredNamespaceStatementPlan(
                namespaceAuthorization.Checks,
                namespaceAuthorization.NamespacePrefixParameterization
            ),
            ownershipPlan: new StoredOwnershipStatementPlan(new OwnershipAuthorizationCheckSpec(0))
        );

        denial
            .Should()
            .BeOfType<StoredAuthorizationDenial.SecurityConfiguration>()
            .Which.Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.NamespaceInvalidAuth1Payload);
    }

    [Test]
    public void It_ignores_a_provider_failure_that_carries_no_auth1_payload()
    {
        Classify(
                SqlDialect.Pgsql,
                providerMessage: "duplicate key value violates unique constraint",
                providerErrorCode: "23505",
                ownershipPlan: new StoredOwnershipStatementPlan(new OwnershipAuthorizationCheckSpec(0))
            )
            .Should()
            .BeNull();
    }

    // ----- Helpers ----------------------------------------------------------

    private static StoredAuthorizationDenial? Classify(
        SqlDialect dialect,
        string providerMessage,
        string? providerErrorCode = null,
        StoredNamespaceStatementPlan? namespacePlan = null,
        StoredOwnershipStatementPlan? ownershipPlan = null
    ) =>
        RelationalCompositeStoredAuthorization.TryClassifyDenial(
            dialect,
            new StoredAuthorizationStubDbException("provider exception"),
            namespacePlan,
            relationshipPlan: null,
            emittedAuth1Index: 0,
            new StoredAuthorizationStubProviderFailureExtractor(
                providerErrorCode ?? OwnershipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                BuildProviderMessage(dialect, providerMessage, providerErrorCode)
            ),
            NullLogger.Instance,
            customViewPlan: null,
            ownershipPlan
        );

    /// <remarks>
    /// PostgreSQL carries the payload as the message with AUTH1 as the SqlState; SQL Server has no custom
    /// SqlState and carries it inside the message. A non-AUTH1 failure is passed through untouched.
    /// </remarks>
    private static string BuildProviderMessage(
        SqlDialect dialect,
        string providerMessage,
        string? providerErrorCode
    )
    {
        if (providerErrorCode is not null)
        {
            return providerMessage;
        }

        return dialect is SqlDialect.Mssql
            ? $"{OwnershipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode} - {providerMessage}"
            : providerMessage;
    }

    private static string OwnershipPayload(
        int configuredStrategyIndex,
        OwnershipAuthorizationAuth1FailureKind failureKind
    ) =>
        OwnershipAuthorizationAuth1FailurePayloadCodec.Encode(
            new OwnershipAuthorizationAuth1FailurePayload(configuredStrategyIndex, failureKind)
        );

    private static RelationalCompositeCommandBuilder CreateBuilderWithCapture(
        SqlDialect dialect,
        RelationalCommandBudget? budget = null
    )
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(dialect),
            budget
        );
        builder.AppendCaptureTarget(
            TargetPredicate,
            [
                new RelationalParameter(
                    builder.Allocator.AllocateStatementScoped("documentUuid", 0),
                    Guid.NewGuid()
                ),
            ]
        );

        return builder;
    }

    /// <remarks>
    /// A stored self-basis custom view, so the fixture needs no reference model. Its emitted statement binds
    /// only the DocumentId the carrier substitutes away, which is what lets a run co-batch with no budget
    /// fallback at all.
    /// </remarks>
    private static IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> CreateCustomViewChecks() =>
        [
            new SingleRecordCustomViewAuthorizationCheckSpec(
                new ConfiguredAuthorizationStrategy(CustomViewStrategyName, 0),
                0,
                CustomViewAuthorizationCheckValueSource.Stored,
                new DbTableName(new DbSchemaName("auth"), CustomViewStrategyName),
                new DbColumnName("DocumentId"),
                [
                    new ColumnPathStep(
                        new DbTableName(new DbSchemaName("edfi"), "School"),
                        new DbColumnName("DocumentId"),
                        null,
                        null
                    ),
                ],
                new CustomViewAuthorizationCheckTarget.Stored(
                    new DbTableName(new DbSchemaName("edfi"), "School"),
                    new DbColumnName("DocumentId")
                ),
                new QualifiedResourceName("Ed-Fi", "School"),
                [$"{CustomViewStrategyName}Element"],
                $"You may need a {CustomViewStrategyName} hint."
            ),
        ];

    private static RelationalOwnershipAuthorization CreateOwnershipAuthorization(
        SqlDialect dialect,
        IReadOnlyList<short>? ownershipTokenIds = null
    ) =>
        new(
            new OwnershipAuthorizationCheckSpec(0),
            OwnershipTokenParameterizationFactory.Create(
                dialect,
                ownershipTokenIds ?? [11],
                "ownershipTokenIds"
            )
        );

    private static RelationalWriteNamespaceAuthorization CreateNamespaceAuthorization(SqlDialect dialect) =>
        new(
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Stored,
                    new DbTableName(new DbSchemaName("edfi"), "School"),
                    new DbColumnName("Name")
                ),
            ],
            NamespacePrefixParameterizationFactory.Create(dialect, ["uri://ed-fi.org/"], "namespacePrefixes")
        );

    /// <remarks>
    /// One stored EdOrg subject, which is the smallest shape that emits a co-batchable relationship
    /// statement. A PostgreSQL/SQL Server scalar claim parameterization keeps the disposition Emitted rather
    /// than Standalone, which a table-valued parameter would force.
    /// </remarks>
    private static RelationshipAuthorizationResult.Authorized CreateAuthorizedRelationship(SqlDialect dialect)
    {
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var resource = new QualifiedResourceName("Ed-Fi", "School");

        return new RelationshipAuthorizationResult.Authorized(
            [
                new RelationshipAuthorizationCheckSpec(
                    new ConfiguredAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                        RawConfiguredIndex: 2
                    ),
                    RelationshipLocalOrder: 0,
                    RelationshipAuthorizationHierarchyDirection.Normal,
                    RelationshipAuthorizationValueSource.Stored,
                    [
                        new RelationshipAuthorizationSubject(
                            resource,
                            rootTable,
                            new DbColumnName("SchoolId"),
                            RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                                RelationshipAuthorizationHierarchyDirection.Normal
                            ),
                            [
                                new RelationshipAuthorizationSubjectContributor(
                                    SecurableElementKind.EducationOrganization,
                                    "$.schoolId",
                                    "SchoolId"
                                ),
                            ]
                        ),
                    ],
                    new RelationshipAuthorizationCheckTarget.Stored(rootTable, new DbColumnName("DocumentId"))
                ),
            ],
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                dialect,
                [255901L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );
    }

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var rootPlan = Given_Default_Relational_Write_Executor.CreateRootPlan();

        return Given_Default_Relational_Write_Executor.CreateMappingSet(
            Given_Default_Relational_Write_Executor.CreateRelationalResourceModel(rootPlan.TableModel),
            [rootPlan],
            dialect
        );
    }

    private sealed class StoredAuthorizationStubDbException(string message) : DbException(message);

    private sealed class StoredAuthorizationStubProviderFailureExtractor(
        string? providerErrorCode,
        string providerMessage
    ) : IRelationshipAuthorizationProviderFailureExtractor
    {
        public RelationshipAuthorizationProviderFailure Extract(DbException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            return new RelationshipAuthorizationProviderFailure(providerErrorCode, providerMessage);
        }
    }
}
