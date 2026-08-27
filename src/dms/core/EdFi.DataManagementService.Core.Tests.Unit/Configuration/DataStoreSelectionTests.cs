// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

/// <summary>
/// The request-scoped selection is two write-once phases with no fallback between them, which is what
/// keeps a request that asked for a derivative from being served the primary because a pipeline forgot
/// to select a target.
/// </summary>
[TestFixture]
[Parallelizable]
public class DataStoreSelectionTests
{
    private const string PrimaryConnectionString = "Server=primary;Database=edfi";
    private const string SnapshotConnectionString = "Server=snapshot;Database=edfi";

    private static DataStore TestDataStore(long id = 1) =>
        new(
            Id: id,
            DataStoreType: "Test",
            Name: "Test Instance",
            ConnectionString: PrimaryConnectionString,
            RouteContext: []
        );

    [TestFixture]
    [Parallelizable]
    public class Given_Nothing_Has_Been_Selected : DataStoreSelectionTests
    {
        private DataStoreSelection _selection = null!;

        [SetUp]
        public void Setup()
        {
            _selection = new DataStoreSelection();
        }

        [Test]
        public void It_reports_no_parent()
        {
            _selection.IsSet.Should().BeFalse();
        }

        [Test]
        public void It_reports_no_effective_target()
        {
            _selection.IsEffectiveTargetSet.Should().BeFalse();
        }

        [Test]
        public void It_refuses_to_return_a_parent()
        {
            Action read = () => _selection.GetSelectedDataStore();

            read.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*parent data store has not been selected*");
        }

        [Test]
        public void It_refuses_to_return_an_effective_target()
        {
            Action read = () => _selection.GetEffectiveTarget();

            read.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*effective target has not been selected*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Only_The_Parent_Has_Been_Selected : DataStoreSelectionTests
    {
        private DataStoreSelection _selection = null!;

        [SetUp]
        public void Setup()
        {
            _selection = new DataStoreSelection();
            _selection.SetSelectedDataStore(TestDataStore());
        }

        [Test]
        public void It_returns_the_parent()
        {
            _selection.GetSelectedDataStore().Id.Should().Be(1);
            _selection.IsSet.Should().BeTrue();
        }

        [Test]
        public void It_still_has_no_effective_target()
        {
            _selection.IsEffectiveTargetSet.Should().BeFalse();
        }

        /// <summary>
        /// The point of the whole design: reading the target before it is selected is an error, not a
        /// quiet read of the parent's own database.
        /// </summary>
        [Test]
        public void It_does_not_fall_back_to_the_parent_connection_string()
        {
            Action read = () => _selection.GetEffectiveTarget();

            read.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void It_refuses_a_second_parent_assignment()
        {
            Action reassign = () => _selection.SetSelectedDataStore(TestDataStore(id: 2));

            reassign
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*parent data store has already been selected*");
        }

        [Test]
        public void It_still_returns_the_first_parent_after_a_refused_reassignment()
        {
            try
            {
                _selection.SetSelectedDataStore(TestDataStore(id: 2));
            }
            catch (InvalidOperationException)
            {
                // The refusal itself is asserted separately; what matters here is what survives it.
            }

            _selection.GetSelectedDataStore().Id.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Parent_Without_A_Connection_String : DataStoreSelectionTests
    {
        [Test]
        public void It_is_refused()
        {
            DataStoreSelection selection = new();

            Action assign = () =>
                selection.SetSelectedDataStore(TestDataStore() with { ConnectionString = "  " });

            assign.Should().Throw<ArgumentException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Both_Phases_Have_Been_Selected : DataStoreSelectionTests
    {
        private DataStoreSelection _selection = null!;

        [SetUp]
        public void Setup()
        {
            _selection = new DataStoreSelection();
            _selection.SetSelectedDataStore(TestDataStore());
            _selection.SetEffectiveTarget(
                new EffectiveDataStoreTarget(EffectiveTargetKind.Snapshot, SnapshotConnectionString)
            );
        }

        [Test]
        public void It_returns_the_effective_target()
        {
            _selection.GetEffectiveTarget().Kind.Should().Be(EffectiveTargetKind.Snapshot);
            _selection.GetEffectiveTarget().ConnectionString.Should().Be(SnapshotConnectionString);
            _selection.IsEffectiveTargetSet.Should().BeTrue();
        }

        /// <summary>
        /// The parent stays the parent: authorization, route context, and logging identity are still
        /// the instance the request resolved to, not the database it is being served from.
        /// </summary>
        [Test]
        public void It_still_returns_the_parent_unchanged()
        {
            _selection.GetSelectedDataStore().ConnectionString.Should().Be(PrimaryConnectionString);
        }

        [Test]
        public void It_refuses_a_second_target_assignment()
        {
            Action reassign = () =>
                _selection.SetEffectiveTarget(EffectiveDataStoreTarget.Primary(PrimaryConnectionString));

            reassign
                .Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*effective target has already been selected*");
        }

        [Test]
        public void It_still_returns_the_first_target_after_a_refused_reassignment()
        {
            try
            {
                _selection.SetEffectiveTarget(EffectiveDataStoreTarget.Primary(PrimaryConnectionString));
            }
            catch (InvalidOperationException)
            {
                // Asserted separately; this test is about what a later reader observes.
            }

            _selection.GetEffectiveTarget().ConnectionString.Should().Be(SnapshotConnectionString);
        }
    }

    /// <summary>
    /// Nothing requires the parent to be assigned first, but a target assigned on its own must not make
    /// the parent readable.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Only_The_Target_Has_Been_Selected : DataStoreSelectionTests
    {
        private DataStoreSelection _selection = null!;

        [SetUp]
        public void Setup()
        {
            _selection = new DataStoreSelection();
            _selection.SetEffectiveTarget(EffectiveDataStoreTarget.Primary(PrimaryConnectionString));
        }

        [Test]
        public void It_returns_the_target()
        {
            _selection.GetEffectiveTarget().Kind.Should().Be(EffectiveTargetKind.Primary);
        }

        [Test]
        public void It_refuses_to_return_a_parent()
        {
            Action read = () => _selection.GetSelectedDataStore();

            read.Should().Throw<InvalidOperationException>();
        }
    }
}
