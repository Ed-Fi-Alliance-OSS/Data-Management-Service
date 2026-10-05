// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;
using JobCodes = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionJobErrorCodes;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

public static class EducationOrganizationProjectionFailureExtensions
{
    private static readonly Dictionary<string, string> _messages = JobCodes.All.ToDictionary(
        pair => pair.Key,
        pair => pair.Value,
        StringComparer.Ordinal
    );

    /// <summary>
    /// The registered job error code for a permanent failure (DMS-1440 spec §5.5), or <c>null</c> for a transient one,
    /// which the job layer retries. A failure whose category is not the one its code has is a defect and throws.
    /// </summary>
    public static JobErrorCode? ToJobErrorCode(this EducationOrganizationProjectionFailure failure)
    {
        if (failure.Category != EducationOrganizationProjectionFailure.CategoryOf(failure.Code))
        {
            throw new InvalidOperationException(
                $"A projection failure with code {failure.Code} cannot have category {failure.Category}."
            );
        }

        if (failure.Category == EducationOrganizationProjectionFailureCategory.Transient)
        {
            return null;
        }

        string code = failure.Code switch
        {
            Code.NotConfigured => JobCodes.NotConfigured,
            Code.DiscoveryInvalid => JobCodes.DiscoveryInvalid,
            Code.Unsupported or Code.UnsupportedContract => JobCodes.Unsupported,
            Code.Unauthorized or Code.TokenRejected => JobCodes.Unauthorized,
            Code.Forbidden => JobCodes.Forbidden,
            Code.TargetNotFound => JobCodes.TargetNotFound,
            Code.TargetNotRoutable => JobCodes.TargetNotRoutable,
            Code.TargetSchemaIncompatible => JobCodes.TargetSchemaIncompatible,
            Code.DataInvalid => JobCodes.DataInvalid,
            Code.InvalidRequest => JobCodes.InvalidRequest,
            Code.MalformedResponse => JobCodes.MalformedResponse,
            Code.UnexpectedResponse => JobCodes.UnexpectedResponse,
            Code.LimitExceeded => JobCodes.LimitExceeded,
            _ => throw new InvalidOperationException(
                $"Permanent projection failure code {failure.Code} has no job error code."
            ),
        };
        return new JobErrorCode(code, _messages[code]);
    }
}
