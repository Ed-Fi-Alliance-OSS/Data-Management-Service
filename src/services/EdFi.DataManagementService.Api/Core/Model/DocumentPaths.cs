// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.ApiSchema.Model;

namespace EdFi.DataManagementService.Api.Core.Model;

/// <summary>
/// DocumentPaths provides JsonPaths to values corresponding to reference
/// and scalar MetaEd properties in a resource document
/// </summary>
public abstract record DocumentPaths(
    /// <summary>
    /// A mapping of unique DocumentObjectKeys to JsonPaths. This is used as a building block for document identities
    /// and document references, where the JsonPaths can later be turned into the values in a document, and the keys
    /// indicate what the value represents.
    ///
    /// As an example, these are the JsonPaths for CourseOffering on Section, a reference with four fields:
    ///
    /// {
    ///   localCourseCode: '$.courseOfferingReference.localCourseCode',
    ///   schoolId: '$.courseOfferingReference.schoolId',
    ///   schoolYear: '$.courseOfferingReference.schoolYear',
    ///   sessionName: '$.courseOfferingReference.sessionName',
    /// }
    /// </summary>
    Dictionary<string, JsonPath> Paths,
    /// <summary>
    /// An ordering of the paths by DocumentObjectKey, used to ensure consistent ordering downstream.
    /// </summary>
    DocumentObjectKey[] PathOrder,
    /// <summary>
    /// Discriminator between reference and scalar path types
    /// </summary>
    bool IsReference
);

/// <summary>
/// JsonPath information for a reference MetaEd property
/// </summary>
public record ReferencePaths(
    Dictionary<string, JsonPath> Paths,
    DocumentObjectKey[] PathOrder,
    bool IsReference,
    /// <summary>
    /// The project name the API document resource is defined in e.g. "EdFi" for a data standard entity
    /// </summary>
    MetaEdProjectName ProjectName,
    /// <summary>
    /// The name of the resource. Typically, this is the same as the corresponding MetaEd entity name. However,
    /// there are exceptions, for example descriptors have a "Descriptor" suffix on their resource name.
    /// </summary>
    MetaEdResourceName ResourceName,
    /// <summary>
    /// Whether this resource is a descriptor. Descriptors are treated differently from other documents
    /// </summary>
    bool IsDescriptor
) : DocumentPaths(Paths, PathOrder, IsReference);

/// <summary>
/// A JsonPath for a scalar MetaEd property
/// </summary>
public record ScalarPaths(
    Dictionary<string, JsonPath> Paths,
    DocumentObjectKey[] PathOrder,
    bool IsReference
) : DocumentPaths(Paths, PathOrder, IsReference);
