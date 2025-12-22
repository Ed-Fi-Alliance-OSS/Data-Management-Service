// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.Information;

public class ApiInformation
{
    public ApiInformation(string version, string applicationName, string informationalVersion, ApiUrls urls)
    {
        Version = version;
        ApplicationName = applicationName;
        InformationalVersion = informationalVersion;
        Urls = urls;
    }

    public string Version { get; }

    public string ApplicationName { get; }

    public string InformationalVersion { get; }

    public ApiUrls Urls { get; }
}

public class ApiUrls
{
    public ApiUrls(string openApiMetadata)
    {
        OpenApiMetadata = openApiMetadata;
    }

    public string OpenApiMetadata { get; }
}
