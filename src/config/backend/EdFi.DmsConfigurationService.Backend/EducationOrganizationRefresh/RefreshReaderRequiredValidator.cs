// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;

/// <summary>
/// Requires a configured projection reader on an instance whose job workers are on, because only workers run the
/// education organization refresh and so call DMS. It is registered only when <c>JobSettings:WorkerEnabled</c> is
/// true, read the same way the worker itself is registered. Each missing setting is named, with the bootstrap
/// alternative, so an upgraded deployment that lacks them fails at startup with a message saying what to set.
/// </summary>
/// <remarks>
/// A set base URL is left to <see cref="DmsEducationOrganizationProjectionSettingsValidator"/>, which then already
/// requires a credential; this validator adds only what that one does not check, so no failure is reported twice.
/// </remarks>
public sealed class RefreshReaderRequiredValidator
    : IValidateOptions<DmsEducationOrganizationProjectionSettings>
{
    private const string Section = DmsEducationOrganizationProjectionSettings.SectionName;

    private const string WorkerSetting = $"{JobOptions.SectionName}:{nameof(JobOptions.WorkerEnabled)}";

    public static readonly string MissingBaseUrl =
        $"{Section}:{nameof(DmsEducationOrganizationProjectionSettings.DmsBaseUrl)} must be set when {WorkerSetting} is "
        + "true, because job workers run the education organization refresh. Configure the DMS reader, or set "
        + $"{WorkerSetting}=false on an instance that runs no jobs.";

    public static readonly string MissingCredential =
        $"{Section}:{nameof(DmsEducationOrganizationProjectionSettings.Credentials)} or at least one "
        + $"{Section}:{nameof(DmsEducationOrganizationProjectionSettings.TenantCredentials)} entry must be set when "
        + $"{WorkerSetting} is true.";

    public ValidateOptionsResult Validate(string? name, DmsEducationOrganizationProjectionSettings options)
    {
        if (!string.IsNullOrWhiteSpace(options.DmsBaseUrl))
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [MissingBaseUrl];
        if (!DmsEducationOrganizationProjectionSettingsValidator.HasCompleteCredential(options))
        {
            failures.Add(MissingCredential);
        }
        return ValidateOptionsResult.Fail(failures);
    }
}
