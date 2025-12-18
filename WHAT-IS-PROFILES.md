# What Are Profiles in the Ed-Fi ODS/API?

This document explains the purpose, design, and implementation of **API Profiles** in the Ed-Fi ODS/API codebase located at `~/work/ods`.

Unless otherwise noted, source code references are **relative to** `~/work/ods/Ed-Fi-ODS`.

Profiles are a feature-flagged capability in the ODS/API:

- Feature key: `profiles` (`Application/EdFi.Ods.Common/Constants/ApiFeature.cs`)
- Registrations and pipeline wiring: `Application/EdFi.Ods.Features/Container/Modules/ProfilesModule.cs`

---

## 1) Purpose: what Profiles are for

In the Ed-Fi ODS/API, a **Profile** is a named, declarative way to describe an alternate “shape” (surface area) of one or more API resources for:

- **Reading** (GET): controlling which members appear in responses.
- **Writing** (POST/PUT): controlling which members are allowed/validated/mapped on input.

Profiles are used as a **data policy mechanism** in the ODS/API: they can be **assigned to API clients** so that, for some resources, the caller must use an appropriate profile-specific content type (or the server will enforce it automatically when unambiguous).

High-level goals:

- Provide **least-privilege payloads**: only the properties/collections needed for a specific integration.
- Support **multiple consumer views** of the same underlying resource.
- Enforce a consistent integration contract (e.g., “this integration can only send these fields”).
- Reduce coupling to the full canonical Ed-Fi resource representation.

Profiles are not the same as:

- **Claims/ClaimSets** (authorization to perform actions): those are separate security mechanisms.
- **Composites** (custom read-only endpoints that join multiple resources): Profiles can *constrain* composites, but composites are a different feature.

---

## 2) Key user-facing behavior

### Profile-aware media types (“profile content types”)

Profiles are activated through vendor-specific media types with this format:

```
application/vnd.ed-fi.{resource}.{profile}.{usage}+json
```

Where:

- `{resource}` is the **singular** resource name (e.g., `student`, `school`)
- `{profile}` is the profile name
- `{usage}` is either `readable` or `writable`

The ODS/API constructs these content types using:

- `Application/EdFi.Ods.Common/Utils/Profiles/ProfilesContentTypeHelper.cs`

```csharp
"application/vnd.ed-fi.{resource}.{profile}.{usage}+json"
```

Notes:

- The helper generates the canonical string using lowercase resource/profile/usage. Matching of the `{resource}`, `{profile}`, and `{usage}` facets is generally case-insensitive, but the middleware only attempts profile parsing when the header value starts with the literal lowercase prefix `application/vnd.ed-fi.`.
- The `{resource}` segment must match the resource being requested/modified (enforced during mapping contract resolution).

#### Which header to use

The request method determines which header is inspected:

- **GET** requests: profile media types are read from the **`Accept`** header.
- **POST/PUT** requests: profile media types are read from the **`Content-Type`** header.

This behavior is implemented in:

- `Application/EdFi.Ods.Features/Profiles/ProfileContentTypeContextMiddleware.cs`

#### Usage must match the HTTP method

The middleware enforces these rules:

- `readable` content types are only valid for **GET**
- `writable` content types are only valid for **POST/PUT**

If the usage doesn’t match, the middleware returns a **400** Problem Details response.

---

### Responses advertise the profile content type

For GET responses, the base controller sets the response `Content-Type` to the profile content type when a profile is active:

- `Application/EdFi.Ods.Api/Controllers/DataManagementControllerBase.cs` (`GetReadContentType()` and GET actions)

If no profile is active, it returns `application/json`.

---

### Profiles can be assigned to API clients (enforcement)

When Profiles are enabled, the API can enforce profile usage for a caller *based on their assigned profiles*:

- `Application/EdFi.Ods.Api/Filters/EnforceAssignedProfileUsageFilter.cs`

Behavior:

1. If the caller has **no assigned profiles**, the filter does nothing.
2. If the caller has assigned profiles, the filter determines which of them are **relevant for the current request**:
   - GET → profiles where the resource has a **readable** content type
   - non-GET → profiles where the resource has a **writable** content type (the filter treats all non-GET methods as writable)
3. If **exactly one** relevant profile exists and the client did not specify a profile content type header, the filter **auto-applies** it.
4. If **multiple** relevant profiles exist and the client did not specify one, the filter returns **403** with an error telling the client which profile content types are required.
5. If the client *did* specify a profile, it must be one of the assigned relevant profiles; otherwise the filter returns **403**.

