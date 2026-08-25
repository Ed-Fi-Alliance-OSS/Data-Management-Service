// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("TargetResolution")]
public sealed class Given_DocumentCacheAdminTargetResolution
{
    [Test]
    public void It_accepts_an_option_target_with_default_tenant_key()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();
        var parseResult = rootCommand.Parse([
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "7",
        ]);

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ => throw new InvalidOperationException("JSON loader should not be used."),
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeTrue(failure);
        invocationTarget!.Source.Should().Be(DocumentCacheAdminInvocationTargetSource.Options);
        invocationTarget.TargetKey.Should().Be(DocumentCacheTargetKey.Create(string.Empty, 7));
    }

    [Test]
    public void It_accepts_an_option_target_with_explicit_tenant_key()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();
        var parseResult = rootCommand.Parse([
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "7",
            DocumentCacheAdminCommandSurface.TenantKeyOptionName,
            "TenantA",
        ]);

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ => throw new InvalidOperationException("JSON loader should not be used."),
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeTrue(failure);
        invocationTarget!.TargetKey.Should().Be(DocumentCacheTargetKey.Create("TenantA", 7));
    }

    [TestCase("0")]
    [TestCase("-1")]
    public void It_rejects_non_positive_option_data_store_ids_during_parsing(string dataStoreId)
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        rootCommand
            .Parse([
                DocumentCacheAdminCommandSurface.StatusCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                dataStoreId,
            ])
            .Errors.Should()
            .NotBeEmpty();
    }

    [Test]
    public void It_rejects_missing_option_target_input()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();
        var parseResult = rootCommand.Parse([DocumentCacheAdminCommandSurface.StatusCommandName]);

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ => throw new InvalidOperationException("JSON loader should not be used."),
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeFalse();
        invocationTarget.Should().BeNull();
        failure.Should().Contain(DocumentCacheAdminCommandSurface.DataStoreIdOptionName);
    }

    [Test]
    public void It_rejects_malformed_option_target_input()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();
        var parseResult = rootCommand.Parse([
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "7",
            DocumentCacheAdminCommandSurface.TenantKeyOptionName,
            " TenantA",
        ]);

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ => throw new InvalidOperationException("JSON loader should not be used."),
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeFalse();
        invocationTarget.Should().BeNull();
        failure.Should().Contain("TenantKey");
    }

    [Test]
    public void It_accepts_exactly_one_json_target_key()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();
        var parseResult = rootCommand.Parse([
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            "request.json",
        ]);

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ => """{"targetKey":{"tenantKey":"TenantA","dataStoreId":7}}""",
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeTrue(failure);
        invocationTarget!.Source.Should().Be(DocumentCacheAdminInvocationTargetSource.RequestJson);
        invocationTarget.TargetKey.Should().Be(DocumentCacheTargetKey.Create("TenantA", 7));
    }

    [Test]
    public void It_rejects_duplicate_option_target_fields_with_request_json()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();
        var parseResult = rootCommand.Parse([
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            "request.json",
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "7",
        ]);

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ => """{"targetKey":{"tenantKey":"","dataStoreId":7}}""",
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeFalse();
        invocationTarget.Should().BeNull();
        failure.Should().Contain("cannot be supplied");
    }

    [TestCase("""{"targetKey":{"tenantKey":"","dataStoreId":7},"extra":true}""")]
    [TestCase("""{"targetKey":{"tenantKey":"","dataStoreId":7,"extra":true}}""")]
    [TestCase("""{"targetKey":{"tenantKey":"","dataStoreId":0}}""")]
    [TestCase("""{"targetKey":{"tenantKey":"","dataStoreId":"7"}}""")]
    [TestCase("""{"targetKey":{"tenantKey":null,"dataStoreId":7}}""")]
    [TestCase("""{"targetKey":{"dataStoreId":7}}""")]
    public void It_rejects_malformed_json_target_input(string requestJson)
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();
        var parseResult = rootCommand.Parse([
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            "-",
        ]);

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ => requestJson,
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeFalse();
        invocationTarget.Should().BeNull();
        failure.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void It_maps_the_invocation_target_to_configuration_overrides()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();
        var parseResult = rootCommand.Parse([
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.DatastoreOptionName,
            DocumentCacheAdminCommandSurface.SqlServerDatastoreOptionValue,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "7",
            DocumentCacheAdminCommandSurface.TenantKeyOptionName,
            "TenantA",
        ]);

        IReadOnlyDictionary<string, string?> overrides =
            DocumentCacheAdminCommandSurface.CreateConfigurationOverrides(
                parseResult,
                DocumentCacheTargetKey.Create("TenantA", 7)
            );

        overrides[DocumentCacheAdminCommandSurface.AppSettingsDatastoreConfigurationKey]
            .Should()
            .Be(DocumentCacheAdminCommandSurface.MssqlAppSettingsDatastoreValue);
        overrides[DocumentCacheAdminCommandSurface.TargetTenantKeyConfigurationKey].Should().Be("TenantA");
        overrides[DocumentCacheAdminCommandSurface.TargetDataStoreIdConfigurationKey].Should().Be("7");
    }

    [Test]
    public void It_loads_settings_environment_and_invocation_target_configuration()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDirectory);
        string settingsPath = Path.Combine(tempDirectory, "dmssettings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "AppSettings": {
                "Datastore": "postgresql"
              },
              "DataManagement": {
                "DocumentCache": {
                  "Targets": [
                    {
                      "TenantKey": "ConfiguredTenant",
                      "DataStoreId": 99
                    }
                  ]
                }
              }
            }
            """
        );

        try
        {
            RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();
            var parseResult = rootCommand.Parse([
                DocumentCacheAdminCommandSurface.StatusCommandName,
                DocumentCacheAdminCommandSurface.SettingsOptionName,
                settingsPath,
                DocumentCacheAdminCommandSurface.EnvironmentOptionName,
                "Development",
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "7",
                DocumentCacheAdminCommandSurface.TenantKeyOptionName,
                "TenantA",
            ]);

            IConfiguration configuration = DocumentCacheAdminConfiguration.Build(
                parseResult,
                DocumentCacheTargetKey.Create("TenantA", 7)
            );

            configuration["AppSettings:Datastore"].Should().Be("postgresql");
            configuration[DocumentCacheAdminCommandSurface.TargetTenantKeyConfigurationKey]
                .Should()
                .Be("TenantA");
            configuration[DocumentCacheAdminCommandSurface.TargetDataStoreIdConfigurationKey]
                .Should()
                .Be("7");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public async Task It_refreshes_the_shared_target_registry_for_the_invocation_target()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("TenantA", 7);
        DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.Unresolved(
            targetKey,
            DocumentCacheTargetEffectiveSettings.FromOptions(new DocumentCacheOptions()),
            retryState: null,
            diagnostics: []
        );
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        DocumentCacheTargetRegistrySnapshot registrySnapshot = new([observation], observedAt);
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot = new([], observedAt);
        IDocumentCacheTargetRegistry registry = A.Fake<IDocumentCacheTargetRegistry>();
        A.CallTo(() =>
                registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup, A<CancellationToken>._)
            )
            .Returns(Task.FromResult(registrySnapshot));
        A.CallTo(() => registry.CurrentRuntimeSnapshot).Returns(runtimeSnapshot);

        var resolver = new DocumentCacheAdminTargetResolver(registry);

        DocumentCacheAdminTargetResolutionResult result = await resolver.ResolveAsync(targetKey);

        result.Outcome.Should().Be(DocumentCacheAdminTargetResolutionOutcome.Completed);
        result.Observation.Should().BeSameAs(observation);
        result.ExecutionContext.Should().BeNull();
        result.CanAttemptMutation.Should().BeFalse();
        A.CallTo(() =>
                registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_rejects_registry_snapshots_that_do_not_match_the_one_invocation_target()
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("TenantA", 7);
        DocumentCacheTargetKey unexpectedTargetKey = DocumentCacheTargetKey.Create("TenantB", 8);
        DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.Configured(
            unexpectedTargetKey,
            DocumentCacheTargetEffectiveSettings.FromOptions(new DocumentCacheOptions())
        );
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        DocumentCacheTargetRegistrySnapshot registrySnapshot = new([observation], observedAt);
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot = new([], observedAt);
        IDocumentCacheTargetRegistry registry = A.Fake<IDocumentCacheTargetRegistry>();
        A.CallTo(() =>
                registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup, A<CancellationToken>._)
            )
            .Returns(Task.FromResult(registrySnapshot));
        A.CallTo(() => registry.CurrentRuntimeSnapshot).Returns(runtimeSnapshot);

        var resolver = new DocumentCacheAdminTargetResolver(registry);

        DocumentCacheAdminTargetResolutionResult result = await resolver.ResolveAsync(targetKey);

        result.Outcome.Should().Be(DocumentCacheAdminTargetResolutionOutcome.UnexpectedTargetMembership);
        result.HasCompleteObservation.Should().BeFalse();
        result.FailureMessage.Should().Contain("exactly the invocation target");
    }
}
