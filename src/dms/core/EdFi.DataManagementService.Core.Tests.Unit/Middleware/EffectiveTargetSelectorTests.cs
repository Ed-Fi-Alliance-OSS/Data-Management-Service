// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Middleware;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// The complete routing matrix, exercised against the pure selector so every combination is covered
/// without standing up a pipeline.
/// </summary>
[TestFixture]
[Parallelizable]
public class EffectiveTargetSelectorTests
{
    private const string PrimaryConnectionString = "Server=primary;Database=edfi";
    private const string ReplicaConnectionString = "Server=replica;Database=edfi";
    private const string SnapshotConnectionString = "Server=snapshot;Database=edfi";

    private static readonly DerivativeRoutingPolicy _readPolicy = new(
        DatabaseAccessIntent.ReadOnly,
        SnapshotEligibility.Allowed,
        ReplicaEligibility.Allowed
    );

    private static readonly DerivativeRoutingPolicy _mutationPolicy = new(
        DatabaseAccessIntent.ReadWrite,
        SnapshotEligibility.RejectedAsMutation,
        ReplicaEligibility.NotApplicable
    );

    private static readonly DerivativeRoutingPolicy _tokenInfoPolicy = new(
        DatabaseAccessIntent.ReadOnly,
        SnapshotEligibility.NotApplicable,
        ReplicaEligibility.NotApplicable
    );

    private static DataStore DataStoreWith(
        params KeyValuePair<DataStoreDerivativeType, string>[] derivatives
    ) =>
        new(
            Id: 7,
            DataStoreType: "Test",
            Name: "Test Instance",
            ConnectionString: PrimaryConnectionString,
            RouteContext: [],
            DerivativeConnectionStrings: derivatives
        );

    private static KeyValuePair<DataStoreDerivativeType, string> Snapshot(
        string connectionString = SnapshotConnectionString
    ) => new(DataStoreDerivativeType.Snapshot, connectionString);

    private static KeyValuePair<DataStoreDerivativeType, string> Replica(
        string connectionString = ReplicaConnectionString
    ) => new(DataStoreDerivativeType.ReadReplica, connectionString);