This is how Profiles are used as a data policy enforcement mechanism: for resources where a profile is assigned and applicable, callers cannot bypass the profile constraints by simply using `application/json`.

---

### Profiles also constrain Composites (read-only)

Composite responses use a “profiles-applied” resource model when the caller has profiles assigned:

- `Application/EdFi.Ods.Features/Composites/Infrastructure/CompositeResourceResponseProvider.cs` (`GetResourceModel()`)
- `Application/EdFi.Ods.Common/Models/Resource/ProfilesAppliedResourceModel.cs`

This is why the repository includes a Postman collection titled “Composite Profile Test Suite”.

---

## 3) How Profiles are defined (XML)

Profiles are defined as XML documents following the schema:

- `Application/EdFi.Ods.Common/Metadata/Schemas/Ed-Fi-ODS-API-Profiles.xsd`

At a high level:

```xml
<Profiles>
  <Profile name="MyProfile">
    <Resource name="School" logicalSchema="Ed-Fi">
      <ReadContentType memberSelection="IncludeOnly|ExcludeOnly|IncludeAll">
        <!-- Members -->
      </ReadContentType>
      <WriteContentType memberSelection="IncludeOnly|ExcludeOnly|IncludeAll">
        <!-- Members -->
      </WriteContentType>
    </Resource>
  </Profile>
</Profiles>
```

### Resource selection

Each `<Profile>` contains one or more `<Resource>` elements.

`logicalSchema`:

- Optional; defaults to the Ed-Fi logical name.
- Used to map the profile resource to the correct physical schema at runtime.
- Implemented in `Application/EdFi.Ods.Common/Models/Resource/ProfileResourceModel.cs` (`CreateResourceByName`).

This enables profiles for **extension resources** (resources in non-Ed-Fi schemas), e.g.:

```xml
<Resource name="StudentArtProgramAssociation" logicalSchema="Sample">
  ...
</Resource>
```

### Read vs write surface area

Each `<Resource>` can define:

- `<ReadContentType ... />` (for GET)
- `<WriteContentType ... />` (for POST/PUT)

If a content type element is absent, that resource is simply not readable/writable for that profile.

### Member selection modes

The schema lists `IncludeOnly`, `ExcludeOnly`, `IncludeAll`, and `ExcludeAll`. In the current runtime implementation, the following are supported:

- `IncludeAll`
- `IncludeOnly`
- `ExcludeOnly`

The filtering behavior is implemented in:

- `Application/EdFi.Ods.Common/Models/Resource/ProfileResourceMembersFilterProvider.cs`

Important details:

- **Identifying properties** (primary key members) are always included.
- **Identifying references** are always included.
- Attempting to exclude identifying members is treated as a validation warning (but warnings are still emitted as validation failures, so the profile definition will be considered invalid during metadata validation/loading).
- Attempting to include/exclude non-existent members is reported via the profile validation reporter.

### What counts as a “member”?

At runtime, member inclusion/exclusion is applied to:

- Properties (`ResourceProperty`)
- References (`Reference`) – references are included/excluded at the object level (e.g., `SchoolReference`).
- Collections (`Collection`) – and you can further constrain the collection item members.
- Embedded objects (`EmbeddedObject`) – the schema uses `<Object>` for nested objects.
- Extensions (`Extension`) – allow including/excluding extension members and deeply shaping extension objects.

The core filtering mechanism uses:

- `Application/EdFi.Ods.Common/Models/Resource/FilterContext.cs`
- `Application/EdFi.Ods.Common/Models/Resource/ResourceClassBase.cs` (applies `FilterContext.MemberFilter` when building members)

XML naming note:

- In the Profiles XSD, a “member” is expressed via `<Property>`, `<Collection>`, or `<Object>` elements. References are typically expressed using `<Property name="...Reference" />` (see `Application/EdFi.Ods.Profiles.Test/Profiles.xml`), even though the runtime resource model categorizes them as `Reference` members.

### Nested member selection (collections and objects)

Collections can be constrained at multiple levels. Example:

```xml
<ReadContentType memberSelection="IncludeOnly">
  <Collection name="EducationOrganizationAddresses" memberSelection="IncludeOnly">
    <Property name="City" />
    <Property name="StateAbbreviationDescriptor" />
  </Collection>
</ReadContentType>
```

The nested filtering is implemented by:

