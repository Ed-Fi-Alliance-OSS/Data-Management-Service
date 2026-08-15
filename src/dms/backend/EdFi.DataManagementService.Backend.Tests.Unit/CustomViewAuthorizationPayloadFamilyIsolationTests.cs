// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// A single command can carry namespace, custom-view, and relationship AUTH1 statements at once. Each
/// family's provider-failure mapper must therefore yield on a payload it does not own, so the mapper that
/// owns the discriminator produces the response. Without that, whichever mapper is consulted first would
/// convert another family's 403 into its own security-configuration 500.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_AnAuth1PayloadFromAnotherFamily
{
    private static readonly DbTableName RootTable = new(new DbSchemaName("edfi"), "GradebookEntry");
    private static readonly DbColumnName NamespaceColumn = new("Namespace");

    private static readonly IReadOnlyList<NamespaceAuthorizationCheckSpec> NamespaceChecks =
    [
        new(0, NamespaceAuthorizationCheckValueSource.Stored, RootTable, NamespaceColumn),
    ];

    private static readonly IReadOnlyList<NamespaceAuthorizationCheckValueSource> NamespaceValueSources =
    [
        NamespaceAuthorizationCheckValueSource.Stored,
    ];

    [TestCase("cv1|0|n")]
    [TestCase("cv1|0|u")]
    [TestCase("cv1|1|r")]
    [TestCase("cv1|0|s")]
    public void It_should_not_be_claimed_as_a_namespace_authorization_failure(string payloadText)
    {
        var mapped = NamespaceAuthorizationProviderFailureMapper.TryMapNamespaceAuthorizationFailure(
            SqlDialect.Pgsql,
            new FakeDbException(payloadText, "AUTH1"),
            new StubProviderFailureExtractor("AUTH1", payloadText),
            NamespaceValueSources,
            ["uri://ed-fi.org"],
            out var failure
        );

        mapped.Should().BeFalse();
        failure.Should().BeNull();
    }

    [Test]
    public void It_should_not_be_claimed_as_a_namespace_stale_stored_target()
    {
        // The custom-view stale code is also 's'. Reading it as a namespace stale target would turn a
        // custom-view retry signal into a namespace one, silently retrying against the wrong plan.
        NamespaceAuthorizationProviderFailureMapper
            .IsStaleStoredTargetFailure(
                SqlDialect.Pgsql,
                new FakeDbException("cv1|0|s", "AUTH1"),
                new StubProviderFailureExtractor("AUTH1", "cv1|0|s"),
                NamespaceValueSources
            )
            .Should()
            .BeFalse();
    }

    [TestCase("cv1|0|n")]
    [TestCase("cv1|0|u")]
    [TestCase("cv1|0|s")]
    public void It_should_not_be_reported_as_namespace_invalid_authorization_metadata(string payloadText)
    {
        // The regression this pins: the diagnostics switch used to route every unrecognized dispatch result
        // through a catch-all that claimed it as namespace invalid-authorization metadata, which would have
        // turned a custom-view 403 into a namespace 500 as soon as one command carried both.
        var built =
            NamespaceAuthorizationProviderFailureMapper.TryBuildInvalidAuthorizationFailureDiagnostics(
                SqlDialect.Pgsql,
                new FakeDbException(payloadText, "AUTH1"),
                new StubProviderFailureExtractor("AUTH1", payloadText),
                NamespaceValueSources,
                NamespaceChecks,
                out var diagnostics
            );

        built.Should().BeFalse();
        diagnostics.Should().BeNull();
    }

    [TestCase("cv1|0|n")]
    [TestCase("cv1|0|u")]
    [TestCase("ns1|0|m")]
    public void It_should_not_be_claimed_as_a_relationship_authorization_failure_or_diagnostic(
        string payloadText
    )
    {
        var mapped = RelationshipAuthorizationProviderFailureMapper.TryMapRelationshipAuthorizationFailure(
            SqlDialect.Pgsql,
            new FakeDbException(payloadText, "AUTH1"),
            new StubProviderFailureExtractor("AUTH1", payloadText),
            expectedEmittedAuth1Index: 0,
            [],
            [],
            out var relationshipFailure,
            out var invalidFailureDiagnostic
        );

        mapped.Should().BeFalse();
        relationshipFailure.Should().BeNull();
        // No diagnostic either: a diagnostic makes callers report their own invalid-payload 500, which is
        // exactly the misclassification being prevented.
        invalidFailureDiagnostic.Should().BeNull();
    }

    [TestCase("garbage-with-no-discriminator")]
    [TestCase("1|0|1|not-a-subject-failure")]
    public void It_should_still_report_a_relationship_diagnostic_for_its_own_undecodable_payload(
        string payloadText
    )
    {
        // Only *known* foreign discriminators yield. A corrupt payload that is not attributable to another
        // family must stay a loud relationship security-configuration error rather than falling through to
        // generic database-failure handling.
        var mapped = RelationshipAuthorizationProviderFailureMapper.TryMapRelationshipAuthorizationFailure(
            SqlDialect.Pgsql,
            new FakeDbException(payloadText, "AUTH1"),
            new StubProviderFailureExtractor("AUTH1", payloadText),
            expectedEmittedAuth1Index: 0,
            [],
            [],
            out var relationshipFailure,
            out var invalidFailureDiagnostic
        );

        mapped.Should().BeFalse();
        relationshipFailure.Should().BeNull();
        invalidFailureDiagnostic.Should().NotBeNull();
    }

    [Test]
    public void It_should_still_let_the_namespace_mapper_claim_its_own_payload()
    {
        // The yield must be narrow: namespace mapping of ns1 payloads is unchanged.
        var mapped = NamespaceAuthorizationProviderFailureMapper.TryMapNamespaceAuthorizationFailure(
            SqlDialect.Pgsql,
            new FakeDbException("ns1|0|m", "AUTH1"),
            new StubProviderFailureExtractor("AUTH1", "ns1|0|m"),
            NamespaceValueSources,
            ["uri://ed-fi.org"],
            out var failure
        );

        mapped.Should().BeTrue();
        failure.Should().NotBeNull();
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
