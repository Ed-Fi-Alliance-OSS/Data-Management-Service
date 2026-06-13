// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.ApiSchemaDownloader.Services;

internal static class PackageIdPathSegmentValidator
{
    public static string Validate(string packageId)
    {
        if (packageId is null)
        {
            throw InvalidPackageId("value is required", nameof(packageId));
        }

        if (string.IsNullOrWhiteSpace(packageId))
        {
            throw InvalidPackageId("value must not be empty or whitespace", nameof(packageId));
        }

        if (packageId.Any(char.IsWhiteSpace))
        {
            throw InvalidPackageId("value must not contain whitespace", nameof(packageId));
        }

        if (packageId is "." or "..")
        {
            throw InvalidPackageId("value must be a package id, not a directory segment", nameof(packageId));
        }

        if (
            Path.IsPathRooted(packageId)
            || packageId.Contains('/')
            || packageId.Contains('\\')
            || packageId.Contains(Path.VolumeSeparatorChar)
        )
        {
            throw InvalidPackageId("value must be a single relative path segment", nameof(packageId));
        }

        if (packageId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw InvalidPackageId("value contains invalid file-name characters", nameof(packageId));
        }

        return packageId;
    }

    private static ArgumentException InvalidPackageId(string reason, string paramName) =>
        new($"packageId is invalid: {reason}.", paramName);
}