- Creating a child `FilterContext` for the collection/object (`FilterContext.GetChildContext`)
- Applying that context to the member’s `ItemType` or `ObjectType` (`ResourceClassBase` initialization)

### Filtering collection items by value

Profiles can filter which items appear in a collection based on a property’s value, using `<Filter>`:

```xml
<Collection name="SchoolCategories" memberSelection="IncludeOnly">
  <Property name="SchoolCategoryDescriptor" />
  <Filter propertyName="SchoolCategoryDescriptor" filterMode="IncludeOnly">
    <Value>uri://ed-fi.org/SchoolCategoryDescriptor#S2</Value>
    <Value>uri://ed-fi.org/SchoolCategoryDescriptor#S4</Value>
  </Filter>
</Collection>
```

Implementation:

- Value filters are parsed into `CollectionItemValueFilter`:
  - `Application/EdFi.Ods.Common/Models/Resource/CollectionItemValueFilter.cs`
- Collections expose `ValueFilters`:
  - `Application/EdFi.Ods.Common/Models/Resource/Collection.cs`
- The filter expression is compiled into a predicate at runtime:
  - `Application/EdFi.Ods.Common/Models/ResourceItemPredicateBuilder.cs`
- Predicates are passed to generated mapping contracts by `MappingContractProvider` (see below).

### Extensions in profiles

To include or exclude extension members, use `<Extension name="MyExtensionSchema" ...>`.

Example (deep include-only):

```xml
<ReadContentType memberSelection="IncludeOnly">
  <Property name="FirstName" />
  <Extension name="Sample" memberSelection="IncludeOnly">
    <Property name="FirstPetOwnedDate" />
    <Object name="StaffPetPreference" memberSelection="IncludeOnly">
      <Property name="MinimumWeight" />
    </Object>
  </Extension>
</ReadContentType>
```

Runtime support:

- `FilterContext.GetExtensionContext()` maps the extension’s logical schema name to a proper-case schema name via the schema map provider and locates the `<Extension>` element.
- `ResourceClassBase` uses `MemberFilter.ShouldIncludeExtension(...)` to include/exclude extension classes.

---

## 4) How Profiles are loaded and validated

### Sources of profile definitions

Profile definitions are loaded from multiple sources via `IProfileDefinitionsProvider` implementations:

1. **Embedded resources in “profile assemblies”**
   - `Application/EdFi.Ods.Common/Metadata/Profiles/EmbeddedResourceProfileDefinitionsProvider.cs`
   - Streams are discovered by:
     - `Application/EdFi.Ods.Common/Metadata/StreamProviders/Profiles/AppDomainEmbeddedResourcesProfilesMetadataStreamsProvider.cs`
   - Assemblies are recognized as profile assemblies when:
     - The assembly name contains `.Profiles.`
     - It contains an embedded resource ending with `Profiles.xml`
     - `Application/EdFi.Ods.Common/Conventions/EdFiConventions.cs` (`IsProfileAssembly`)

2. **Admin database**
   - `Application/EdFi.Ods.Features/Profiles/AdminDatabaseProfileDefinitionsProvider.cs`
   - Reads `dbo.Profiles.ProfileDefinition` (XML) and validates it.
   - Also ensures the `ProfileName` in the database matches the `<Profile name="...">` in the XML definition.

The combined provider:

- `Application/EdFi.Ods.Common/Metadata/Profiles/ProfileMetadataProvider.cs`

…merges all definitions, removes duplicates (case-insensitive), and exposes:

- `GetProfileDefinitionsByName()` → `Dictionary<string, XElement>`
- `GetValidationResults()` → validation results per source/stream

### Validation

Validation is performed by:

- `Application/EdFi.Ods.Common/Metadata/Profiles/ProfileMetadataValidator.cs`

It validates:

1. **XSD validation** using `Ed-Fi-ODS-API-Profiles.xsd`
2. **Resource model validation** by attempting to construct and iterate:
   - `ProfileResourceModel` instances
   - `ProfilesAppliedResourceModel` (for readable and writable)

Profile-level validation messages are produced by:

- `Application/EdFi.Ods.Common/Models/Validation/ProfileValidationReporter.cs`
- `Application/EdFi.Ods.Common/Models/Resource/ProfileResourceMembersFilterProvider.cs` (reports invalid includes/excludes)

Implementation note: the validator returns these messages as FluentValidation failures (even when marked with warning severity). Any failures make the validation result not valid, and the corresponding provider ignores the invalid definitions (e.g., an invalid embedded `Profiles.xml` stream is skipped; an invalid Admin DB profile row is skipped).

