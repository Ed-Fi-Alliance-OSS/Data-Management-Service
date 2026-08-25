// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace CustomValidationConsumer;

/// <summary>
/// An implementer-owned options type for the district's external identity lookup service, bound
/// from the deployment's own configuration (for example an "ExternalIdentity" section) and
/// consumed here as <c>IOptions&lt;ExternalIdentityOptions&gt;</c>.
/// EdFi.Api.CustomValidation carries no opinion about this type's shape; it exists only so that
/// <see cref="ServiceCollectionExtensions.AddDistrictValidators"/> has something concrete to bind
/// and <see cref="StudentIdentityValidator"/> has something concrete to inject.
/// </summary>
public class ExternalIdentityOptions
{
    /// <summary>
    /// The base URL of the district's external student identity lookup service.
    /// </summary>
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>
    /// How long a single lookup call is allowed to run before it is abandoned.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}
