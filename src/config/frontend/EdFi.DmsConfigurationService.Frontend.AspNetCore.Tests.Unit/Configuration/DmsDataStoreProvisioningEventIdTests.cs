// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

/// <summary>
/// Every log event of DMS data store provisioning (ids <c>1438_00</c> to <c>1438_99</c>) has its own id across
/// the four assemblies that define them, so operators can filter on the id alone (docs/OPERATIONS.md lists
/// them). The events are the <see cref="EventId"/> static fields, public or not, of every type in those
/// assemblies.
/// </summary>
[TestFixture]
public class Given_the_log_events_of_dms_data_store_provisioning
{
    private const int FirstId = 1438_00;

    private const int LastId = 1438_99;

    private static readonly Assembly[] _assemblies =
    [
        typeof(DmsDataStoreProvisioningStartup).Assembly,
        typeof(DmsTargetLockHolder).Assembly,
        typeof(PostgresqlDmsTargetEngine).Assembly,
        typeof(MssqlDmsTargetEngine).Assembly,
    ];

    private sealed record DeclaredEvent(Assembly Assembly, string Field, EventId Event);

    private DeclaredEvent[] _events = [];

    [SetUp]
    public void Setup()
    {
        _events =
        [
            .. _assemblies
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => !type.ContainsGenericParameters)
                .SelectMany(type =>
                    type.GetFields(
                            BindingFlags.Static
                                | BindingFlags.Public
                                | BindingFlags.NonPublic
                                | BindingFlags.DeclaredOnly
                        )
                        .Where(field => field.FieldType == typeof(EventId))
                        .Select(field => new DeclaredEvent(
                            type.Assembly,
                            $"{type.FullName}.{field.Name}",
                            (EventId)field.GetValue(null)!
                        ))
                )
                .Where(declared => declared.Event.Id is >= FirstId and <= LastId),
        ];
    }

    [Test]
    public void It_finds_provisioning_events_in_each_of_the_four_assemblies() =>
        _events.Select(declared => declared.Assembly).Distinct().Should().BeEquivalentTo(_assemblies);

    [Test]
    public void It_gives_every_event_its_own_id() =>
        _events
            .GroupBy(declared => declared.Event.Id)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(declared => declared.Field))}")
            .Should()
            .BeEmpty();
}
