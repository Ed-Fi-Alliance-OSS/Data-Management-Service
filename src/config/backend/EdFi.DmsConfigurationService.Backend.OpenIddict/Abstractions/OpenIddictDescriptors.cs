// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using OpenIddict.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Abstractions
{
    /// <summary>
    /// OpenIddict-compatible application model for potential future migration
    /// </summary>
    public class ExtendedOpenIddictApplicationDescriptor : OpenIddictApplicationDescriptor
    {
        /// <summary>
        /// Gets or sets the namespace prefixes for this application
        /// </summary>
        public string? NamespacePrefixes { get; set; }

        /// <summary>
        /// Gets or sets the education organization IDs for this application
        /// </summary>
        public string? EducationOrganizationIds { get; set; }

        /// <summary>
        /// Gets or sets the protocol mappers (claims configuration) for this application
        /// </summary>
        public string? ProtocolMappers { get; set; }
    }

    /// <summary>
    /// OpenIddict-compatible scope model for potential future migration
    /// </summary>
    public class ExtendedOpenIddictScopeDescriptor : OpenIddictScopeDescriptor
    {
        // Additional scope properties can be added here if needed
    }

    /// <summary>
    /// OpenIddict-compatible authorization model for potential future migration
    /// </summary>
    public class ExtendedOpenIddictAuthorizationDescriptor : OpenIddictAuthorizationDescriptor
    {
        // Additional authorization properties can be added here if needed
    }

    /// <summary>
    /// OpenIddict-compatible token model for potential future migration
    /// </summary>
    public class ExtendedOpenIddictTokenDescriptor : OpenIddictTokenDescriptor
    {
        // Additional token properties can be added here if needed
    }
}
