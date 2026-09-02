// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Backend.Cdc.Control;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CommandSurface")]
public sealed class Given_DocumentCacheAdminCdcCommandSurface
{
    [Test]
    public void It_exposes_exactly_the_cdc_verbs()
    {
        CdcCommand()
            .Subcommands.Select(command => command.Name)
            .Should()
            .Equal(
                DocumentCacheAdminCommandSurface.CdcEnableVerbName,
                DocumentCacheAdminCommandSurface.CdcStatusVerbName,
                DocumentCacheAdminCommandSurface.CdcRestartVerbName,
                DocumentCacheAdminCommandSurface.CdcAdoptVerbName,
                DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName,
                DocumentCacheAdminCommandSurface.CdcRetireVerbName
            );
    }

    [Test]
    public void It_exposes_the_cdc_deployment_options_on_every_verb()
    {
        foreach (Command verb in CdcCommand().Subcommands)
        {
            verb.Options.Select(option => option.Name)
                .Should()
                .Contain(
                    [
                        DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                        DocumentCacheAdminCommandSurface.TenantKeyOptionName,
                        DocumentCacheAdminCommandSurface.CdcBindingStatePathOptionName,
                        DocumentCacheAdminCommandSurface.DeploymentKeyOptionName,
                        DocumentCacheAdminCommandSurface.InstanceKeyOptionName,
                        DocumentCacheAdminCommandSurface.GenerationOptionName,
                        DocumentCacheAdminCommandSurface.KafkaBootstrapServersOptionName,
                        DocumentCacheAdminCommandSurface.ConnectBaseUrlOptionName,
                        DocumentCacheAdminCommandSurface.MaxRecordBytesOptionName,
                        DocumentCacheAdminCommandSurface.DurabilityProfileOptionName,
                    ],
                    verb.Name
                );
        }
    }

    [Test]
    public void It_exposes_the_provisioning_evidence_flags_only_where_the_readiness_sequence_runs()
    {
        foreach (Command verb in CdcCommand().Subcommands)
        {
            IEnumerable<string> optionNames = verb.Options.Select(option => option.Name);
            string[] evidenceOptions =
            [
                DocumentCacheAdminCommandSurface.DatabaseCreationModeOptionName,
                DocumentCacheAdminCommandSurface.WriteAdmissionOptionName,
            ];

            if (DocumentCacheAdminCommandSurface.RequiresCdcProvisioningEvidence(verb.Name))
            {
                optionNames.Should().Contain(evidenceOptions, verb.Name);
                continue;
            }

            optionNames.Should().NotContain(evidenceOptions, verb.Name);
        }
    }

    [Test]
    public void It_exposes_confirmation_only_for_replace_source_and_retire()
    {
        foreach (Command verb in CdcCommand().Subcommands)
        {
            IEnumerable<string> optionNames = verb.Options.Select(option => option.Name);

            if (DocumentCacheAdminCommandSurface.ExpectedCdcConfirmationOptionValue(verb.Name) is not null)
            {
                optionNames.Should().Contain(DocumentCacheAdminCommandSurface.ConfirmOptionName, verb.Name);
                continue;
            }

            optionNames.Should().NotContain(DocumentCacheAdminCommandSurface.ConfirmOptionName, verb.Name);
        }
    }

    [Test]
    public void It_requires_a_confirmation_token_for_replace_source_and_retire()
    {
        DocumentCacheAdminCommandSurface
            .ExpectedCdcConfirmationOptionValue(DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName)
            .Should()
            .Be(DocumentCacheAdminCommandSurface.CdcSourceReplacementConfirmationOptionValue);
        DocumentCacheAdminCommandSurface
            .ExpectedCdcConfirmationOptionValue(DocumentCacheAdminCommandSurface.CdcRetireVerbName)
            .Should()
            .Be(DocumentCacheAdminCommandSurface.CdcBindingRetirementConfirmationOptionValue);
        DocumentCacheAdminCommandSurface
            .ExpectedCdcConfirmationOptionValue(DocumentCacheAdminCommandSurface.CdcEnableVerbName)
            .Should()
            .BeNull();
    }

