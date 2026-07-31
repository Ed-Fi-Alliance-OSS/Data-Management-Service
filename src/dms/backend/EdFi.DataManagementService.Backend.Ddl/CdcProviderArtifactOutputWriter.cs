// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Backend.Ddl;

internal static class CdcProviderArtifactOutputWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static CdcProviderDiagnostic? WriteManifestPayload(
        string artifactDirectoryPath,
        CdcProviderManifestPayload payload
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectoryPath);
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            Directory.CreateDirectory(artifactDirectoryPath);
            var outputPath = Path.Combine(artifactDirectoryPath, payload.FileName.Value);
            File.WriteAllText(outputPath, NormalizeLineEndings(payload.Json), Utf8NoBom);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ArtifactOutputFailure(payload.FileName, exception);
        }
    }

    private static CdcProviderDiagnostic ArtifactOutputFailure(CdcSafeName fileName, Exception exception) =>
        new(
            Code: "CDC_PROVIDER_ARTIFACT_OUTPUT_FAILED",
            Category: CdcProviderDiagnosticCategory.ValidationMismatch,
            Severity: CdcProviderDiagnosticSeverity.Error,
            PrincipalKind: CdcPrincipalKind.None,
            ArtifactKind: CdcProviderArtifactKind.None,
            SafeName: fileName,
            ExpectedValue: "writable-artifact-directory",
            ObservedValue: exception.GetType().Name,
            ProviderErrorClass: exception.GetType().Name,
            Classification: CdcProviderRetryContinuityClassification.FailClosed
        );

    private static string NormalizeLineEndings(string content) =>
        content.Contains('\r') ? content.Replace("\r\n", "\n").Replace("\r", "\n") : content;
}
