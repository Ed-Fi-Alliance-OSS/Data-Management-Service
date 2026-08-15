// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Owns how the standalone custom-view membership run attributes a provider failure that carries no AUTH1
/// payload: a broken <c>auth.{StrategyName}</c> contract is the documented validation 500, while a transient
/// provider failure is never evidence about the view and must reach the caller's retryable handling.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Custom_View_Authorization_Executor
{
    [Test]
    public async Task It_does_not_wrap_a_transient_non_auth1_failure_as_a_custom_view_validation_failure()
    {
        var transientFailure = new FakeDbException("deadlock detected", "40P01");
        var classifier = new ConfigurableRelationalWriteExceptionClassifier
        {
            IsTransientFailureToReturn = true,
        };
        var sut = new CustomViewAuthorizationExecutor(
            new ThrowingCommandExecutor(transientFailure),
            new StubProviderFailureExtractor("40P01", "deadlock detected"),
            writeExceptionClassifier: classifier
        );

        var act = async () => await sut.ExecuteAsync(CreateRequest());

        var assertion = await act.Should().ThrowAsync<DbException>();
        assertion.Which.Should().BeSameAs(transientFailure);
    }

    [Test]
    public async Task It_still_wraps_a_missing_view_failure_as_a_custom_view_validation_failure()
    {
        var missingViewFailure = new FakeDbException("relation does not exist", "42P01");
        var sut = new CustomViewAuthorizationExecutor(
            new ThrowingCommandExecutor(missingViewFailure),
            new StubProviderFailureExtractor("42P01", "relation does not exist"),
            writeExceptionClassifier: new ConfigurableRelationalWriteExceptionClassifier()
        );

        var act = async () => await sut.ExecuteAsync(CreateRequest());

        var assertion = await act.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
        assertion.Which.InnerException.Should().BeSameAs(missingViewFailure);
    }

    /// <summary>One self-basis stored check, so the run emits membership SQL without a reference model.</summary>
    private static CustomViewAuthorizationExecutionRequest CreateRequest()
    {
        var rootPlan = Given_Default_Relational_Write_Executor.CreateRootPlan();
        var mappingSet = Given_Default_Relational_Write_Executor.CreateMappingSet(
            Given_Default_Relational_Write_Executor.CreateRelationalResourceModel(rootPlan.TableModel),
            [rootPlan],
            SqlDialect.Pgsql
        );
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");

        return new CustomViewAuthorizationExecutionRequest(
            mappingSet,
            DocumentId: 345L,
            Checks:
            [
                new SingleRecordCustomViewAuthorizationCheckSpec(
                    new ConfiguredAuthorizationStrategy("SchoolWithATag", 0),
                    0,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    new DbTableName(new DbSchemaName("auth"), "SchoolWithATag"),
                    new DbColumnName("DocumentId"),
                    [new ColumnPathStep(rootTable, new DbColumnName("DocumentId"), null, null)],
                    new CustomViewAuthorizationCheckTarget.Stored(rootTable, new DbColumnName("DocumentId")),
                    new QualifiedResourceName("Ed-Fi", "School"),
                    ["SchoolWithATagElement"],
                    "You may need a SchoolWithATag hint."
                ),
            ]
        );
    }

    private sealed class ThrowingCommandExecutor(DbException failure) : IRelationalCommandExecutor
    {
        public SqlDialect Dialect => SqlDialect.Pgsql;

        public Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        ) => Task.FromException<TResult>(failure);
    }

    private sealed class StubProviderFailureExtractor(string? providerErrorCode, string providerMessage)
        : IRelationshipAuthorizationProviderFailureExtractor
    {
        public RelationshipAuthorizationProviderFailure Extract(DbException exception) =>
            new(providerErrorCode, providerMessage);
    }

    private sealed class FakeDbException(string message, string sqlState) : DbException(message)
    {
        public override string SqlState => sqlState;
    }
}