    [Test]
    public void It_exposes_previous_generation_only_for_replace_source()
    {
        foreach (Command verb in CdcCommand().Subcommands)
        {
            IEnumerable<string> optionNames = verb.Options.Select(option => option.Name);

            if (verb.Name == DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName)
            {
                optionNames
                    .Should()
                    .Contain(DocumentCacheAdminCommandSurface.PreviousGenerationOptionName, verb.Name);
                continue;
            }

            optionNames
                .Should()
                .NotContain(DocumentCacheAdminCommandSurface.PreviousGenerationOptionName, verb.Name);
        }
    }

    [Test]
    public void It_exposes_connector_already_absent_only_for_retire()
    {
        foreach (Command verb in CdcCommand().Subcommands)
        {
            IEnumerable<string> optionNames = verb.Options.Select(option => option.Name);

            if (verb.Name == DocumentCacheAdminCommandSurface.CdcRetireVerbName)
            {
                optionNames
                    .Should()
                    .Contain(DocumentCacheAdminCommandSurface.ConnectorAlreadyAbsentOptionName, verb.Name);
                continue;
            }

            optionNames
                .Should()
                .NotContain(DocumentCacheAdminCommandSurface.ConnectorAlreadyAbsentOptionName, verb.Name);
        }
    }

    [Test]
    public void It_does_not_expose_secret_bearing_cdc_options()
    {
        foreach (Command verb in CdcCommand().Subcommands)
        {
            verb.Options.Select(option => option.Name)
                .Should()
                .NotContain(
                    [
                        "--connection-string",
                        "--kafka-password",
                        "--credential",
                        "--client-secret",
                        "--secret",
                        "--password",
                        "--dms-bearer-token",
                    ],
                    verb.Name
                );
        }
    }

    [TestCaseSource(nameof(CdcVerbNameCases))]
    public void It_parses_every_cdc_verb(string verbName)
    {
        Parse(VerbArgs(verbName)).Errors.Should().BeEmpty();
    }

    [TestCaseSource(nameof(CdcVerbNameCases))]
    public void It_reports_the_parsed_cdc_verb(string verbName)
    {
        ParseResult parseResult = Parse(VerbArgs(verbName));

        DocumentCacheAdminCommandSurface.IsCdcCommand(parseResult).Should().BeTrue(verbName);
        DocumentCacheAdminCommandSurface.CdcVerbName(parseResult).Should().Be(verbName);
    }

    [Test]
    public void It_does_not_report_the_document_cache_commands_as_cdc_commands()
    {
        ParseResult statusParseResult = Parse([
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "1",
        ]);

        DocumentCacheAdminCommandSurface.IsCdcCommand(statusParseResult).Should().BeFalse();
        DocumentCacheAdminCommandSurface.CdcVerbName(statusParseResult).Should().BeNull();
    }

    [Test]
    public void It_does_not_report_a_bare_cdc_group_as_a_verb()
    {
        ParseResult parseResult = Parse([DocumentCacheAdminCommandSurface.CdcCommandName]);

        DocumentCacheAdminCommandSurface.IsCdcCommand(parseResult).Should().BeTrue();
        DocumentCacheAdminCommandSurface.CdcVerbName(parseResult).Should().BeNull();
        parseResult.Errors.Should().NotBeEmpty();
    }

    [Test]
    public void It_does_not_apply_document_cache_status_timeout_overrides_to_the_cdc_status_verb()
    {
        IReadOnlyDictionary<string, string?> overrides =
            DocumentCacheAdminCommandSurface.CreateConfigurationOverrides(
                Parse(VerbArgs(DocumentCacheAdminCommandSurface.CdcStatusVerbName))
            );

        overrides
            .Keys.Should()
            .NotContain([
                DocumentCacheAdminCommandSurface.StatusObservationTimeoutConfigurationKey,
                DocumentCacheAdminCommandSurface.StatusEndpointTimeoutConfigurationKey,
                DocumentCacheAdminCommandSurface.AdministrationWorkflowTimeoutConfigurationKey,
            ]);
    }

