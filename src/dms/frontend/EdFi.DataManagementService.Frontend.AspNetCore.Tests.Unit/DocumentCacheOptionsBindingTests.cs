// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class DocumentCacheOptionsBindingTests
{
    [Test]
    public void It_binds_DocumentCacheOptions_from_the_supported_configuration_section()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = "",
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "1",
                    ["DataManagement:DocumentCache:ReadAcceleration:Enabled"] = "true",
                    ["DataManagement:DocumentCache:ReadAcceleration:DirectFillTimeout"] = "00:00:00.125",
                    ["DataManagement:DocumentCache:Projector:PollInterval"] = "00:00:07",
                    ["DataManagement:DocumentCache:Projector:PageSize"] = "25",
                    ["DataManagement:DocumentCache:Projector:MaxConcurrentTargets"] = "4",
                    ["DataManagement:DocumentCache:Projector:FailureBackoff"] = "00:01:15",
                    ["DataManagement:DocumentCache:Projector:BaselineHighWaterMark"] = "2500",
                    ["DataManagement:DocumentCache:Administration:WorkflowTimeout"] = "12:00:00",
                    ["DataManagement:DocumentCache:Status:StatusObservationTimeout"] = "00:00:08",
                    ["DataManagement:DocumentCache:Status:EndpointTimeout"] = "00:00:45",
                    ["DataManagement:DocumentCache:Status:RequiredRole"] = "dms-document-cache-operator",
                }
            )
            .Build();

        DocumentCacheOptions options = new();
        configuration.GetSection(DocumentCacheOptions.SectionName).Bind(options);

        options.Targets.Should().ContainSingle();
        options.Targets[0].TenantKey.Should().BeEmpty();
        options.Targets[0].DataStoreId.Should().Be(1);
        options.ReadAcceleration.Enabled.Should().BeTrue();
        options.ReadAcceleration.DirectFillTimeout.Should().Be(TimeSpan.FromMilliseconds(125));
        options.Projector.PollInterval.Should().Be(TimeSpan.FromSeconds(7));
        options.Projector.PageSize.Should().Be(25);
        options.Projector.MaxConcurrentTargets.Should().Be(4);
        options.Projector.FailureBackoff.Should().Be(TimeSpan.FromSeconds(75));
        options.Projector.BaselineHighWaterMark.Should().Be(2500);
        options.Administration.WorkflowTimeout.Should().Be(TimeSpan.FromHours(12));
        options.Status.StatusObservationTimeout.Should().Be(TimeSpan.FromSeconds(8));
        options.Status.EndpointTimeout.Should().Be(TimeSpan.FromSeconds(45));
        options.Status.RequiredRole.Should().Be("dms-document-cache-operator");
    }

    [Test]
    public void It_binds_the_day_based_administrative_workflow_timeout()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Administration:WorkflowTimeout"] = "1.00:00:00",
                }
            )
            .Build();

        DocumentCacheOptions options = new();
        configuration.GetSection(DocumentCacheOptions.SectionName).Bind(options);

        options.Administration.WorkflowTimeout.Should().Be(TimeSpan.FromHours(24));
    }

    /// <summary>
    /// The deployment-owned CDC settings the compose stacks always emit. Their variables are blank on
    /// every run that did not opt into CDC, so the DMS image is started with the indexed target keys
    /// present and empty. That shape must resolve to no projection target at all and leave the status
    /// endpoint unmapped: a stack running without CDC has to start exactly as it did before the keys
    /// were added, and a target the deployment never configured must never be projected.
    /// </summary>
    [Test]
    public void It_binds_blank_indexed_target_keys_to_no_projection_target()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = "",
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "",
                    ["DataManagement:DocumentCache:Status:RequiredRole"] = "",
                }
            )
            .Build();

        DocumentCacheOptions options = new();
        configuration.GetSection(DocumentCacheOptions.SectionName).Bind(options);

        using AssertionScope assertions = new();
        options.Targets.Should().BeEmpty();
        options.Status.TryGetRequiredRoleForEndpointMapping(out _).Should().BeFalse();

        // The blank pair also has to survive options validation, because the DMS host validates on
        // start: a failure here would refuse to start every non-CDC stack.
        new DocumentCacheOptionsValidator(configuration)
            .Validate(Options.DefaultName, options)
            .Failed.Should()
            .BeFalse();
    }

    [Test]
    public void It_rejects_malformed_status_timeout_values_during_binding()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Status:StatusObservationTimeout"] = "five-seconds",
                }
            )
            .Build();

        DocumentCacheOptions options = new();
        Action bind = () => configuration.GetSection(DocumentCacheOptions.SectionName).Bind(options);

        bind.Should().Throw<InvalidOperationException>().WithMessage("*StatusObservationTimeout*");
    }
}
