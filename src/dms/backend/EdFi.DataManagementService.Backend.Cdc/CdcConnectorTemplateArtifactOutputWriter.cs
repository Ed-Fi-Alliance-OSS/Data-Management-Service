// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc;

internal static class CdcConnectorTemplateArtifactOutputWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static CdcConnectorTemplateDiagnostic? WriteManifestPayload(
        string artifactDirectoryPath,
        CdcConnectorTemplateArtifactPayload payload,
        CdcProvider provider,
        CdcConnectorTemplateSourcePhase sourcePhase
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectoryPath);
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            Directory.CreateDirectory(artifactDirectoryPath);
            string outputPath = Path.Combine(artifactDirectoryPath, payload.FileName.Value);
            File.WriteAllText(outputPath, NormalizeLineEndings(payload.Json), Utf8NoBom);
            return null;
        }
        catch (Exception exception) when (IsExpectedOutputFailure(exception))
        {
            return ArtifactOutputFailure(payload.FileName, provider, sourcePhase, exception);
        }
    }

    private static bool IsExpectedOutputFailure(Exception exception) =>
        exception
            is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException
                and not ArgumentNullException;

    private static CdcConnectorTemplateDiagnostic ArtifactOutputFailure(
        CdcSafeName fileName,
        CdcProvider provider,
        CdcConnectorTemplateSourcePhase sourcePhase,
        Exception exception
    ) =>
        new(
            CdcConnectorTemplateDiagnosticCodes.ArtifactOutputFailed,
            CdcConnectorTemplateDiagnosticCategory.ArtifactOutputFailure,
            CdcConnectorTemplateDiagnosticSeverity.Error,
            "artifactOutput.manifestOutputDirectoryPath",
            fileName,
            "writable-artifact-directory",
            exception.GetType().Name,
            provider,
            sourcePhase,
            CdcConnectorTemplateRedactionClassification.Safe
        );

    private static string NormalizeLineEndings(string content) =>
        content.Contains('\r') ? content.Replace("\r\n", "\n").Replace("\r", "\n") : content;
}