### Caching and cache expiration

The Profiles feature uses an interceptor cache keyed by `InterceptorCacheKeys.ProfileMetadata`, configured in:

- `Application/EdFi.Ods.Features/Container/Modules/ProfilesModule.cs`

On expiration, a `ProfileMetadataCacheExpired` notification is published:

- `Application/EdFi.Ods.Common/Profiles/ProfileMetadataCacheExpired.cs`

Which triggers cache clearing and re-publishing profile names to Admin:

- `Application/EdFi.Ods.Features/Profiles/ProfileMetadataCacheExpiredNotificationHandler.cs`

Multi-tenancy note:

- When multi-tenancy is enabled, the profile metadata cache is tenant-scoped via `ContextualCachingInterceptor<TenantConfiguration>`:
  - `Application/EdFi.Ods.Features/Container/Modules/MultitenancyModule.cs`

---

## 5) Runtime architecture: how a Profile affects processing

### 5.1 Request parsing and “profile in context”

The middleware parses the request headers and sets `ProfileContentTypeContext`:

- `Application/EdFi.Ods.Common/Profiles/ProfileContentTypeContext.cs`
- `Application/EdFi.Ods.Features/Profiles/ProfileContentTypeContextMiddleware.cs`

The context stores:

- `ProfileName`
- `ResourceName` (from the media type)
- `ContentTypeUsage` (`Readable` or `Writable`)

This context is stored in a per-request context provider (`IContextProvider<ProfileContentTypeContext>`) and is later used by mapping, validation, and serialization.

### 5.2 Profile-aware ResourceModel (“ProfileResourceModel”)

When a profile is needed, the API constructs a profile-constrained resource model:

- `Application/EdFi.Ods.Common/Models/ProfileResourceModelProvider.cs`
- `Application/EdFi.Ods.Common/Models/Resource/ProfileResourceModel.cs`

Each profile resource has two variants:

- Readable `Resource` (for GET)
- Writable `Resource` (for POST/PUT)

Exposed as:

- `Application/EdFi.Ods.Common/Models/Resource/ProfileResourceContentTypes.cs`

Each variant is a normal `Resource` built with a `FilterContext`, which alters member lists by applying `ProfileResourceMembersFilterProvider`.

### 5.3 Mapping: generated mapping contracts constrained by profiles

Mapping between API models and domain entities is controlled by a profile-specific `IMappingContract`, created by:

- `Application/EdFi.Ods.Common/Models/MappingContractProvider.cs`

Key behaviors:

- Ensures the `{resource}` in the profile content type matches the requested resource (`DataManagementResourceContext`).
- Ensures the resource exists in the profile and supports the requested usage (readable/writable).
- Builds a `MappingContractKey` that includes tenant identifier (for multi-tenancy) and caches the contract.
- Uses reflection to instantiate a generated `{ResourceClassName}MappingContract` type, passing constructor arguments derived from which members are included/supported in the profile-constrained resource class.
- For collections with `<Filter>`, it builds item predicates via `ResourceItemPredicateBuilder` and passes them into the mapping contract (for filtering collection items).

This is where profile definitions become “executable” behavior during read/write processing.

### 5.4 Serialization: filtering response JSON

Response shaping for GET requests is driven by JSON.NET serialization:

- `Application/EdFi.Ods.Api/Serialization/ProfilesAwareContractResolver.cs`
- Configured globally in:
  - `Application/EdFi.Ods.Api/Startup/NewtonsoftJsonOptionConfigurator.cs`

When `ProfileContentTypeContext` is present and the usage is `Readable`:

- `ProfilesAwareContractResolver.GetSerializableMembers()` limits JSON members to those supported by the profile-constrained resource class.
- `ETag` and `LastModifiedDate` are explicitly allowed as “metadata” properties.

Writable profile requests do not use profile-based serialization filtering (the resolver defers to default behavior for `Writable`).

### 5.5 Validation behavior is profile-aware

The Ed-Fi ODS has a customized validation flow that uses mapping contracts to decide which members should be validated.

Key implementation points:

- `Application/EdFi.Ods.Api/Validation/DataAnnotationsResourceValidator.cs` injects `IMappingContractProvider` into the validation context.
- `Application/EdFi.Ods.Common/Validation/Validator.cs` consults `IMappingContract.IsMemberSupported(memberName)` and skips validation for members not supported by the profile.

Some validation attributes also explicitly check for profiles. Example:

- `Application/EdFi.Ods.Common/Attributes/RequiredReferenceAttribute.cs`
  - For non-identifying references, if the reference is not present in the profile’s writable representation, the “required” check is skipped.

### 5.6 Creation safety: profiles can prevent creating resources

When a writable profile is active, the API checks whether the profile’s writable representation includes all required members needed to create the resource (including required children).

Implemented as a decorator:

- `Application/EdFi.Ods.Features/Profiles/ProfileBasedCreateEntityDecorator.cs`

Which calls:

- `ProfileResourceContentTypes.CanCreateResourceClass(...)`
  - `Application/EdFi.Ods.Common/Models/Resource/ProfileResourceContentTypes.cs`

If required members are missing, it throws:

- `Application/EdFi.Ods.Common/Exceptions/DataPolicyException.cs` (400 “Data Policy Enforced”)

---

## 6) Error handling summary (common cases)

These are the most common outcomes when using profiles:

- **400 Bad Request** (“Invalid Profile Usage”)
  - Malformed profile media type
  - Unknown usage segment
  - `readable` used on POST/PUT or `writable` used on GET
  - Exceptions like `ProfileContentTypeUsageException` or `DataPolicyException`

- **403 Forbidden** (“Data Policy Failure Due to Incorrect Usage”)
  - Caller has profile assignments relevant to the resource and:
    - did not specify a profile when multiple are applicable, or
    - specified a profile not assigned to the caller for that resource
  - Implemented by `EnforceAssignedProfileUsageFilter` using `SecurityDataPolicyException`

- **405 Method Not Allowed with Profile**
  - The profile includes the resource, but not for the requested usage (read vs write)
  - Implemented by `ProfileMethodUsageException`

- **406 Not Acceptable / 415 Unsupported Media Type**
  - Profile name not supported by this host
  - Implemented by `ProfileContentTypeContextMiddleware`:
    - GET (Accept) → 406
    - POST/PUT (Content-Type) → 415

---

## 7) Where to find examples and templates

Profiles definition examples:

- `Samples/Project-Profiles-Template/Profiles.xml` (minimal template)
- `Application/EdFi.Ods.Profiles.Test/Profiles.xml` (comprehensive test cases: include/exclude, nested collections, filters, extensions)
- `Application/EdFi.Ods.Profiles.Test/InvalidProfiles.xml` (invalid examples used for validation testing)

Postman collections exercising profile behavior:

- `Postman Test Suite/Ed-Fi ODS-API Profile Test Suite.postman_collection.json`
- `Postman Test Suite/Ed-Fi ODS-API Composite Profile Test Suite.postman_collection.json`

---

## 8) How to add or deploy profiles (implementation-oriented)

### Option A: ship Profiles.xml as an embedded resource (plugin/profile assembly)

The ODS loads embedded `Profiles.xml` resources from assemblies that match `EdFiConventions.IsProfileAssembly`:

- Assembly name contains `.Profiles.`
- Contains an embedded resource whose name ends with `Profiles.xml`

Implementation details:

- Stream discovery: `AppDomainEmbeddedResourcesProfilesMetadataStreamsProvider`
- XML load/validate: `EmbeddedResourceProfileDefinitionsProvider`

### Option B: store profile definitions in the Admin database

The Admin database schema includes:

- `dbo.Profiles` (contains `ProfileName` and `ProfileDefinition`)
- `dbo.ProfileApplications` (associates profiles with applications; callers inherit profiles via their application)

Loading/validation:

- `AdminDatabaseProfileDefinitionsProvider`

Publishing names to Admin (for UI/discovery):

- `AdminProfileNamesPublisher` (and scheduled startup job `PublishAdminProfileNamesStartupCommand`)

---

## 9) Practical “gotchas” and constraints

- `ExcludeAll` is allowed by the XSD but is not implemented in `ProfileResourceMembersFilterProvider` (it throws `NotImplementedException` during model construction/validation).
- Identifying members cannot be removed from the surface area (they are auto-included for correctness).
- Profile-based filtering for “sub-shapes” primarily applies to:
  - collections and embedded objects (deep member selection)
  - extensions (deep member selection via `<Extension>`)
  - references are included/excluded as a whole (not deeply shaped)
- Collection item filtering via `<Filter>` compiles a predicate for string-valued comparisons and is intended for descriptor-like properties.
- For normal (non-composite) resources, when multiple profiles apply to the same resource for the same usage, the client must specify which profile is in use (or the API returns 403).
