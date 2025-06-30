// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.Frontend.SchoolYearLoader.Model
{
    public class SchoolYearType
    {
        [JsonPropertyName("schoolYear")]
        public int schoolYear { get; set; }

        [JsonPropertyName("schoolYearDescription")]
        public string schoolYearDescription { get; set; } = string.Empty;

        [JsonPropertyName("currentSchoolYear")]
        public bool currentSchoolYear { get; set; }
    }
}