    [Test]
    public void It_maps_the_supplied_cdc_options_to_control_plane_configuration_keys()
    {
        IReadOnlyDictionary<string, string?> overrides =
            DocumentCacheAdminCommandSurface.CreateConfigurationOverrides(
                Parse([
                    .. VerbArgs(DocumentCacheAdminCommandSurface.CdcStatusVerbName),
                    DocumentCacheAdminCommandSurface.DeploymentKeyOptionName,
                    "deployment-a",
                    DocumentCacheAdminCommandSurface.InstanceKeyOptionName,
                    "instance-a",
                    DocumentCacheAdminCommandSurface.GenerationOptionName,
                    "7",
                    DocumentCacheAdminCommandSurface.KafkaBootstrapServersOptionName,
                    "broker:9092",
                    DocumentCacheAdminCommandSurface.ConnectBaseUrlOptionName,
                    "http://connect:8083",
                    DocumentCacheAdminCommandSurface.MaxRecordBytesOptionName,
                    "4194304",
                    DocumentCacheAdminCommandSurface.DurabilityProfileOptionName,
                    DocumentCacheAdminCommandSurface.ProductionDurabilityProfileOptionValue,
                    DocumentCacheAdminCommandSurface.CdcBindingStatePathOptionName,
                    "./.cdc-state",
                ])
            );

        overrides[DocumentCacheAdminCommandSurface.CdcDeploymentKeyConfigurationKey]
            .Should()
            .Be("deployment-a");
        overrides[DocumentCacheAdminCommandSurface.CdcInstanceKeyConfigurationKey].Should().Be("instance-a");
        overrides[DocumentCacheAdminCommandSurface.CdcGenerationConfigurationKey].Should().Be("7");
        overrides[DocumentCacheAdminCommandSurface.CdcKafkaBootstrapServersConfigurationKey]
            .Should()
            .Be("broker:9092");
        overrides[DocumentCacheAdminCommandSurface.CdcConnectBaseUriConfigurationKey]
            .Should()
            .Be("http://connect:8083");
        overrides[DocumentCacheAdminCommandSurface.CdcMaxRecordBytesConfigurationKey].Should().Be("4194304");
        overrides[DocumentCacheAdminCommandSurface.CdcDurabilityProfileConfigurationKey]
            .Should()
            .Be(DocumentCacheAdminCommandSurface.ProductionDurabilityProfileOptionValue);
        overrides[DocumentCacheAdminCommandSurface.CdcBindingStateRootPathConfigurationKey]
            .Should()
            .Be("./.cdc-state");
    }

    [Test]
    public void It_leaves_an_omitted_cdc_option_out_of_the_configuration_overrides()
    {
        IReadOnlyDictionary<string, string?> overrides =
            DocumentCacheAdminCommandSurface.CreateConfigurationOverrides(
                Parse(VerbArgs(DocumentCacheAdminCommandSurface.CdcStatusVerbName))
            );

        overrides
            .Keys.Should()
            .NotContain([
                DocumentCacheAdminCommandSurface.CdcDeploymentKeyConfigurationKey,
                DocumentCacheAdminCommandSurface.CdcInstanceKeyConfigurationKey,
                DocumentCacheAdminCommandSurface.CdcGenerationConfigurationKey,
                DocumentCacheAdminCommandSurface.CdcKafkaBootstrapServersConfigurationKey,
                DocumentCacheAdminCommandSurface.CdcConnectBaseUriConfigurationKey,
                DocumentCacheAdminCommandSurface.CdcMaxRecordBytesConfigurationKey,
                DocumentCacheAdminCommandSurface.CdcDurabilityProfileConfigurationKey,
                DocumentCacheAdminCommandSurface.CdcBindingStateRootPathConfigurationKey,
            ]);
    }

