// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.ComponentModel;

namespace EdFi.DmsConfigurationService.DataModel.Model.Application;

/// <summary>
/// The 201 body of POST /v3/applications. Key and Secret are always written, and are null when
/// the Application is created without its default API client.
/// </summary>
public class ApplicationInsertResponse
{
    public required int Id { get; init; }

    [Description("The default API client's key, or null when createDefaultCredentials was false.")]
    public required string? Key { get; init; }

    [Description("The default API client's secret, or null when createDefaultCredentials was false.")]
    public required string? Secret { get; init; }
}
