// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// The public partitions contract over HTTP on PostgreSQL. This binding carries the whole scenario:
/// the validation rows, which are answered before any provider is involved, and the sizing rows, which
/// are not. The SQL Server twin carries only the sizing rows.
/// </summary>
/// <remarks>
/// Leases the descriptor runtime fixture, whose ApiSchema declares the regular resource these
/// partitions are calculated over.
/// </remarks>
public sealed class Given_Postgresql_PartitionPublicContract : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    /// <summary>
    /// The mandatory minimum partition size is a multiple of this, so at the deployed value the seeded
    /// collection would be a single partition and the sizing assertions would pass without the
    /// requested count ever mattering.
    /// </summary>
    protected override int? MaximumPageSizeOverride => PartitionPublicContractScenario.HostMaximumPageSize;

    [Test]
    public Task It_answers_a_partition_parameter_fault_with_the_parameter_validation_shell() =>
        PartitionPublicContractScenario.It_answers_a_partition_parameter_fault_with_the_parameter_validation_shell(
            Harness
        );

    [Test]
    public Task It_treats_a_blank_partition_count_as_malformed() =>
        PartitionPublicContractScenario.It_treats_a_blank_partition_count_as_malformed(Harness);

    [Test]
    public Task It_refuses_a_partition_count_above_the_supported_maximum() =>
        PartitionPublicContractScenario.It_refuses_a_partition_count_above_the_supported_maximum(Harness);

    [Test]
    public Task It_reports_every_reserved_parameter_in_canonical_order() =>
        PartitionPublicContractScenario.It_reports_every_reserved_parameter_in_canonical_order(Harness);

    [Test]
    public Task It_answers_a_malformed_count_and_an_unknown_field_with_the_unknown_field_alone() =>
        PartitionPublicContractScenario.It_answers_a_malformed_count_and_an_unknown_field_with_the_unknown_field_alone(
            Harness
        );

    [Test]
    public Task It_answers_a_malformed_count_and_a_malformed_window_with_the_window_alone() =>
        PartitionPublicContractScenario.It_answers_a_malformed_count_and_a_malformed_window_with_the_window_alone(
            Harness
        );

    [Test]
    public Task It_answers_an_unknown_field_and_a_reserved_parameter_with_the_unknown_field_alone() =>
        PartitionPublicContractScenario.It_answers_an_unknown_field_and_a_reserved_parameter_with_the_unknown_field_alone(
            Harness
        );

    [Test]
    public Task It_answers_a_malformed_window_and_an_unknown_field_in_the_parameter_validation_shell() =>
        PartitionPublicContractScenario.It_answers_a_malformed_window_and_an_unknown_field_in_the_parameter_validation_shell(
            Harness
        );

    [Test]
    public Task It_resolves_a_partition_count_supplied_in_two_letter_cases_to_the_last_value() =>
        PartitionPublicContractScenario.It_resolves_a_partition_count_supplied_in_two_letter_cases_to_the_last_value(
            Harness
        );

    [Test]
    public Task It_covers_the_collection_with_one_partition_when_one_is_requested() =>
        PartitionPublicContractScenario.It_covers_the_collection_with_one_partition_when_one_is_requested(
            Harness
        );

    [Test]
    public Task It_returns_the_same_boundaries_once_the_minimum_partition_size_binds() =>
        PartitionPublicContractScenario.It_returns_the_same_boundaries_once_the_minimum_partition_size_binds(
            Harness
        );
}