    private static EffectiveDataStoreTarget SelectedTargetOf(EffectiveTargetSelectionResult result)
    {
        result.Should().BeOfType<EffectiveTargetSelectionResult.Selected>();
        return ((EffectiveTargetSelectionResult.Selected)result).Target;
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Snapshot_Was_Requested : EffectiveTargetSelectorTests
    {
        [Test]
        public void It_selects_the_configured_snapshot_on_a_snapshot_eligible_read()
        {
            var target = SelectedTargetOf(
                EffectiveTargetSelector.Select(_readPolicy, DataStoreWith(Snapshot()), true)
            );

            target.Kind.Should().Be(EffectiveTargetKind.Snapshot);
            target.ConnectionString.Should().Be(SnapshotConnectionString);
        }

        /// <summary>
        /// An explicit request for a snapshot outranks a configured replica. Serving the replica would
        /// answer a point-in-time question with current data.
        /// </summary>
        [Test]
        public void It_overrides_a_configured_read_replica()
        {
            var target = SelectedTargetOf(
                EffectiveTargetSelector.Select(_readPolicy, DataStoreWith(Snapshot(), Replica()), true)
            );

            target.Kind.Should().Be(EffectiveTargetKind.Snapshot);
        }

        [Test]
        public void It_rejects_when_no_snapshot_is_configured()
        {
            var result = EffectiveTargetSelector.Select(_readPolicy, DataStoreWith(), true);

            result.Should().BeOfType<EffectiveTargetSelectionResult.MissingSnapshot>();
        }

        /// <summary>
        /// The rejection is the whole point: a snapshot that is not configured must not quietly become
        /// a read of current data from the primary or a replica.
        /// </summary>
        [Test]
        public void It_does_not_fall_back_to_a_configured_replica_when_no_snapshot_is_configured()
        {
            var result = EffectiveTargetSelector.Select(_readPolicy, DataStoreWith(Replica()), true);

            result.Should().BeOfType<EffectiveTargetSelectionResult.MissingSnapshot>();
        }

        [Test]
        public void It_rejects_a_mutation_whether_or_not_a_snapshot_is_configured()
        {
            EffectiveTargetSelector
                .Select(_mutationPolicy, DataStoreWith(Snapshot()), true)
                .Should()
                .BeOfType<EffectiveTargetSelectionResult.RejectedAsMutation>();

            EffectiveTargetSelector
                .Select(_mutationPolicy, DataStoreWith(), true)
                .Should()
                .BeOfType<EffectiveTargetSelectionResult.RejectedAsMutation>();
        }

        /// <summary>
        /// Token introspection reads the database but the header means nothing there, so it is ignored
        /// rather than rejected or honored.
        /// </summary>
        [Test]
        public void It_is_ignored_where_the_header_does_not_apply()
        {
            var target = SelectedTargetOf(
                EffectiveTargetSelector.Select(_tokenInfoPolicy, DataStoreWith(Snapshot(), Replica()), true)
            );

            target.Kind.Should().Be(EffectiveTargetKind.Primary);
            target.ConnectionString.Should().Be(PrimaryConnectionString);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Snapshot_Was_Requested : EffectiveTargetSelectorTests
    {
        [Test]
        public void It_selects_a_configured_replica_on_a_replica_eligible_read()
        {
            var target = SelectedTargetOf(
                EffectiveTargetSelector.Select(_readPolicy, DataStoreWith(Replica()), false)
            );

            target.Kind.Should().Be(EffectiveTargetKind.ReadReplica);
            target.ConnectionString.Should().Be(ReplicaConnectionString);
        }

        [Test]
        public void It_selects_the_primary_when_no_replica_is_configured()
        {
            var target = SelectedTargetOf(
                EffectiveTargetSelector.Select(_readPolicy, DataStoreWith(Snapshot()), false)
            );

            target.Kind.Should().Be(EffectiveTargetKind.Primary);
            target.ConnectionString.Should().Be(PrimaryConnectionString);
        }

        [Test]
        public void It_selects_the_primary_on_a_read_that_is_not_replica_eligible()
        {
            var target = SelectedTargetOf(
                EffectiveTargetSelector.Select(_tokenInfoPolicy, DataStoreWith(Replica()), false)
            );

            target.Kind.Should().Be(EffectiveTargetKind.Primary);
        }

        [Test]
        public void It_selects_the_primary_for_a_mutation_even_with_a_replica_configured()
        {
            var target = SelectedTargetOf(
                EffectiveTargetSelector.Select(_mutationPolicy, DataStoreWith(Replica(), Snapshot()), false)
            );

            target.Kind.Should().Be(EffectiveTargetKind.Primary);
        }

        /// <summary>
        /// A replica is never served to a write, whatever the pipeline declares about replica
        /// eligibility, because the access intent alone settles it.
        /// </summary>
        [Test]
        public void It_selects_the_primary_for_a_write_that_declares_replica_eligibility()
        {
            DerivativeRoutingPolicy inconsistentPolicy = new(
                DatabaseAccessIntent.ReadWrite,
                SnapshotEligibility.RejectedAsMutation,
                ReplicaEligibility.Allowed
            );

            var target = SelectedTargetOf(
                EffectiveTargetSelector.Select(inconsistentPolicy, DataStoreWith(Replica()), false)
            );

            target.Kind.Should().Be(EffectiveTargetKind.Primary);
        }
    }

    /// <summary>
    /// Selection is a presence test on already-decrypted configuration, not a connection-string parse.
    /// A configured value that no provider could open is still the value the request was routed to;
    /// it fails when a connection is acquired, which is where a provider error belongs.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Provider_Invalid_Derivative : EffectiveTargetSelectorTests
    {
        private const string NotAConnectionString = "this is not a connection string at all";

        [Test]
        public void It_selects_the_snapshot_verbatim_rather_than_reporting_it_missing()
        {
            var result = EffectiveTargetSelector.Select(
                _readPolicy,
                DataStoreWith(Snapshot(NotAConnectionString)),
                true
            );

            var target = SelectedTargetOf(result);
            target.Kind.Should().Be(EffectiveTargetKind.Snapshot);
            target.ConnectionString.Should().Be(NotAConnectionString);
        }

        [Test]
        public void It_selects_the_replica_verbatim_rather_than_serving_the_primary()
        {
            var target = SelectedTargetOf(
                EffectiveTargetSelector.Select(
                    _readPolicy,
                    DataStoreWith(Replica(NotAConnectionString)),
                    false
                )
            );

            target.Kind.Should().Be(EffectiveTargetKind.ReadReplica);
            target.ConnectionString.Should().Be(NotAConnectionString);
        }
    }

    /// <summary>
    /// Whatever CMS stored reaches the acquisition boundary unchanged, so a value whose exact text
    /// matters - a trailing semicolon, unusual spacing, an option DMS does not know about - is not
    /// silently rewritten on the way.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Configured_String_With_Exact_Text : EffectiveTargetSelectorTests
    {
        private const string ExactText = "  Server=replica ;  Database=edfi;Application Name=Ed-Fi ODS;;  ";

        [Test]
        public void It_is_carried_through_byte_for_byte()
        {
            var target = SelectedTargetOf(
                EffectiveTargetSelector.Select(_readPolicy, DataStoreWith(Replica(ExactText)), false)
            );

            target.ConnectionString.Should().Be(ExactText);
        }
    }
}
