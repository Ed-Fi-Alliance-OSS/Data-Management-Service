// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CommandSurface")]
public sealed class Given_DocumentCacheAdminCommandSurface
{
    [Test]
    public void It_exposes_exactly_the_v1_document_cache_commands()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        rootCommand
            .Subcommands.Select(command => command.Name)
            .Should()
            .BeEquivalentTo(
                [
                    DocumentCacheAdminCommandSurface.StatusCommandName,
                    DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName,
                    DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
                    DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
                    DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                    DocumentCacheAdminCommandSurface.ScrubCommandName,
                    DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
                ],
                options => options.WithStrictOrdering()
            );
    }

    [Test]
    public void It_exposes_global_output_logging_and_configuration_options()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        rootCommand
            .Options.Select(option => option.Name)
            .Should()
            .Contain([
                DocumentCacheAdminCommandSurface.JsonOptionName,
                DocumentCacheAdminCommandSurface.VerboseOptionName,
                DocumentCacheAdminCommandSurface.SettingsOptionName,
                DocumentCacheAdminCommandSurface.EnvironmentOptionName,
                DocumentCacheAdminCommandSurface.DatastoreOptionName,
            ]);
    }

    [Test]
    public void It_does_not_expose_secret_bearing_configuration_options()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        AllOptionNames(rootCommand)
            .Should()
            .NotContain([
                "--connection-string",
                "--cms-credential",
                "--credential",
                "--client-secret",
                "--secret",
                "--password",
            ]);
    }

    [Test]
    public void It_exposes_status_options()
    {
        Command statusCommand = CommandByName(DocumentCacheAdminCommandSurface.StatusCommandName);

        statusCommand
            .Options.Select(option => option.Name)
            .Should()
            .Contain([
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                DocumentCacheAdminCommandSurface.TenantKeyOptionName,
                DocumentCacheAdminCommandSurface.RequestJsonOptionName,
                DocumentCacheAdminCommandSurface.StatusObservationTimeoutSecondsOptionName,
                DocumentCacheAdminCommandSurface.StatusTimeoutSecondsOptionName,
            ]);
    }

    [Test]
    public void It_exposes_mutating_command_options()
    {
        foreach (Command command in MutatingCommands())
        {
            command
                .Options.Select(option => option.Name)
                .Should()
                .Contain(
                    [
                        DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                        DocumentCacheAdminCommandSurface.TenantKeyOptionName,
                        DocumentCacheAdminCommandSurface.RequestJsonOptionName,
                        DocumentCacheAdminCommandSurface.ConfirmOptionName,
                        DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName,
                        DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
                    ],
                    command.Name
                );
        }
    }

    [Test]
    public void It_exposes_offline_writer_admission_only_for_writer_fenced_commands()
    {
        CommandByName(DocumentCacheAdminCommandSurface.ActivateOfflineCommandName)
            .Options.Select(option => option.Name)
            .Should()
            .Contain(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);
        CommandByName(DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName)
            .Options.Select(option => option.Name)
            .Should()
            .Contain(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);
        CommandByName(DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName)
            .Options.Select(option => option.Name)
            .Should()
            .Contain(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);

        CommandByName(DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName)
            .Options.Select(option => option.Name)
            .Should()
            .NotContain(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);
        CommandByName(DocumentCacheAdminCommandSurface.RebuildOnlineCommandName)
            .Options.Select(option => option.Name)
            .Should()
            .NotContain(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);
        CommandByName(DocumentCacheAdminCommandSurface.ScrubCommandName)
            .Options.Select(option => option.Name)
            .Should()
            .NotContain(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);
    }

    [Test]
    public void It_uses_the_supported_timeout_defaults()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        rootCommand
            .Parse(DocumentCacheAdminCommandSurface.StatusCommandName)
            .GetRequiredValue<string>(
                DocumentCacheAdminCommandSurface.StatusObservationTimeoutSecondsOptionName
            )
            .Should()
            .Be(DocumentCacheAdminCommandSurface.DefaultStatusObservationTimeoutSeconds);
        rootCommand
            .Parse(DocumentCacheAdminCommandSurface.StatusCommandName)
            .GetRequiredValue<string>(DocumentCacheAdminCommandSurface.StatusTimeoutSecondsOptionName)
            .Should()
            .Be(DocumentCacheAdminCommandSurface.DefaultStatusTimeoutSeconds);
        rootCommand
            .Parse(ValidMutatingArgs(DocumentCacheAdminCommandSurface.RebuildOnlineCommandName))
            .GetRequiredValue<string>(DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName)
            .Should()
            .Be(DocumentCacheAdminCommandSurface.DefaultCommandTimeoutSeconds);
    }

    [TestCase(DocumentCacheAdminCommandSurface.StatusCommandName, "--status-observation-timeout-seconds")]
    [TestCase(DocumentCacheAdminCommandSurface.StatusCommandName, "--status-timeout-seconds")]
    [TestCase(DocumentCacheAdminCommandSurface.RebuildOnlineCommandName, "--command-timeout-seconds")]
    public void It_accepts_positive_fractional_timeout_values(string commandName, string optionName)
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        rootCommand
            .Parse(ArgsWithOptionalConfirmation(commandName, optionName, "1.25"))
            .Errors.Should()
            .BeEmpty();
    }

    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("not-a-number")]
    [TestCase("1e2")]
    [TestCase("999999999999999999999999999999999999999999999999999999999999999999999999")]
    public void It_rejects_invalid_status_observation_timeout_values(string value)
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        rootCommand
            .Parse([
                DocumentCacheAdminCommandSurface.StatusCommandName,
                "--status-observation-timeout-seconds",
                value,
            ])
            .Errors.Should()
            .NotBeEmpty();
    }

    [TestCase(DocumentCacheAdminCommandSurface.StatusCommandName, "--timeout")]
    [TestCase(DocumentCacheAdminCommandSurface.RebuildOnlineCommandName, "--timeout")]
    [TestCase(DocumentCacheAdminCommandSurface.RebuildOnlineCommandName, "--provider-command-timeout")]
    [TestCase(DocumentCacheAdminCommandSurface.RebuildOnlineCommandName, "--mutex-timeout")]
    public void It_rejects_unsupported_timeout_aliases(string commandName, string optionName)
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        rootCommand
            .Parse(ArgsWithOptionalConfirmation(commandName, optionName, "1"))
            .Errors.Should()
            .NotBeEmpty();
    }

    [Test]
    public void It_rejects_unsupported_datastore_values()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        rootCommand
            .Parse([DocumentCacheAdminCommandSurface.StatusCommandName, "--datastore", "oracle"])
            .Errors.Should()
            .NotBeEmpty();
    }

    [Test]
    public void It_maps_status_timeout_options_to_document_cache_configuration()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        IReadOnlyDictionary<string, string?> overrides =
            DocumentCacheAdminCommandSurface.CreateConfigurationOverrides(
                rootCommand.Parse([
                    DocumentCacheAdminCommandSurface.StatusCommandName,
                    "--status-observation-timeout-seconds",
                    "1.25",
                    "--status-timeout-seconds",
                    "2.5",
                ])
            );

        overrides[DocumentCacheAdminCommandSurface.StatusObservationTimeoutConfigurationKey]
            .Should()
            .Be(TimeSpan.FromSeconds(1.25).ToString("c"));
        overrides[DocumentCacheAdminCommandSurface.StatusEndpointTimeoutConfigurationKey]
            .Should()
            .Be(TimeSpan.FromSeconds(2.5).ToString("c"));
    }

    [Test]
    public void It_maps_command_timeout_and_sqlserver_datastore_to_runtime_configuration()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        IReadOnlyDictionary<string, string?> overrides =
            DocumentCacheAdminCommandSurface.CreateConfigurationOverrides(
                rootCommand.Parse([
                    .. ValidMutatingArgs(DocumentCacheAdminCommandSurface.RebuildOnlineCommandName),
                    "--command-timeout-seconds",
                    "1.5",
                    "--datastore",
                    DocumentCacheAdminCommandSurface.SqlServerDatastoreOptionValue,
                ])
            );

        overrides[DocumentCacheAdminCommandSurface.AdministrationWorkflowTimeoutConfigurationKey]
            .Should()
            .Be(TimeSpan.FromSeconds(1.5).ToString("c"));
        overrides[DocumentCacheAdminCommandSurface.AppSettingsDatastoreConfigurationKey]
            .Should()
            .Be(DocumentCacheAdminCommandSurface.MssqlAppSettingsDatastoreValue);
    }

    private static Command CommandByName(string commandName) =>
        DocumentCacheAdminCommandSurface
            .CreateRootCommand()
            .Subcommands.Single(command => command.Name == commandName);

    private static IEnumerable<Command> MutatingCommands() =>
        DocumentCacheAdminCommandSurface
            .CreateRootCommand()
            .Subcommands.Where(command => command.Name != DocumentCacheAdminCommandSurface.StatusCommandName);

    private static IEnumerable<string> AllOptionNames(Command command) =>
        command.Options.Select(option => option.Name).Concat(command.Subcommands.SelectMany(AllOptionNames));

    private static string[] ArgsWithOptionalConfirmation(
        string commandName,
        string optionName,
        string optionValue
    ) =>
        DocumentCacheAdminCommandSurface.IsMutatingCommand(commandName)
            ? [.. ValidMutatingArgs(commandName), optionName, optionValue]
            : [commandName, optionName, optionValue];

    private static string[] ValidMutatingArgs(string commandName) =>
        [
            commandName,
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            DocumentCacheAdminTestCommandContracts.ExpectedConfirmationJsonValue(commandName),
        ];
}