    [Test]
    public void It_takes_the_evidence_and_durability_tokens_from_the_control_plane_declarations()
    {
        DocumentCacheAdminCommandSurface
            .DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue.Should()
            .Be(CdcProvisioningProofFactory.CreatedForInitialCdcProvisioningToken);
        DocumentCacheAdminCommandSurface
            .WriteAdmissionClosedNeverOpenedOptionValue.Should()
            .Be(CdcProvisioningProofFactory.ClosedNeverOpenedToken);
        DocumentCacheAdminCommandSurface
            .LocalDurabilityProfileOptionValue.Should()
            .Be(CdcControlOptions.LocalDurabilityProfile);
        DocumentCacheAdminCommandSurface
            .ProductionDurabilityProfileOptionValue.Should()
            .Be(CdcControlOptions.ProductionDurabilityProfile);
    }

    [Test]
    public void It_still_maps_the_global_datastore_override_for_a_cdc_verb()
    {
        IReadOnlyDictionary<string, string?> overrides =
            DocumentCacheAdminCommandSurface.CreateConfigurationOverrides(
                Parse([
                    .. VerbArgs(DocumentCacheAdminCommandSurface.CdcStatusVerbName),
                    DocumentCacheAdminCommandSurface.DatastoreOptionName,
                    DocumentCacheAdminCommandSurface.SqlServerDatastoreOptionValue,
                ])
            );

        overrides[DocumentCacheAdminCommandSurface.AppSettingsDatastoreConfigurationKey]
            .Should()
            .Be(DocumentCacheAdminCommandSurface.MssqlAppSettingsDatastoreValue);
    }

    [TestCase(DocumentCacheAdminCommandSurface.CdcEnableVerbName)]
    [TestCase(DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName)]
    public void It_requires_both_provisioning_evidence_flags(string verbName)
    {
        Parse([
            .. VerbArgsWithoutEvidence(verbName),
            DocumentCacheAdminCommandSurface.DatabaseCreationModeOptionName,
            DocumentCacheAdminCommandSurface.DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue,
        ])
            .Errors.Should()
            .NotBeEmpty();

        Parse([
            .. VerbArgsWithoutEvidence(verbName),
            DocumentCacheAdminCommandSurface.WriteAdmissionOptionName,
            DocumentCacheAdminCommandSurface.WriteAdmissionClosedNeverOpenedOptionValue,
        ])
            .Errors.Should()
            .NotBeEmpty();

        Parse(VerbArgsWithoutEvidence(verbName)).Errors.Should().NotBeEmpty();
    }

    [TestCase("createdForInitialCdcProvisioning")]
    [TestCase("Created-For-Initial-Cdc-Provisioning")]
    [TestCase("created-for-cdc-provisioning")]
    [TestCase("true")]
    [TestCase("")]
    public void It_rejects_any_other_database_creation_mode_value(string value)
    {
        Parse([
            .. VerbArgsWithoutEvidence(DocumentCacheAdminCommandSurface.CdcEnableVerbName),
            DocumentCacheAdminCommandSurface.DatabaseCreationModeOptionName,
            value,
            DocumentCacheAdminCommandSurface.WriteAdmissionOptionName,
            DocumentCacheAdminCommandSurface.WriteAdmissionClosedNeverOpenedOptionValue,
        ])
            .Errors.Should()
            .NotBeEmpty();
    }

    [TestCase("closedNeverOpened")]
    [TestCase("Closed-Never-Opened")]
    [TestCase("closed-and-drained")]
    [TestCase("true")]
    [TestCase("")]
    public void It_rejects_any_other_write_admission_value(string value)
    {
        Parse([
            .. VerbArgsWithoutEvidence(DocumentCacheAdminCommandSurface.CdcEnableVerbName),
            DocumentCacheAdminCommandSurface.DatabaseCreationModeOptionName,
            DocumentCacheAdminCommandSurface.DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue,
            DocumentCacheAdminCommandSurface.WriteAdmissionOptionName,
            value,
        ])
            .Errors.Should()
            .NotBeEmpty();
    }

