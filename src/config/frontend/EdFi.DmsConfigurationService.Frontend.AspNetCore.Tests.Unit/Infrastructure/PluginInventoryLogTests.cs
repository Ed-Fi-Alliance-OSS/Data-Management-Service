// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Api.Plugins.Hosting;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// What the inventory writes when no plugin was loaded. The per-plugin event itself needs a loaded
/// plugin and is exercised by the fixture-plugin boot tests.
/// </summary>
[TestFixture]
public class Given_an_inventory_with_no_loaded_plugin_and_one_loader_warning
{
    private IReadOnlyList<CapturedLog> _entries = null!;

    [SetUp]
    public void Setup()
    {
        CapturingLoggerProvider provider = new();
        PluginAuditInput auditInput = new(
            CmsPluginContracts.Registry,
            [],
            [],
            [
                new PluginLoadWarning(
                    PluginLoadWarningKind.UnallowlistedDirectories,
                    ["Stray\nPlugin"],
                    Detail: null
                ),
            ]
        );

        PluginInventoryLog.Emit(provider.CreateLogger("Program"), auditInput);
        _entries = provider.Entries;
    }

    [Test]
    public void It_emits_no_inventory_event()
    {
        _entries.Should().NotContain(entry => entry.EventId.Name == "PluginInventory");
    }

    [Test]
    public void It_replays_the_loader_warning_as_one_warning_event()
    {
        _entries
            .Should()
            .ContainSingle()
            .Which.Should()
            .Match<CapturedLog>(entry =>
                entry.Level == LogLevel.Warning && entry.EventId.Name == "PluginLoadWarning"
            );
    }

    [Test]
    public void It_strips_control_characters_from_the_directory_names_it_replays()
    {
        _entries
            .Should()
            .ContainSingle()
            .Which.State["@IgnoredDirectories"]
            .Should()
            .BeEquivalentTo(new[] { "StrayPlugin" });
    }
}
