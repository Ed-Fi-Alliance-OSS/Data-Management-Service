// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// Reads the active public keys from the <c>OpenIddictKey</c> table. Repository failures propagate (the provider
/// records them as <c>Failed(Retrieval)</c>); a row whose key material cannot be used is logged and discarded, as the
/// token manager's key loop did.
/// </summary>
public sealed class DatabaseSigningKeySource(
    IOpenIddictTokenRepository tokenRepository,
    ILogger<DatabaseSigningKeySource> logger
) : ISigningKeySource
{
    public SigningKeySource Kind => SigningKeySource.Database;

    public async Task<SigningKeySourceResult> LoadAsync(CancellationToken cancellationToken)
    {
        var records = await tokenRepository.GetActivePublicKeysAsync(cancellationToken);

        List<SigningKeyEntry> entries = [];
        int discarded = 0;
        foreach (var record in records)
        {
            try
            {
                PublicKeyFormat format = PublicKeyMaterialParser.DetectFormat(record.PublicKey, logger);
                if (format == PublicKeyFormat.Unknown)
                {
                    logger.LogWarning(
                        "Unknown key format for key ID: {KeyId}",
                        LoggingUtility.SanitizeForLog(record.KeyId)
                    );
                    discarded++;
                    continue;
                }

                entries.Add(
                    SigningKeyEntry.FromRsaPublicParameters(
                        record.KeyId,
                        PublicKeyMaterialParser.ImportPublicParameters(record.PublicKey, format)
                    )
                );
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Failed to process key with ID {KeyId}",
                    LoggingUtility.SanitizeForLog(record.KeyId)
                );
                discarded++;
            }
        }

        return new SigningKeySourceResult(entries, discarded);
    }
}
