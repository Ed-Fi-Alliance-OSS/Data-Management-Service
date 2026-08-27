// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

public class DataStoreTests
{
    private static DataStore CreateDataStore(
        IEnumerable<KeyValuePair<DataStoreDerivativeType, string>>? derivatives = null
    ) =>
        new(
            Id: 1,
            DataStoreType: "Production",
            Name: "Main Instance",
            ConnectionString: "host=primary;database=edfi;",
            RouteContext: [],
            DerivativeConnectionStrings: derivatives
        );

    [TestFixture]
    [Parallelizable]
    public class Given_A_DataStore_With_No_Derivative_Input
    {
        private DataStore _dataStore = null!;

        [SetUp]
        public void Setup()
        {
            _dataStore = CreateDataStore();
        }

        [Test]
        public void It_should_expose_an_empty_derivative_map()
        {
            _dataStore.Derivatives.Should().BeEmpty();
        }

        [Test]
        public void It_should_report_no_snapshot_configured()
        {
            _dataStore
                .TryGetDerivative(DataStoreDerivativeType.Snapshot, out string? connectionString)
                .Should()
                .BeFalse();

            connectionString.Should().BeNull();
        }

        [Test]
        public void It_should_report_no_read_replica_configured()
        {
            _dataStore
                .TryGetDerivative(DataStoreDerivativeType.ReadReplica, out string? connectionString)
                .Should()
                .BeFalse();

            connectionString.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_DataStore_With_Configured_Derivatives
    {
        private DataStore _dataStore = null!;

        [SetUp]
        public void Setup()
        {
            _dataStore = CreateDataStore(
                new Dictionary<DataStoreDerivativeType, string>
                {
                    [DataStoreDerivativeType.Snapshot] = "host=snapshot;database=edfi;",
                    [DataStoreDerivativeType.ReadReplica] = "host=replica;database=edfi;",
                }
            );
        }

        [Test]
        public void It_should_expose_the_snapshot_connection_string()
        {
            _dataStore
                .TryGetDerivative(DataStoreDerivativeType.Snapshot, out string? connectionString)
                .Should()
                .BeTrue();

            connectionString.Should().Be("host=snapshot;database=edfi;");
        }

        [Test]
        public void It_should_expose_the_read_replica_connection_string()
        {
            _dataStore
                .TryGetDerivative(DataStoreDerivativeType.ReadReplica, out string? connectionString)
                .Should()
                .BeTrue();

            connectionString.Should().Be("host=replica;database=edfi;");
        }
    }

    /// <summary>
    /// The derivative map is a snapshot taken at construction, not an alias of whatever the caller
    /// supplied. A request that has already selected a target must keep the configuration it selected
    /// from, so no later change to the caller's collection may reach any observable member.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Derivative_Source_Is_Mutated_After_Construction
    {
        private Dictionary<DataStoreDerivativeType, string> _source = null!;
        private DataStore _dataStore = null!;
        private DataStore _equivalent = null!;
        private int _hashCodeBeforeMutation;

        [SetUp]
        public void Setup()
        {
            _source = new Dictionary<DataStoreDerivativeType, string>
            {
                [DataStoreDerivativeType.Snapshot] = "host=snapshot;database=edfi;",
            };

            _dataStore = CreateDataStore(_source);
            _equivalent = _dataStore with { };
            _hashCodeBeforeMutation = _dataStore.GetHashCode();

            _source[DataStoreDerivativeType.ReadReplica] = "host=added-later;database=edfi;";
            _source[DataStoreDerivativeType.Snapshot] = "host=replaced-later;database=edfi;";
            _source.Remove(DataStoreDerivativeType.Snapshot);
        }

        [Test]
        public void It_should_not_observe_an_added_derivative()
        {
            _dataStore.Derivatives.Should().NotContainKey(DataStoreDerivativeType.ReadReplica);
        }

        [Test]
        public void It_should_not_observe_a_replaced_or_removed_derivative()
        {
            _dataStore
                .TryGetDerivative(DataStoreDerivativeType.Snapshot, out string? connectionString)
                .Should()
                .BeTrue();

            connectionString.Should().Be("host=snapshot;database=edfi;");
        }

        [Test]
        public void It_should_keep_its_derivative_count()
        {
            _dataStore.Derivatives.Should().HaveCount(1);
        }

        [Test]
        public void It_should_keep_its_hash_code()
        {
            _dataStore.GetHashCode().Should().Be(_hashCodeBeforeMutation);
        }

        [Test]
        public void It_should_keep_comparing_equal_to_the_copy_taken_before_mutation()
        {
            _dataStore.Should().Be(_equivalent);
        }
    }

    /// <summary>
    /// Because the exposed member is an immutable dictionary, a `with` expression cannot introduce
    /// caller-shared or mutable derivative state, and a `with` that changes something else carries the
    /// existing snapshot forward untouched.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_DataStore_Copied_With_A_With_Expression
    {
        private DataStore _original = null!;
        private DataStore _renamed = null!;
        private DataStore _replacedDerivatives = null!;

        [SetUp]
        public void Setup()
        {
            _original = CreateDataStore(
                new Dictionary<DataStoreDerivativeType, string>
                {
                    [DataStoreDerivativeType.Snapshot] = "host=snapshot;database=edfi;",
                }
            );

            _renamed = _original with { Name = "Renamed Instance" };
            _replacedDerivatives = _original with
            {
                Derivatives = ImmutableDictionary<DataStoreDerivativeType, string>.Empty,
            };
        }

        [Test]
        public void It_should_carry_the_derivative_snapshot_forward_when_another_member_changes()
        {
            _renamed
                .TryGetDerivative(DataStoreDerivativeType.Snapshot, out string? connectionString)
                .Should()
                .BeTrue();

            connectionString.Should().Be("host=snapshot;database=edfi;");
        }

        [Test]
        public void It_should_leave_the_original_unchanged_when_derivatives_are_replaced()
        {
            _original.Derivatives.Should().ContainKey(DataStoreDerivativeType.Snapshot);
        }

        [Test]
        public void It_should_apply_the_replacement_to_the_copy_only()
        {
            _replacedDerivatives.Derivatives.Should().BeEmpty();
        }
    }

    /// <summary>
    /// The route-context member keeps its existing shape and behavior, which this pins so the model
    /// reshape is not mistaken for a change to it.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_DataStore_Built_With_Named_Arguments
    {
        private DataStore _dataStore = null!;

        [SetUp]
        public void Setup()
        {
            _dataStore = new DataStore(
                Id: 7,
                DataStoreType: "Production",
                Name: "Named",
                ConnectionString: "host=primary;database=edfi;",
                RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>
                {
                    [new RouteQualifierName("district")] = new RouteQualifierValue("255901"),
                },
                RelationalProviderToken: null,
                RelationalProviderMetadataStatus: RelationalProviderMetadataStatus.Missing
            );
        }

        [Test]
        public void It_should_populate_the_scalar_members()
        {
            _dataStore.Id.Should().Be(7);
            _dataStore.Name.Should().Be("Named");
            _dataStore.DataStoreType.Should().Be("Production");
            _dataStore.ConnectionString.Should().Be("host=primary;database=edfi;");
        }

        [Test]
        public void It_should_populate_the_route_context()
        {
            _dataStore.RouteContext.Should().ContainKey(new RouteQualifierName("district"));
        }

        [Test]
        public void It_should_default_to_no_derivatives()
        {
            _dataStore.Derivatives.Should().BeEmpty();
        }
    }
}