    [TestCase(DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName)]
    [TestCase(DocumentCacheAdminCommandSurface.CdcRetireVerbName)]
    public void It_rejects_a_missing_or_wrong_confirmation_token(string verbName)
    {
        string[] argsWithoutConfirmation = ArgsWithoutOption(
            VerbArgs(verbName),
            DocumentCacheAdminCommandSurface.ConfirmOptionName
        );

        Parse(argsWithoutConfirmation).Errors.Should().NotBeEmpty();
        Parse([.. argsWithoutConfirmation, DocumentCacheAdminCommandSurface.ConfirmOptionName, "yes"])
            .Errors.Should()
            .NotBeEmpty();
    }

    [Test]
    public void It_exposes_the_adopted_binding_record_only_for_adopt()
    {
        foreach (Command verb in CdcCommand().Subcommands)
        {
            IEnumerable<string> optionNames = verb.Options.Select(option => option.Name);

            if (verb.Name == DocumentCacheAdminCommandSurface.CdcAdoptVerbName)
            {
                optionNames
                    .Should()
                    .Contain(DocumentCacheAdminCommandSurface.BindingJsonOptionName, verb.Name);
                continue;
            }

            optionNames
                .Should()
                .NotContain(DocumentCacheAdminCommandSurface.BindingJsonOptionName, verb.Name);
        }
    }

    [Test]
    public void It_requires_the_adopted_binding_record_to_be_supplied()
    {
        Parse(
            ArgsWithoutOption(
                VerbArgs(DocumentCacheAdminCommandSurface.CdcAdoptVerbName),
                DocumentCacheAdminCommandSurface.BindingJsonOptionName
            )
        )
            .Errors.Should()
            .NotBeEmpty();
    }

    [Test]
    public void It_requires_the_replaced_generation_to_be_named_explicitly()
    {
        Parse(
            ArgsWithoutOption(
                VerbArgs(DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName),
                DocumentCacheAdminCommandSurface.PreviousGenerationOptionName
            )
        )
            .Errors.Should()
            .NotBeEmpty();
    }

    [TestCase(DocumentCacheAdminCommandSurface.GenerationOptionName, "0")]
    [TestCase(DocumentCacheAdminCommandSurface.GenerationOptionName, "-1")]
    [TestCase(DocumentCacheAdminCommandSurface.MaxRecordBytesOptionName, "0")]
    [TestCase(DocumentCacheAdminCommandSurface.MaxRecordBytesOptionName, "-1048576")]
    [TestCase(DocumentCacheAdminCommandSurface.DataStoreIdOptionName, "0")]
    public void It_rejects_non_positive_numeric_cdc_options(string optionName, string value)
    {
        Parse([
            .. ArgsWithoutOption(VerbArgs(DocumentCacheAdminCommandSurface.CdcStatusVerbName), optionName),
            optionName,
            value,
        ])
            .Errors.Should()
            .NotBeEmpty();
    }

    [Test]
    public void It_rejects_a_non_positive_previous_generation()
    {
        Parse([
            .. ArgsWithoutOption(
                VerbArgs(DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName),
                DocumentCacheAdminCommandSurface.PreviousGenerationOptionName
            ),
            DocumentCacheAdminCommandSurface.PreviousGenerationOptionName,
            "0",
        ])
            .Errors.Should()
            .NotBeEmpty();
    }

    [TestCase(DocumentCacheAdminCommandSurface.LocalDurabilityProfileOptionValue)]
    [TestCase(DocumentCacheAdminCommandSurface.ProductionDurabilityProfileOptionValue)]
    public void It_accepts_the_defined_durability_profiles(string value)
    {
        Parse([
            .. VerbArgs(DocumentCacheAdminCommandSurface.CdcStatusVerbName),
            DocumentCacheAdminCommandSurface.DurabilityProfileOptionName,
            value,
        ])
            .Errors.Should()
            .BeEmpty();
    }

    [TestCase("Local")]
    [TestCase("development")]
    [TestCase("prod")]
    [TestCase("")]
    public void It_rejects_an_unknown_durability_profile(string value)
    {
        Parse([
            .. VerbArgs(DocumentCacheAdminCommandSurface.CdcStatusVerbName),
            DocumentCacheAdminCommandSurface.DurabilityProfileOptionName,
            value,
        ])
            .Errors.Should()
            .NotBeEmpty();
    }

