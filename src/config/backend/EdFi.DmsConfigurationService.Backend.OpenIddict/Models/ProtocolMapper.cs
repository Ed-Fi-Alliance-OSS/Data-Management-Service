// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models
{
    public class ProtocolMapper
    {
        [JsonPropertyName("claim.name")]
        public string ClaimName { get; set; } = string.Empty;

        [JsonPropertyName("claim.value")]
        public string ClaimValue { get; set; } = string.Empty;

        [JsonPropertyName("jsonType.label")]
        public string JsonTypeLabel { get; set; } = string.Empty;

        [JsonPropertyName("jsonAType.label")]
        public string JsonATypeLabel { get; set; } = string.Empty;
    }
}
