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
    [TestCase("own1|0|m")]
    [TestCase("own1|1|u")]
    [TestCase("own1|0|s")]
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
        // Every family's stale code is 's'. Reading another family's as a namespace stale target would turn
        // its retry signal into a namespace one, silently retrying against the wrong plan.
        foreach (var payloadText in new[] { "cv1|0|s", "own1|0|s" })
        {
            NamespaceAuthorizationProviderFailureMapper
                .IsStaleStoredTargetFailure(
                    SqlDialect.Pgsql,
                    new FakeDbException(payloadText, "AUTH1"),
                    new StubProviderFailureExtractor("AUTH1", payloadText),
                    NamespaceValueSources
                )
                .Should()
                .BeFalse(payloadText);
        }
    }

    [TestCase("cv1|0|n")]
    [TestCase("cv1|0|u")]
    [TestCase("cv1|0|s")]
    [TestCase("own1|0|m")]
    [TestCase("own1|1|u")]
    [TestCase("own1|0|s")]
    public void It_should_not_be_reported_as_namespace_invalid_authorization_metadata(string payloadText)
    {
        // The regression this pins: the diagnostics switch used to route every unrecognized dispatch result
        // through a catch-all that claimed it as namespace invalid-authorization metadata, which would have
        // turned a custom-view 403 into a namespace 500 as soon as one command carried both. The ownership
        // cases pin the same thing for the family added by DMS-1060, whose statements co-batch with the
        // namespace statement on every write and delete path.
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
    [TestCase("own1|0|m")]
    [TestCase("own1|1|u")]
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

    /// <summary>
    /// The custom-view mapper is safe by construction — it only claims a <c>CustomView</c> dispatch result —
    /// but the contract is pinned so a future refactor toward a catch-all cannot reintroduce the
    /// cross-family misclassification the rest of this fixture exists to prevent.
    /// </summary>
    [TestCase("own1|0|m")]
    [TestCase("own1|1|u")]
    [TestCase("own1|0|s")]
    public void It_should_not_be_claimed_as_a_custom_view_authorization_problem(string payloadText)
    {
        CustomViewAuthorizationProviderFailureMapper
            .IsUnmappableCustomViewPayload(
                SqlDialect.Pgsql,
                new FakeDbException(payloadText, "AUTH1"),
                new StubProviderFailureExtractor("AUTH1", payloadText),
                []
            )
            .Should()
            .BeFalse(payloadText);

        // Recognized by the dispatcher, so it is not an unrecognized provider failure the custom-view mapper
        // would attribute to a missing or revoked auth view.
        CustomViewAuthorizationProviderFailureMapper
            .IsUnrecognizedProviderFailure(
                SqlDialect.Pgsql,
                new FakeDbException(payloadText, "AUTH1"),
                new StubProviderFailureExtractor("AUTH1", payloadText)
            )
            .Should()
            .BeFalse(payloadText);
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

    /// <summary>
    /// The full co-batch matrix for the ownership family. A single command carries namespace, custom-view,
    /// relationship and ownership statements, and only one of them can raise the abort — so no other family
    /// may convert an ownership payload into a denial of its own, whatever the payload says.
    /// </summary>
    /// <remarks>
    /// Covers every variant an ownership statement can produce: the two denials (§2.13 and §2.14), the
    /// stale-target retry signal, a configured-strategy index that matches no planned check, and two
    /// malformed payload shapes. None of these mappers is given an ownership plan, so this is also the
    /// "no planned ownership check" case.
    /// <para>
    /// These assertions are about the <em>403</em> paths, which is where a misattribution would be a
    /// security-relevant wrong answer. Which family reports the 500 <em>diagnostic</em> for a malformed
    /// ownership payload is settled separately, when the ownership provider-failure mapper joins the
    /// composite classifier.
    /// </para>
    /// </remarks>
    [TestCase("own1|0|m")]
    [TestCase("own1|0|u")]
    [TestCase("own1|0|s")]
    [TestCase("own1|9|m")]
    [TestCase("own1|0|x")]
    [TestCase("own1|0")]
    public void It_should_never_be_turned_into_another_familys_authorization_denial(string payloadText)
    {
        NamespaceAuthorizationProviderFailureMapper
            .TryMapNamespaceAuthorizationFailure(
                SqlDialect.Pgsql,
                new FakeDbException(payloadText, "AUTH1"),
                new StubProviderFailureExtractor("AUTH1", payloadText),
                NamespaceValueSources,
                ["uri://ed-fi.org"],
                out var namespaceFailure
            )
            .Should()
            .BeFalse(payloadText);
        namespaceFailure.Should().BeNull(payloadText);

        RelationshipAuthorizationProviderFailureMapper
            .TryMapRelationshipAuthorizationFailure(
                SqlDialect.Pgsql,
                new FakeDbException(payloadText, "AUTH1"),
                new StubProviderFailureExtractor("AUTH1", payloadText),
                expectedEmittedAuth1Index: 0,
                [],
                [],
                out var relationshipFailure,
                out _
            )
            .Should()
            .BeFalse(payloadText);
        relationshipFailure.Should().BeNull(payloadText);

        CustomViewAuthorizationProviderFailureMapper
            .IsUnmappableCustomViewPayload(
                SqlDialect.Pgsql,
                new FakeDbException(payloadText, "AUTH1"),
                new StubProviderFailureExtractor("AUTH1", payloadText),
                []
            )
            .Should()
            .BeFalse(payloadText);
    }

    /// <summary>
    /// A well-formed ownership payload must not be read as any other family's stale-target retry signal:
    /// every family encodes stale as <c>s</c>, and retrying against the wrong plan would re-run a check the
    /// abort never came from.
    /// </summary>
    [Test]
    public void It_should_never_be_read_as_another_familys_stale_stored_target()
    {
        const string OwnershipStalePayload = "own1|0|s";

        NamespaceAuthorizationProviderFailureMapper
            .IsStaleStoredTargetFailure(
                SqlDialect.Pgsql,
                new FakeDbException(OwnershipStalePayload, "AUTH1"),
                new StubProviderFailureExtractor("AUTH1", OwnershipStalePayload),
                NamespaceValueSources
            )
            .Should()
            .BeFalse();

        CustomViewAuthorizationProviderFailureMapper
            .IsStaleStoredTargetFailure(
                SqlDialect.Pgsql,
                new FakeDbException(OwnershipStalePayload, "AUTH1"),
                new StubProviderFailureExtractor("AUTH1", OwnershipStalePayload),
                []
            )
            .Should()
            .BeFalse();
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