    [Test]
    public void It_lists_the_cdc_group_and_its_verbs_in_help_output()
    {
        HelpOutput([DocumentCacheAdminCommandSurface.CdcCommandName, "--help"])
            .Should()
            .ContainAll(DocumentCacheAdminCommandSurface.CdcVerbNames);

        HelpOutput(["--help"]).Should().Contain(DocumentCacheAdminCommandSurface.CdcCommandName);
    }

    [Test]
    public void It_lists_the_evidence_flags_and_their_exact_tokens_in_enable_help_output()
    {
        string help = HelpOutput([
            DocumentCacheAdminCommandSurface.CdcCommandName,
            DocumentCacheAdminCommandSurface.CdcEnableVerbName,
            "--help",
        ]);

        help.Should()
            .ContainAll(
                DocumentCacheAdminCommandSurface.DatabaseCreationModeOptionName,
                DocumentCacheAdminCommandSurface.WriteAdmissionOptionName,
                DocumentCacheAdminCommandSurface.DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue,
                DocumentCacheAdminCommandSurface.WriteAdmissionClosedNeverOpenedOptionValue
            );
    }

    private static IEnumerable<string> CdcVerbNameCases() => DocumentCacheAdminCommandSurface.CdcVerbNames;

    /// <summary>A minimally valid invocation of one cdc verb.</summary>
    private static string[] VerbArgs(string verbName)
    {
        List<string> args =
        [
            DocumentCacheAdminCommandSurface.CdcCommandName,
            verbName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "1",
        ];

        if (DocumentCacheAdminCommandSurface.RequiresCdcProvisioningEvidence(verbName))
        {
            args.AddRange([
                DocumentCacheAdminCommandSurface.DatabaseCreationModeOptionName,
                DocumentCacheAdminCommandSurface.DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue,
                DocumentCacheAdminCommandSurface.WriteAdmissionOptionName,
                DocumentCacheAdminCommandSurface.WriteAdmissionClosedNeverOpenedOptionValue,
            ]);
        }

        if (verbName == DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName)
        {
            args.AddRange([DocumentCacheAdminCommandSurface.PreviousGenerationOptionName, "3"]);
        }

        if (verbName == DocumentCacheAdminCommandSurface.CdcAdoptVerbName)
        {
            args.AddRange([DocumentCacheAdminCommandSurface.BindingJsonOptionName, "binding.json"]);
        }

        if (DocumentCacheAdminCommandSurface.ExpectedCdcConfirmationOptionValue(verbName) is { } confirmation)
        {
            args.AddRange([DocumentCacheAdminCommandSurface.ConfirmOptionName, confirmation]);
        }

        return [.. args];
    }

    private static string[] VerbArgsWithoutEvidence(string verbName) =>
        ArgsWithoutOption(
            ArgsWithoutOption(
                VerbArgs(verbName),
                DocumentCacheAdminCommandSurface.DatabaseCreationModeOptionName
            ),
            DocumentCacheAdminCommandSurface.WriteAdmissionOptionName
        );

    /// <summary>Drops one <c>--option value</c> pair, so a required-option case can omit exactly it.</summary>
    private static string[] ArgsWithoutOption(string[] args, string optionName)
    {
        List<string> remaining = [];
        int index = 0;

        while (index < args.Length)
        {
            if (string.Equals(args[index], optionName, StringComparison.Ordinal))
            {
                index += 2;
                continue;
            }

            remaining.Add(args[index]);
            index++;
        }

        return [.. remaining];
    }

    private static Command CdcCommand() =>
        DocumentCacheAdminCommandSurface
            .CreateRootCommand()
            .Subcommands.Single(command => command.Name == DocumentCacheAdminCommandSurface.CdcCommandName);

    private static ParseResult Parse(string[] args) =>
        DocumentCacheAdminCommandSurface.CreateRootCommand().Parse(args);

    private static string HelpOutput(string[] args)
    {
        using var output = new StringWriter();
        Parse(args).Invoke(new InvocationConfiguration { Output = output });
        return output.ToString();
    }
}
