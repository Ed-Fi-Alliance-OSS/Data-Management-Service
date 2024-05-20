// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// DocumentPaths provides JsonPaths to values corresponding to reference
/// and scalar MetaEd properties in a resource document
/// </summary>
internal abstract record DocumentPaths(
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
    Dictionary<string, string> paths,
    /// <summary>
    /// An ordering of the paths by DocumentObjectKey, used to ensure consistent ordering downstream.
    /// </summary>
    string[] pathOrder,
    /// <summary>
    /// Discriminator between reference and scalar path types
    /// </summary>
    bool isReference
);

/// <summary>
/// JsonPath information for a reference MetaEd property
/// </summary>
internal record ReferencePaths(
    Dictionary<string, string> paths,
    string[] pathOrder,
    bool isReference,
    /// <summary>
    /// The project name the API document resource is defined in e.g. "EdFi" for a data standard entity
    /// </summary>
    string projectName,
    /// <summary>
    /// The name of the resource. Typically, this is the same as the corresponding MetaEd entity name. However,
    /// there are exceptions, for example descriptors have a "Descriptor" suffix on their resource name.
    /// </summary>
    string resourceName,
    /// <summary>
    /// Whether this resource is a descriptor. Descriptors are treated differently from other documents
    /// </summary>
    bool isDescriptor
) : DocumentPaths(paths, pathOrder, isReference);

/// <summary>
/// A JsonPath for a scalar MetaEd property
/// </summary>
internal record ScalarPaths(
    Dictionary<string, string> paths,
    string[] pathOrder,
    bool isReference
) : DocumentPaths(paths, pathOrder, isReference);
