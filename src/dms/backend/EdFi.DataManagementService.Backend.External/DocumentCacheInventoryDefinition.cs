// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Stable object names for the 18-00 DocumentCache physical inventory.
/// </summary>
public static class DocumentCacheInventoryDefinition
{
    public static readonly DbSchemaName DmsSchema = new("dms");

    public static readonly DbTableName Document = new(DmsSchema, "Document");

    public static readonly DbTableName ResourceKey = new(DmsSchema, "ResourceKey");

    public static readonly DbTableName DocumentCache = new(DmsSchema, "DocumentCache");

    public static readonly DbTableName DocumentCacheState = new(DmsSchema, "DocumentCacheState");

    public static readonly DbTableName DocumentProjectionWork = new(DmsSchema, "DocumentProjectionWork");

    public static class DataStoreIdentityConstraints
    {
        public const string PrimaryKey = "PK_DataStoreIdentity";
        public const string Singleton = "CK_DataStoreIdentity_Singleton";
    }

    public static class DocumentColumns
    {
        public static readonly DbColumnName DocumentId = new("DocumentId");
        public static readonly DbColumnName DocumentUuid = new("DocumentUuid");
        public static readonly DbColumnName ResourceKeyId = new("ResourceKeyId");
        public static readonly DbColumnName CreatedByOwnershipTokenId = new("CreatedByOwnershipTokenId");
        public static readonly DbColumnName ContentVersion = new("ContentVersion");
        public static readonly DbColumnName IdentityVersion = new("IdentityVersion");
        public static readonly DbColumnName ContentLastModifiedAt = new("ContentLastModifiedAt");
        public static readonly DbColumnName IdentityLastModifiedAt = new("IdentityLastModifiedAt");
        public static readonly DbColumnName CreatedAt = new("CreatedAt");
    }

    public static class ResourceKeyColumns
    {
        public static readonly DbColumnName ResourceKeyId = new("ResourceKeyId");
        public static readonly DbColumnName ProjectName = new("ProjectName");
        public static readonly DbColumnName ResourceName = new("ResourceName");
        public static readonly DbColumnName ResourceVersion = new("ResourceVersion");
    }

    public static class DocumentCacheColumns
    {
        public static readonly DbColumnName DocumentId = new("DocumentId");
        public static readonly DbColumnName DocumentUuid = new("DocumentUuid");
        public static readonly DbColumnName ProjectName = new("ProjectName");
        public static readonly DbColumnName ResourceName = new("ResourceName");
        public static readonly DbColumnName ResourceVersion = new("ResourceVersion");
        public static readonly DbColumnName ContentVersion = new("ContentVersion");
        public static readonly DbColumnName StreamEtag = new("StreamEtag");
        public static readonly DbColumnName LastModifiedAt = new("LastModifiedAt");
        public static readonly DbColumnName DocumentJson = new("DocumentJson");
        public static readonly DbColumnName ComputedAt = new("ComputedAt");
    }

    public static class DocumentCacheStateColumns
    {
        public static readonly DbColumnName StateId = new("StateId");
        public static readonly DbColumnName ProjectionLifecycleState = new("ProjectionLifecycleState");
        public static readonly DbColumnName CacheAheadRecoveryRequired = new("CacheAheadRecoveryRequired");
    }

    public static class DocumentProjectionWorkColumns
    {
        public static readonly DbColumnName DocumentId = new("DocumentId");
        public static readonly DbColumnName RequiredContentVersion = new("RequiredContentVersion");
        public static readonly DbColumnName FirstEnqueuedAt = new("FirstEnqueuedAt");
        public static readonly DbColumnName LastEnqueuedAt = new("LastEnqueuedAt");
    }

    public static class DocumentCacheConstraints
    {
        public const string PrimaryKey = "PK_DocumentCache";
        public const string ForeignKeyToDocument = "FK_DocumentCache_Document";
        public const string ComputedAtDefault = "DF_DocumentCache_ComputedAt";
        public const string PgsqlJsonObject = "CK_DocumentCache_JsonObject";
        public const string MssqlJsonObject = "CK_DocumentCache_IsJsonObject";
    }

    public static class DocumentConstraints
    {
        public const string ForeignKeyToResourceKey = "FK_Document_ResourceKey";
    }

    public static class ResourceKeyConstraints
    {
        public const string PrimaryKey = "PK_ResourceKey";
        public const string UniqueProjectNameResourceName = "UX_ResourceKey_ProjectName_ResourceName";
    }

    public static class DocumentCacheStateConstraints
    {
        public const string PrimaryKey = "PK_DocumentCacheState";
        public const string Singleton = "CK_DocumentCacheState_Singleton";
        public const string Lifecycle = "CK_DocumentCacheState_Lifecycle";
    }

    public static class DocumentProjectionWorkConstraints
    {
        public const string PrimaryKey = "PK_DocumentProjectionWork";
        public const string ForeignKeyToDocument = "FK_DocumentProjectionWork_Document";
    }

    public static class DocumentProjectionWorkIndexes
    {
        public const string FirstEnqueuedAtDocumentId =
            "IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId";
    }

    public static class DocumentCacheTriggers
    {
        public const string ValidateDocumentUuid = "TR_DocumentCache_ValidateDocumentUuid";
        public const string PgsqlValidateDocumentUuidFunction = "TF_DocumentCache_ValidateDocumentUuid";
        public const string ValidateDocumentUuidFailureMessagePrefix =
            "dms.DocumentCache.DocumentUuid diverges from the owning dms.Document row";
        public const string PgsqlValidateDocumentUuidFailureMessage =
            ValidateDocumentUuidFailureMessagePrefix + " for DocumentId %";
        public const string MssqlValidateDocumentUuidFailureMessage =
            ValidateDocumentUuidFailureMessagePrefix + ".";
    }

    public static class DocumentEnqueueArtifacts
    {
        public const string PgsqlInsertFunction = "TF_Document_EnqueueProjectionInsert";
        public const string PgsqlUpdateFunction = "TF_Document_EnqueueProjectionUpdate";
        public const string PgsqlInsertTrigger = "TR_Document_EnqueueProjectionInsert";
        public const string PgsqlUpdateTrigger = "TR_Document_EnqueueProjectionUpdate";
        public const string MssqlTrigger = "TR_Document_EnqueueProjectionWork";
    }
}
