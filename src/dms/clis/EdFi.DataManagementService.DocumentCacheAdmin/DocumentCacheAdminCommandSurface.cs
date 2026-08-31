// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Numerics;
using EdFi.DataManagementService.Backend.Cdc.Control;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal static class DocumentCacheAdminCommandSurface
{
    public const string StatusCommandName = "status";
    public const string ActivateNewEmptyCommandName = "activate-new-empty";
    public const string ActivateOfflineCommandName = "activate-offline";
    public const string DeactivateOfflineCommandName = "deactivate-offline";
    public const string RebuildOnlineCommandName = "rebuild-online";
    public const string ScrubCommandName = "scrub";
    public const string RecoverCacheAheadCommandName = "recover-cache-ahead";

    /// <summary>
    /// Deployment-owned CDC binding operations are a verb group rather than seven more root commands,
    /// so their verbs are scoped names: <c>cdc status</c> is not the DocumentCache <c>status</c>
    /// command even though the two share a verb name. Use <see cref="IsCdcCommand(ParseResult)"/>
    /// wherever command identity is decided from a parse result.
    /// </summary>
    public const string CdcCommandName = "cdc";

    public const string CdcEnableVerbName = "enable";
    public const string CdcStatusVerbName = "status";
    public const string CdcRestartVerbName = "restart";
    public const string CdcAdoptVerbName = "adopt";
    public const string CdcReplaceSourceVerbName = "replace-source";
    public const string CdcRetireVerbName = "retire";

    public const string JsonOptionName = "--json";
    public const string VerboseOptionName = "--verbose";
    public const string SettingsOptionName = "--settings";
    public const string EnvironmentOptionName = "--environment";
    public const string DatastoreOptionName = "--datastore";
    public const string TenantKeyOptionName = "--tenant-key";
    public const string DataStoreIdOptionName = "--data-store-id";
    public const string RequestJsonOptionName = "--request-json";
    public const string ConfirmOptionName = "--confirm";
    public const string OfflineWriterAdmissionOptionName = "--offline-writer-admission";
    public const string ExpectedPhysicalSourceFingerprintOptionName =
        "--expected-physical-source-fingerprint";
    public const string CommandTimeoutSecondsOptionName = "--command-timeout-seconds";
    public const string StatusObservationTimeoutSecondsOptionName = "--status-observation-timeout-seconds";
    public const string StatusTimeoutSecondsOptionName = "--status-timeout-seconds";

    public const string CdcBindingStatePathOptionName = "--cdc-binding-state-path";
    public const string DeploymentKeyOptionName = "--deployment-key";
    public const string InstanceKeyOptionName = "--instance-key";
    public const string GenerationOptionName = "--generation";
    public const string PreviousGenerationOptionName = "--previous-generation";
    public const string KafkaBootstrapServersOptionName = "--kafka-bootstrap-servers";
    public const string ConnectBaseUrlOptionName = "--connect-base-url";
    public const string MaxRecordBytesOptionName = "--max-record-bytes";
    public const string DurabilityProfileOptionName = "--durability-profile";
    public const string DatabaseCreationModeOptionName = "--database-creation-mode";
    public const string WriteAdmissionOptionName = "--write-admission";
    public const string BindingJsonOptionName = "--binding-json";

    public const string PostgresqlDatastoreOptionValue = "postgresql";
    public const string SqlServerDatastoreOptionValue = "sqlserver";
    public const string MssqlAppSettingsDatastoreValue = "mssql";

    public const string AppSettingsDatastoreConfigurationKey = "AppSettings:Datastore";
    public const string AdministrationWorkflowTimeoutConfigurationKey =
        "DataManagement:DocumentCache:Administration:WorkflowTimeout";
    public const string StatusObservationTimeoutConfigurationKey =
        "DataManagement:DocumentCache:Status:StatusObservationTimeout";
    public const string StatusEndpointTimeoutConfigurationKey =
        "DataManagement:DocumentCache:Status:EndpointTimeout";
    public const string DefaultCommandTimeoutSeconds = "86400";
    public const string DefaultStatusObservationTimeoutSeconds = "5";
    public const string DefaultStatusTimeoutSeconds = "30";
    public const string OfflineWriterAdmissionClosedAndDrainedOptionValue =
        DocumentCacheOfflineWriterAdmission.ClosedAndDrainedJsonValue;

    // The durability-profile and provisioning-evidence tokens are the CDC control plane's own, so they
    // are taken from its declarations rather than restated: an operator token that drifted from the
    // token the control plane matches would be accepted here and refused there.
    public const string LocalDurabilityProfileOptionValue = CdcControlOptions.LocalDurabilityProfile;
    public const string ProductionDurabilityProfileOptionValue =
        CdcControlOptions.ProductionDurabilityProfile;
    public const string DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue =
        CdcProvisioningProofFactory.CreatedForInitialCdcProvisioningToken;
    public const string WriteAdmissionClosedNeverOpenedOptionValue =
        CdcProvisioningProofFactory.ClosedNeverOpenedToken;

    public const string CdcSourceReplacementConfirmationOptionValue = "cdcSourceReplacement";
    public const string CdcBindingRetirementConfirmationOptionValue = "cdcBindingRetirement";

    public const string CdcDeploymentKeyConfigurationKey = $"{CdcControlOptions.SectionName}:DeploymentKey";
    public const string CdcInstanceKeyConfigurationKey = $"{CdcControlOptions.SectionName}:InstanceKey";
    public const string CdcGenerationConfigurationKey = $"{CdcControlOptions.SectionName}:Generation";
    public const string CdcKafkaBootstrapServersConfigurationKey =
        $"{CdcControlOptions.SectionName}:KafkaBootstrapServers";
    public const string CdcConnectBaseUriConfigurationKey = $"{CdcControlOptions.SectionName}:ConnectBaseUri";
    public const string CdcMaxRecordBytesConfigurationKey = $"{CdcControlOptions.SectionName}:MaxRecordBytes";
    public const string CdcDurabilityProfileConfigurationKey =
        $"{CdcControlOptions.SectionName}:DurabilityProfile";
    public const string CdcBindingStateRootPathConfigurationKey =
        $"{CdcControlServiceCollectionExtensions.BindingStateStoreSectionName}:RootPath";

    public static IReadOnlyList<string> CdcVerbNames { get; } =
    [
        CdcEnableVerbName,
        CdcStatusVerbName,
        CdcRestartVerbName,
        CdcAdoptVerbName,
        CdcReplaceSourceVerbName,
        CdcRetireVerbName,
    ];

    public static RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("Ed-Fi DMS DocumentCache administration tool");

        AddGlobalOptions(rootCommand);
        rootCommand.Subcommands.Add(CreateStatusCommand());
        rootCommand.Subcommands.Add(
            CreateMutatingCommand(ActivateNewEmptyCommandName, "Activate DocumentCache on a new empty target")
        );
        rootCommand.Subcommands.Add(
            CreateMutatingCommand(ActivateOfflineCommandName, "Activate DocumentCache with writers offline")
        );
        rootCommand.Subcommands.Add(
            CreateMutatingCommand(
                DeactivateOfflineCommandName,
                "Deactivate DocumentCache with writers offline"
            )
        );
        rootCommand.Subcommands.Add(
            CreateMutatingCommand(RebuildOnlineCommandName, "Rebuild DocumentCache while online")
        );
        rootCommand.Subcommands.Add(
            CreateMutatingCommand(ScrubCommandName, "Run DocumentCache integrity scrub")
        );
        rootCommand.Subcommands.Add(
            CreateMutatingCommand(RecoverCacheAheadCommandName, "Recover from a required cache-ahead state")
        );
        rootCommand.Subcommands.Add(CreateCdcCommand());

        return rootCommand;
    }

    public static IReadOnlyDictionary<string, string?> CreateConfigurationOverrides(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        Dictionary<string, string?> overrides = [];

        string? datastore = parseResult.GetValue<string?>(DatastoreOptionName);
        if (!string.IsNullOrWhiteSpace(datastore))
        {
            overrides[AppSettingsDatastoreConfigurationKey] = ToAppSettingsDatastoreValue(datastore);
        }

        if (IsCdcCommand(parseResult))
        {
            // A cdc verb carries none of the DocumentCache status or administrative timeout options, and
            // its `status` verb shares a name with the DocumentCache `status` command, so the group is
            // recognized before any name comparison below.
            AddCdcConfigurationOverrides(parseResult, overrides);
            return overrides;
        }

        string commandName = parseResult.CommandResult.Command.Name;
        if (string.Equals(commandName, StatusCommandName, StringComparison.Ordinal))
        {
            overrides[StatusObservationTimeoutConfigurationKey] = ToConfigurationTimeSpanValue(
                parseResult.GetRequiredValue<string>(StatusObservationTimeoutSecondsOptionName)
            );
            overrides[StatusEndpointTimeoutConfigurationKey] = ToConfigurationTimeSpanValue(
                parseResult.GetRequiredValue<string>(StatusTimeoutSecondsOptionName)
            );
        }
        else if (IsMutatingCommand(commandName))
        {
            overrides[AdministrationWorkflowTimeoutConfigurationKey] = ToConfigurationTimeSpanValue(
                parseResult.GetRequiredValue<string>(CommandTimeoutSecondsOptionName)
            );
        }

        return overrides;
    }

    public static bool ShouldInvokeWithoutRuntime(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        return parseResult.Action is HelpAction;
    }

    public static bool IsMutatingCommand(string commandName) =>
        DocumentCacheAdminMutatingCommandContracts.TryGet(commandName, out _);

    /// <summary>
    /// True when the parsed command is the <c>cdc</c> group or one of its verbs. Command identity for a
    /// verb group cannot come from the leaf name alone, because <c>cdc status</c> and the DocumentCache
    /// <c>status</c> command share one.
    /// </summary>
    public static bool IsCdcCommand(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        Command command = parseResult.CommandResult.Command;
        return string.Equals(command.Name, CdcCommandName, StringComparison.Ordinal)
            || command.Parents.Any(parent =>
                string.Equals(parent.Name, CdcCommandName, StringComparison.Ordinal)
            );
    }

    /// <summary>
    /// The cdc verb the parse result names, or null when it is not a cdc verb. The bare <c>cdc</c> group
    /// yields null: a group with no verb is a parse error rather than an operation.
    /// </summary>
    public static string? CdcVerbName(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        if (!IsCdcCommand(parseResult))
        {
            return null;
        }

        string commandName = parseResult.CommandResult.Command.Name;
        return CdcVerbNames.Contains(commandName, StringComparer.Ordinal) ? commandName : null;
    }

    /// <summary>
    /// The scoped label one cdc verb is reported and logged under. The leaf verb name alone would put
    /// <c>cdc status</c> and the DocumentCache <c>status</c> command under one label.
    /// </summary>
    public static string CdcCommandLabel(string verbName) => $"{CdcCommandName} {verbName}";

    /// <summary>
    /// The exact confirmation token a cdc verb requires, or null when the verb takes no confirmation.
    /// </summary>
    public static string? ExpectedCdcConfirmationOptionValue(string verbName) =>
        verbName switch
        {
            CdcReplaceSourceVerbName => CdcSourceReplacementConfirmationOptionValue,
            CdcRetireVerbName => CdcBindingRetirementConfirmationOptionValue,
            _ => null,
        };

    /// <summary>
    /// True for the cdc verbs that run the initial readiness sequence, which is admitted only against
    /// operator-attested provisioning evidence. <c>replace-source</c> is included because the replacing
    /// generation runs that same sequence and its request carries the same evidence.
    /// </summary>
    public static bool RequiresCdcProvisioningEvidence(string verbName) =>
        string.Equals(verbName, CdcEnableVerbName, StringComparison.Ordinal)
        || string.Equals(verbName, CdcReplaceSourceVerbName, StringComparison.Ordinal);

    public static bool RequiresOfflineWriterAdmission(string commandName) =>
        DocumentCacheAdminMutatingCommandContracts.TryGet(
            commandName,
            out DocumentCacheAdminMutatingCommandContract? contract
        ) && contract.RequiresOfflineWriterAdmission;

    public static string ExpectedConfirmationOptionValue(string commandName)
    {
        if (!DocumentCacheAdminMutatingCommandContracts.TryGet(commandName, out var contract))
        {
            throw new ArgumentException(
                $"Command '{commandName}' does not have an expected confirmation.",
                nameof(commandName)
            );
        }

        return contract.ExpectedConfirmationJsonValue;
    }

    public static bool TryParsePositiveSeconds(string? value, out TimeSpan timeSpan)
    {
        timeSpan = TimeSpan.Zero;

        if (
            string.IsNullOrWhiteSpace(value)
            || !double.TryParse(
                value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out double seconds
            )
            || !double.IsFinite(seconds)
            || seconds <= 0
        )
        {
            return false;
        }

        try
        {
            timeSpan = TimeSpan.FromSeconds(seconds);
        }
        catch (OverflowException)
        {
            return false;
        }

        return timeSpan > TimeSpan.Zero;
    }

    private static void AddGlobalOptions(Command command)
    {
        command.Options.Add(
            new Option<bool>(JsonOptionName)
            {
                Description = "Write the shared JSON contract document to stdout",
                Recursive = true,
            }
        );
        command.Options.Add(
            new Option<bool>(VerboseOptionName, "-v")
            {
                Description = "Enable verbose (debug-level) logging",
                Recursive = true,
            }
        );
        command.Options.Add(
            new Option<string?>(SettingsOptionName)
            {
                Description = "Path to a DMS appsettings JSON file",
                Recursive = true,
            }
        );
        command.Options.Add(
            new Option<string?>(EnvironmentOptionName)
            {
                Description = "DMS environment name used for configuration loading",
                Recursive = true,
            }
        );

        var datastoreOption = new Option<string?>(DatastoreOptionName)
        {
            Description = "Target datastore provider: postgresql or sqlserver",
            Recursive = true,
        };
        datastoreOption.AcceptOnlyFromAmong(PostgresqlDatastoreOptionValue, SqlServerDatastoreOptionValue);
        command.Options.Add(datastoreOption);
    }

    private static Command CreateStatusCommand()
    {
        var command = new Command(StatusCommandName, "Inspect one DocumentCache target");
        AddTargetOptions(command);
        command.Options.Add(CreateRequestJsonOption());
        command.Options.Add(
            CreatePositiveSecondsOption(
                StatusObservationTimeoutSecondsOptionName,
                "Provider durable-observation timeout in seconds",
                DefaultStatusObservationTimeoutSeconds
            )
        );
        command.Options.Add(
            CreatePositiveSecondsOption(
                StatusTimeoutSecondsOptionName,
                "Total status evaluation timeout in seconds",
                DefaultStatusTimeoutSeconds
            )
        );
        command.SetAction(ExecuteCommandSurfaceOnly);
        return command;
    }

    private static Command CreateMutatingCommand(string name, string description)
    {
        var command = new Command(name, description);

        AddTargetOptions(command);
        command.Options.Add(CreateRequestJsonOption());
        var confirmOption = new Option<string?>(ConfirmOptionName)
        {
            Description = "Command confirmation token",
        };
        command.Options.Add(confirmOption);
        command.Options.Add(
            new Option<string?>(ExpectedPhysicalSourceFingerprintOptionName)
            {
                Description = "Optional opaque physical source fingerprint guard",
            }
        );
        command.Options.Add(
            CreatePositiveSecondsOption(
                CommandTimeoutSecondsOptionName,
                "Total administrative command timeout in seconds",
                DefaultCommandTimeoutSeconds
            )
        );

        if (RequiresOfflineWriterAdmission(name))
        {
            var offlineWriterAdmissionOption = new Option<string?>(OfflineWriterAdmissionOptionName)
            {
                Description = "Offline writer admission acknowledgement",
            };
            command.Options.Add(offlineWriterAdmissionOption);
        }

        command.Validators.Add(result => ValidateMutatingCommandOptions(result, name));
        command.SetAction(ExecuteCommandSurfaceOnly);
        return command;
    }

    /// <summary>
    /// Projects the cdc options onto the configuration keys the control plane binds
    /// <see cref="CdcControlOptions"/> from, so a command-line value and a settings-file value reach the
    /// same place. Only options the operator actually supplied are written: an absent option must leave
    /// the configured value alone rather than overwrite it with a blank.
    /// </summary>
    private static void AddCdcConfigurationOverrides(
        ParseResult parseResult,
        Dictionary<string, string?> overrides
    )
    {
        AddSuppliedText(parseResult, overrides, DeploymentKeyOptionName, CdcDeploymentKeyConfigurationKey);
        AddSuppliedText(parseResult, overrides, InstanceKeyOptionName, CdcInstanceKeyConfigurationKey);
        AddSuppliedText(
            parseResult,
            overrides,
            KafkaBootstrapServersOptionName,
            CdcKafkaBootstrapServersConfigurationKey
        );
        AddSuppliedText(parseResult, overrides, ConnectBaseUrlOptionName, CdcConnectBaseUriConfigurationKey);
        AddSuppliedText(
            parseResult,
            overrides,
            DurabilityProfileOptionName,
            CdcDurabilityProfileConfigurationKey
        );
        AddSuppliedText(
            parseResult,
            overrides,
            CdcBindingStatePathOptionName,
            CdcBindingStateRootPathConfigurationKey
        );
        AddSuppliedNumber<long>(parseResult, overrides, GenerationOptionName, CdcGenerationConfigurationKey);
        AddSuppliedNumber<int>(
            parseResult,
            overrides,
            MaxRecordBytesOptionName,
            CdcMaxRecordBytesConfigurationKey
        );
    }

    private static void AddSuppliedText(
        ParseResult parseResult,
        Dictionary<string, string?> overrides,
        string optionName,
        string configurationKey
    )
    {
        if (GetSpecifiedOption(parseResult, optionName)?.GetValueOrDefault<string?>() is { } value)
        {
            overrides[configurationKey] = value;
        }
    }

    private static void AddSuppliedNumber<T>(
        ParseResult parseResult,
        Dictionary<string, string?> overrides,
        string optionName,
        string configurationKey
    )
        where T : struct, INumber<T>
    {
        if (GetSpecifiedOption(parseResult, optionName)?.GetValueOrDefault<T?>() is { } value)
        {
            overrides[configurationKey] = value.ToString(null, CultureInfo.InvariantCulture);
        }
    }

    private static Command CreateCdcCommand()
    {
        var command = new Command(
            CdcCommandName,
            "Deployment-owned CDC binding operations for one DocumentCache target"
        );

        foreach (string verbName in CdcVerbNames)
        {
            command.Subcommands.Add(CreateCdcVerbCommand(verbName));
        }

        return command;
    }

    private static Command CreateCdcVerbCommand(string verbName)
    {
        var command = new Command(verbName, CdcVerbDescription(verbName));

        AddTargetOptions(command);
        command.Options.Add(
            new Option<string?>(CdcBindingStatePathOptionName)
            {
                Description = "Root path of the durable CDC binding state store",
            }
        );
        command.Options.Add(
            new Option<string?>(DeploymentKeyOptionName)
            {
                Description = "Opaque deployment key contributing to governed artifact names",
            }
        );
        command.Options.Add(
            new Option<string?>(InstanceKeyOptionName)
            {
                Description = "Opaque instance key contributing to governed artifact names",
            }
        );
        command.Options.Add(
            CreatePositiveNumberOption<long>(GenerationOptionName, "Positive binding generation")
        );
        command.Options.Add(
            new Option<string?>(KafkaBootstrapServersOptionName)
            {
                Description = "Kafka bootstrap servers the governed topics are provisioned through",
            }
        );
        command.Options.Add(
            new Option<string?>(ConnectBaseUrlOptionName)
            {
                Description = "Base URL of the Kafka Connect REST interface",
            }
        );
        command.Options.Add(
            CreatePositiveNumberOption<int>(
                MaxRecordBytesOptionName,
                "Largest record the pipeline must carry end to end"
            )
        );

        var durabilityProfileOption = new Option<string?>(DurabilityProfileOptionName)
        {
            Description =
                $"Durability profile: {LocalDurabilityProfileOptionValue} or {ProductionDurabilityProfileOptionValue}",
        };
        durabilityProfileOption.AcceptOnlyFromAmong(
            LocalDurabilityProfileOptionValue,
            ProductionDurabilityProfileOptionValue
        );
        command.Options.Add(durabilityProfileOption);

        if (RequiresCdcProvisioningEvidence(verbName))
        {
            command.Options.Add(
                new Option<string?>(DatabaseCreationModeOptionName)
                {
                    Description =
                        $"Operator evidence that the physical database was created for this CDC provisioning; must be '{DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue}'",
                }
            );
            command.Options.Add(
                new Option<string?>(WriteAdmissionOptionName)
                {
                    Description =
                        $"Operator evidence that write admission has been closed since the database was created; must be '{WriteAdmissionClosedNeverOpenedOptionValue}'",
                }
            );
        }

        if (string.Equals(verbName, CdcReplaceSourceVerbName, StringComparison.Ordinal))
        {
            command.Options.Add(
                CreatePositiveNumberOption<long>(
                    PreviousGenerationOptionName,
                    "Positive generation being replaced; it is named explicitly and never inferred"
                )
            );
        }

        if (string.Equals(verbName, CdcAdoptVerbName, StringComparison.Ordinal))
        {
            command.Options.Add(
                new Option<string?>(BindingJsonOptionName)
                {
                    Description =
                        "Path to the complete binding record to adopt, or '-' for stdin; adoption never "
                        + "infers a binding from the artifacts that happen to exist",
                }
            );
        }

        if (ExpectedCdcConfirmationOptionValue(verbName) is not null)
        {
            command.Options.Add(
                new Option<string?>(ConfirmOptionName) { Description = "Command confirmation token" }
            );
        }

        command.Validators.Add(result => ValidateCdcCommandOptions(result, verbName));
        command.SetAction(ExecuteCommandSurfaceOnly);
        return command;
    }

    private static string CdcVerbDescription(string verbName) =>
        verbName switch
        {
            CdcEnableVerbName => "Enable CDC on a target created for this provisioning",
            CdcStatusVerbName => "Report deployment-owned CDC readiness for one binding",
            CdcRestartVerbName => "Restart the binding's connector after affirmative continuity evidence",
            CdcAdoptVerbName => "Adopt an operator-supplied binding around a complete governed artifact set",
            CdcReplaceSourceVerbName => "Replace the physical source behind an enabled target",
            CdcRetireVerbName => "Retire a binding and its governed artifacts",
            _ => throw new ArgumentException($"'{verbName}' is not a cdc verb.", nameof(verbName)),
        };

    private static void AddTargetOptions(Command command)
    {
        var dataStoreIdOption = new Option<long?>(DataStoreIdOptionName)
        {
            Description = "Positive CMS data store identifier",
        };
        dataStoreIdOption.Validators.Add(result =>
        {
            long? value = result.GetValueOrDefault<long?>();
            if (value is <= 0)
            {
                result.AddError($"{DataStoreIdOptionName} must be a positive integer.");
            }
        });
        command.Options.Add(dataStoreIdOption);
        command.Options.Add(
            new Option<string?>(TenantKeyOptionName)
            {
                Description = "Target tenant key; omitted means the default tenant",
            }
        );
    }

    private static Option<string?> CreateRequestJsonOption() =>
        new(RequestJsonOptionName)
        {
            Description = "Path to a shared JSON request document, or '-' for stdin",
        };

    private static Option<string> CreatePositiveSecondsOption(
        string name,
        string description,
        string defaultValue
    )
    {
        var option = new Option<string>(name)
        {
            Description = $"{description} (default: {defaultValue})",
            DefaultValueFactory = _ => defaultValue,
        };
        option.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (!TryParsePositiveSeconds(value, out _))
            {
                result.AddError($"{name} must be a positive numeric seconds value.");
            }
        });
        return option;
    }

    private static Option<T?> CreatePositiveNumberOption<T>(string name, string description)
        where T : struct, INumber<T>
    {
        var option = new Option<T?>(name) { Description = description };
        option.Validators.Add(result =>
        {
            T? value = result.GetValueOrDefault<T?>();
            if (value is { } suppliedValue && suppliedValue <= T.Zero)
            {
                result.AddError($"{name} must be a positive integer.");
            }
        });
        return option;
    }

    private static int ExecuteCommandSurfaceOnly(ParseResult parseResult)
    {
        _ = CreateConfigurationOverrides(parseResult);
        return DocumentCacheAdminExitCodes.Success;
    }

    private static void ValidateMutatingCommandOptions(CommandResult result, string commandName)
    {
        if (GetSpecifiedOption(result, RequestJsonOptionName) is not null)
        {
            return;
        }

        string expectedConfirmation = ExpectedConfirmationOptionValue(commandName);
        ValidateRequiredExactOption(result, ConfirmOptionName, expectedConfirmation, "confirmation token");

        if (RequiresOfflineWriterAdmission(commandName))
        {
            ValidateRequiredExactOption(
                result,
                OfflineWriterAdmissionOptionName,
                OfflineWriterAdmissionClosedAndDrainedOptionValue,
                "offline writer admission acknowledgement"
            );
        }

        OptionResult? fingerprintResult = GetSpecifiedOption(
            result,
            ExpectedPhysicalSourceFingerprintOptionName
        );
        if (fingerprintResult is null)
        {
            return;
        }

        string? fingerprint = fingerprintResult.GetValueOrDefault<string?>();
        try
        {
            _ = new DocumentCachePhysicalSourceFingerprint(fingerprint ?? string.Empty);
        }
        catch (ArgumentException)
        {
            result.AddError(
                $"{ExpectedPhysicalSourceFingerprintOptionName} must use the canonical sha256 lowercase hexadecimal format."
            );
        }
    }

    private static void ValidateCdcCommandOptions(CommandResult result, string verbName)
    {
        if (ExpectedCdcConfirmationOptionValue(verbName) is { } expectedConfirmation)
        {
            ValidateRequiredExactOption(
                result,
                ConfirmOptionName,
                expectedConfirmation,
                "confirmation token"
            );
        }

        if (
            string.Equals(verbName, CdcReplaceSourceVerbName, StringComparison.Ordinal)
            && GetSpecifiedOption(result, PreviousGenerationOptionName) is null
        )
        {
            result.AddError(
                $"{PreviousGenerationOptionName} is required for '{CdcCommandName} {CdcReplaceSourceVerbName}'."
            );
        }

        if (
            string.Equals(verbName, CdcAdoptVerbName, StringComparison.Ordinal)
            && GetSpecifiedOption(result, BindingJsonOptionName) is null
        )
        {
            result.AddError(
                $"{BindingJsonOptionName} is required for '{CdcCommandName} {CdcAdoptVerbName}'."
            );
        }

        if (!RequiresCdcProvisioningEvidence(verbName))
        {
            return;
        }

        ValidateRequiredExactOption(
            result,
            DatabaseCreationModeOptionName,
            DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue,
            "database creation mode evidence"
        );
        ValidateRequiredExactOption(
            result,
            WriteAdmissionOptionName,
            WriteAdmissionClosedNeverOpenedOptionValue,
            "write admission evidence"
        );
    }

    private static void ValidateRequiredExactOption(
        CommandResult result,
        string optionName,
        string expectedValue,
        string valueDescription
    )
    {
        OptionResult? optionResult = GetSpecifiedOption(result, optionName);
        if (optionResult is null)
        {
            result.AddError($"{optionName} is required and must be '{expectedValue}'.");
            return;
        }

        if (optionResult.Errors.Any())
        {
            return;
        }

        string? suppliedValue = optionResult.GetValueOrDefault<string?>();
        if (!string.Equals(suppliedValue, expectedValue, StringComparison.Ordinal))
        {
            result.AddError($"{optionName} must be the exact {valueDescription} '{expectedValue}'.");
        }
    }

    private static OptionResult? GetSpecifiedOption(SymbolResult symbolResult, string optionName)
    {
        OptionResult? optionResult = symbolResult.GetResult(optionName) as OptionResult;
        return optionResult is { Implicit: false } ? optionResult : null;
    }

    private static OptionResult? GetSpecifiedOption(ParseResult parseResult, string optionName)
    {
        OptionResult? optionResult = parseResult.GetResult(optionName) as OptionResult;
        return optionResult is { Implicit: false } ? optionResult : null;
    }

    private static string ToAppSettingsDatastoreValue(string datastore) =>
        string.Equals(datastore, SqlServerDatastoreOptionValue, StringComparison.Ordinal)
            ? MssqlAppSettingsDatastoreValue
            : PostgresqlDatastoreOptionValue;

    private static string ToConfigurationTimeSpanValue(string secondsValue)
    {
        if (!TryParsePositiveSeconds(secondsValue, out TimeSpan timeSpan))
        {
            throw new InvalidOperationException(
                $"Validated timeout value '{secondsValue}' could not be converted."
            );
        }

        return timeSpan.ToString("c", CultureInfo.InvariantCulture);
    }
}
