// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.Backend.Cdc;

internal static class CdcConnectorTemplateDiagnosticPropertyNames
{
    public static string UnexpectedProviderConnectionProperty(string propertyName) =>
        HashedToken("providerConnection.unexpected", propertyName);

    public static string UnexpectedKafkaSecurityProperty(string propertyName) =>
        HashedToken("kafkaSecurity.unexpected", propertyName);

    public static string ReservedConnectorProperty(string propertyName) =>
        HashedToken("connectorConfig.reserved", propertyName);

    public static string UnexpectedEffectiveConfigProperty(string propertyName) =>
        HashedToken("effectiveConfig.unexpected", propertyName);

    public static string UnexpectedSourcePartitionProperty(string propertyName) =>
        HashedToken("source.partition.unexpected", propertyName);

    private static string HashedToken(string prefix, string propertyName)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(propertyName));
        string hashPrefix = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();

        return $"{prefix}#{hashPrefix}";
    }
}
