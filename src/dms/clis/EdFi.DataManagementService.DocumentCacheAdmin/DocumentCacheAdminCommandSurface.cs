// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
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
    public const string TargetTenantKeyConfigurationKey = "DataManagement:DocumentCache:Targets:0:TenantKey";
    public const string TargetDataStoreIdConfigurationKey =
        "DataManagement:DocumentCache:Targets:0:DataStoreId";

    public const string DefaultCommandTimeoutSeconds = "86400";
    public const string DefaultStatusObservationTimeoutSeconds = "5";
    public const string DefaultStatusTimeoutSeconds = "30";
    public const string OfflineWriterAdmissionClosedAndDrainedOptionValue = "closedAndDrained";

    private static readonly HashSet<string> MutatingCommandNames =
    [
        ActivateNewEmptyCommandName,
        ActivateOfflineCommandName,
        DeactivateOfflineCommandName,
        RebuildOnlineCommandName,
        ScrubCommandName,
        RecoverCacheAheadCommandName,
    ];

    private static readonly IReadOnlyDictionary<
        string,
        DocumentCacheAdministrativeCommandConfirmation
    > ConfirmationByCommandName = new Dictionary<string, DocumentCacheAdministrativeCommandConfirmation>(
        StringComparer.Ordinal
    )
    {
        [ActivateNewEmptyCommandName] = DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation,
        [ActivateOfflineCommandName] = DocumentCacheAdministrativeCommandConfirmation.OfflineActivation,
        [DeactivateOfflineCommandName] = DocumentCacheAdministrativeCommandConfirmation.OfflineDeactivation,
        [RebuildOnlineCommandName] = DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild,
        [ScrubCommandName] = DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub,
        [RecoverCacheAheadCommandName] =
            DocumentCacheAdministrativeCommandConfirmation.InternalCacheAheadRecovery,
    };

    private static readonly IReadOnlyDictionary<
        string,
        DocumentCacheOfflineWriterAdmissionConfirmation
    > OfflineWriterAdmissionConfirmationByCommandName = new Dictionary<
        string,
        DocumentCacheOfflineWriterAdmissionConfirmation
    >(StringComparer.Ordinal)
    {
        [ActivateOfflineCommandName] =
            DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained,
        [DeactivateOfflineCommandName] =
            DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained,
        [RecoverCacheAheadCommandName] =
            DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained,
    };

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

        return rootCommand;
    }

    public static IReadOnlyDictionary<string, string?> CreateConfigurationOverrides(ParseResult parseResult)
    {
        return CreateConfigurationOverrides(parseResult, targetKey: null);
    }

    public static IReadOnlyDictionary<string, string?> CreateConfigurationOverrides(
        ParseResult parseResult,
        DocumentCacheTargetKey? targetKey
    )
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        Dictionary<string, string?> overrides = [];

        string? datastore = parseResult.GetValue<string?>(DatastoreOptionName);
        if (!string.IsNullOrWhiteSpace(datastore))
        {
            overrides[AppSettingsDatastoreConfigurationKey] = ToAppSettingsDatastoreValue(datastore);
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
        else if (MutatingCommandNames.Contains(commandName))
        {
            overrides[AdministrationWorkflowTimeoutConfigurationKey] = ToConfigurationTimeSpanValue(
                parseResult.GetRequiredValue<string>(CommandTimeoutSecondsOptionName)
            );
        }

        if (targetKey is not null)
        {
            overrides[TargetTenantKeyConfigurationKey] = targetKey.TenantKey;
            overrides[TargetDataStoreIdConfigurationKey] = targetKey.DataStoreId.ToString(
                CultureInfo.InvariantCulture
            );
        }

        return overrides;
    }

    public static bool ShouldInvokeWithoutRuntime(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        return parseResult.Action is HelpAction;
    }

    public static bool IsMutatingCommand(string commandName) => MutatingCommandNames.Contains(commandName);

    public static bool RequiresOfflineWriterAdmission(string commandName) =>
        commandName
            is ActivateOfflineCommandName
                or DeactivateOfflineCommandName
                or RecoverCacheAheadCommandName;

    public static bool TryGetExpectedConfirmation(
        string commandName,
        out DocumentCacheAdministrativeCommandConfirmation confirmation
    ) => ConfirmationByCommandName.TryGetValue(commandName, out confirmation);

    public static bool TryGetExpectedOfflineWriterAdmissionConfirmation(
        string commandName,
        out DocumentCacheOfflineWriterAdmissionConfirmation confirmation
    ) => OfflineWriterAdmissionConfirmationByCommandName.TryGetValue(commandName, out confirmation);

    public static string ExpectedConfirmationOptionValue(string commandName)
    {
        if (!TryGetExpectedConfirmation(commandName, out var confirmation))
        {
            throw new ArgumentException(
                $"Command '{commandName}' does not have an expected confirmation.",
                nameof(commandName)
            );
        }

        return ToLowerCamelName(confirmation);
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

    private static string ToLowerCamelName<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
