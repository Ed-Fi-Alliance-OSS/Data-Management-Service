// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    private static DmsDataStoreProvisionerReadiness EndpointReadiness(string provider)
    {
        DmsDataStoreProvisionerReadiness readiness = new();
        readiness.Record(new DmsProvisionerReadiness.Ready(provider, DateTimeOffset.UtcNow));
        return readiness;
    }

    [Test, Category("EndpointCanonicalAcceptance")]
    public async Task It_refuses_network_alias_descriptors_in_either_acceptance_order(
        [Values("postgresql", "mssql")] string provider,
        [Values("host-case", "expanded-ipv6", "bracketed-ipv6", "short-ipv4")] string aliasKind,
        [Values(false, true)] bool aliasFirst
    )
    {
        var endpoint = CreateCommand(provider: provider).Endpoint;
        (string canonical, string alias) = aliasKind switch
        {
            "host-case" => ("db.example", "DB.EXAMPLE"),
            "expanded-ipv6" => ("2001:db8::1", "2001:0db8:0:0:0:0:0:1"),
            "bracketed-ipv6" => ("2001:db8::1", "[2001:db8::1]"),
            _ => ("127.0.0.1", "127.1"),
        };
        await AssertEndpointAliasRefused(
            endpoint with
            {
                Host = canonical,
            },
            endpoint with
            {
                Host = alias,
            },
            aliasFirst
        );
    }

    [TestCase(false), TestCase(true)]
    [Category("EndpointCanonicalAcceptance")]
    public async Task It_refuses_named_instance_case_aliases_in_either_acceptance_order(bool aliasFirst)
    {
        ManagedDataStoreEndpoint endpoint = new("mssql", "tcp", "db.example", null, "school", "master");
        await AssertEndpointAliasRefused(endpoint, endpoint with { Instance = "SCHOOL" }, aliasFirst);
    }

    private async Task AssertEndpointAliasRefused(
        ManagedDataStoreEndpoint canonical,
        ManagedDataStoreEndpoint alias,
        bool aliasFirst
    )
    {
        long other = await Tenant("alias-caller");
        using var firstServices = AcceptanceServices();
        using var otherServices = AcceptanceServices(other);
        var firstRepository = ManagedRepository(
            firstServices.GetRequiredService<IJobEnqueuer>(),
            readiness: EndpointReadiness(canonical.Provider)
        );
        var otherRepository = ManagedRepository(
            otherServices.GetRequiredService<IJobEnqueuer>(),
            other,
            EndpointReadiness(canonical.Provider)
        );
        var canonicalCommand = CreateCommand(provider: canonical.Provider) with { Endpoint = canonical };
        var aliasCommand = CreateCommand(provider: alias.Provider) with { Endpoint = alias };
        if (aliasFirst)
        {
            (await otherRepository.AcceptCreateAsync(aliasCommand, default))
                .Should()
                .BeOfType<DataStoreManageCreateResult.InvalidRequest>();
            await AssertAcceptanceCounts(0, 0);
        }
        (await firstRepository.AcceptCreateAsync(canonicalCommand, default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>();
        if (!aliasFirst)
        {
            (await otherRepository.AcceptCreateAsync(aliasCommand, default))
                .Should()
                .BeOfType<DataStoreManageCreateResult.InvalidRequest>();
        }
        (
            await otherRepository.AcceptCreateAsync(
                canonicalCommand with
                {
                    ExpectedSourceIdentity = Guid.NewGuid(),
                },
                default
            )
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.TargetReserved>();
        await AssertAcceptanceCounts(1, 1);
    }

    [Test, Category("EndpointCanonicalAcceptance")]
    public async Task It_refuses_malformed_direct_network_hosts_without_rows_or_jobs(
        [Values("postgresql", "mssql")] string provider,
        [Values(
            "db.example,other",
            "db.example/path",
            "db.example:5432",
            "db.example,1433",
            "db.example\\school",
            "db_example",
            "déb.example"
        )]
            string host
    )
    {
        using var services = AcceptanceServices();
        var command = CreateCommand(provider: provider);
        command = command with { Endpoint = command.Endpoint with { Host = host } };
        (
            await ManagedRepository(
                    services.GetRequiredService<IJobEnqueuer>(),
                    readiness: EndpointReadiness(provider)
                )
                .AcceptCreateAsync(command, default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.InvalidRequest>();
        await AssertAcceptanceCounts(0, 0);
    }

    [TestCase("school,1433"), TestCase("school/path"), TestCase("school\\nested")]
    [Category("EndpointCanonicalAcceptance")]
    public async Task It_refuses_malformed_direct_instances_without_rows_or_jobs(string instance)
    {
        using var services = AcceptanceServices();
        var command = CreateCommand(provider: "mssql") with
        {
            Endpoint = new("mssql", "tcp", "db.example", null, instance, "master"),
        };
        (
            await ManagedRepository(
                    services.GetRequiredService<IJobEnqueuer>(),
                    readiness: EndpointReadiness("mssql")
                )
                .AcceptCreateAsync(command, default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.InvalidRequest>();
        await AssertAcceptanceCounts(0, 0);
    }

    [TestCase("/Var/Run/postgresql", "/var/run/postgresql")]
    [TestCase("@Postgresql[Socket]", "@postgresql[Socket]")]
    [Category("EndpointCanonicalAcceptance")]
    public async Task It_preserves_distinct_case_sensitive_socket_reservations(string first, string second)
    {
        long other = await Tenant("other-socket");
        using var firstServices = AcceptanceServices();
        using var otherServices = AcceptanceServices(other);
        var command = CreateCommand() with
        {
            Endpoint = new("postgresql", "unix", first, 5432, null, "postgres"),
        };
        (
            await ManagedRepository(firstServices.GetRequiredService<IJobEnqueuer>())
                .AcceptCreateAsync(command, default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>();
        command = command with
        {
            ExpectedSourceIdentity = Guid.NewGuid(),
            Endpoint = command.Endpoint with { Host = second },
        };
        (
            await ManagedRepository(otherServices.GetRequiredService<IJobEnqueuer>(), other)
                .AcceptCreateAsync(command, default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.Success>();
        await AssertAcceptanceCounts(2, 2);
    }
}
