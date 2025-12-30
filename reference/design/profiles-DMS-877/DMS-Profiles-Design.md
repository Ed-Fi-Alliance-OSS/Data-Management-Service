# DMS Profile Application Design Document

## DMS-877: Profile Enforcement in Data Management Service

### Overview

This document describes how the Data Management Service (DMS) applies API
Profiles during read and write operations. Profiles constrain the data surface
area of API Resources based on rules defined in XML profile definitions.

---

## 1. Problem Statement

When an API client makes a request to DMS, the system must:

1. Determine if a profile applies (via header or assignment)
2. Validate the client is authorized to use the requested profile
3. For **reads**: Filter response to only include allowed fields
4. For **writes**: Validate that the operation respects profile constraints

---

## 2. Profile Header Format

### 2.1 Header Specification

Profiles are specified via Content-Type/Accept headers using this format:

```text
application/vnd.ed-fi.[resource-name].[profile-name].[usage-type]+json
```

| Component | Description | Example |
|-----------|-------------|---------|
| `resource-name` | Lowercase resource endpoint | `student`, `school` |
| `profile-name` | Lowercase profile name | `student-exclude-birthdate` |
| `usage-type` | `readable` (GET), `writable` (POST/PUT) | `readable` |

### 2.2 Header Usage by Operation

| Operation | Header | Usage Type |
|-----------|--------|------------|
| GET | `Accept` | `readable` |
| POST | `Content-Type` | `writable` |
| PUT | `Content-Type` | `writable` |
| DELETE | N/A | N/A |

**Example Header Values:**

```text
GET:      Accept: application/vnd.ed-fi.student.exclude-birthdate.readable+json
POST/PUT: Content-Type: application/vnd.ed-fi.student.exclude-birthdate.writable+json
```

Note: Profiles do not apply to DELETE operations.

### 2.3 Implicit Profile Application

When a client's application has exactly **one** profile assigned that covers
the requested resource:

- The profile is applied automatically
- Client can use standard `application/json` content type
- Client can also explicitly specify the profile (header takes precedence)

When a client's application has **multiple** profiles covering the resource:

- Client **must** specify which profile to use via header
- Request without profile header returns an error

---

## 3. High-Level Request Flow

```text
Request ──> JWT Auth ──> Profile Resolution Middleware
                                     │
                         ┌───────────┴───────────┐
                         │ Profile in header?    │
                         └───────────┬───────────┘
                                Yes  │  No
                         ┌───────────┴───────────┐
                         │                       │
                         ▼                       ▼
                  ┌─────────────┐    ┌───────────────────────┐
                  │Parse header │    │Count assigned profiles│
                  └──────┬──────┘    └───────────┬───────────┘
                         │                       │
                         │           ┌───────────┼───────────┐
                         │           │           │           │
                         │          0            1          2+
                         │           │           │           │
                         │           │      Auto-select   Error
                         │           │       profile      403
                         │           │           │
                         └─────────┬─┴───────────┘
                                   │
                         ┌─────────┴─────────┐
                         │ Profile selected? │
                         └─────────┬─────────┘
                              Yes  │  No
                         ┌─────────┴─────────┐
                         │                   │
                         ▼                   │
                  ┌─────────────┐            │
                  │ Authorized? │            │
                  └──────┬──────┘            │
                    Yes  │  No               │
                    ┌────┴────┐              │
                    ▼         ▼              │
                 Store     Error             │
                 context    403              │
                    │                        │
                    └──────────┬─────────────┘
                               │
                               ▼
                    Continue to Read/Write Pipeline
```

---

## 4. Profile Resolution Middleware

### 4.1 Position in Pipeline

The Profile Resolution Middleware should be inserted **after** authentication
and **before** document validation:

```text
Existing Pipeline:
  1. RequestResponseLoggingMiddleware
  2. CoreExceptionLoggingMiddleware
  3. TenantValidationMiddleware
  4. JwtAuthenticationMiddleware
  5. ResolveDmsInstanceMiddleware
  6. ApiSchemaValidationMiddleware
  7. ProvideApiSchemaMiddleware
  8. ParsePathMiddleware
  9. ParseBodyMiddleware (for writes)
  ...

With Profile Support:
  1. RequestResponseLoggingMiddleware
  2. CoreExceptionLoggingMiddleware
  3. TenantValidationMiddleware
  4. JwtAuthenticationMiddleware
  5. ResolveDmsInstanceMiddleware
  6. ApiSchemaValidationMiddleware
  7. ProvideApiSchemaMiddleware
  8. ParsePathMiddleware
  9. >>> ProfileResolutionMiddleware <<<  (NEW)
  10. ParseBodyMiddleware (for writes)
  ...
```

### 4.2 ProfileResolutionMiddleware Responsibilities

1. **Parse profile header** (if present)
   - Extract resource name, profile name, usage type from header
   - Validate header format

2. **Validate usage type matches operation**
   - GET must use `readable`
   - POST/PUT must use `writable`

3. **Resolve profile definition**
   - Fetch from cache or CMS
   - Validate profile covers the requested resource

4. **Validate authorization**
   - Check if client's application has the profile assigned
   - Handle implicit profile selection

5. **Store in RequestInfo**
   - Add `ProfileContext` to `RequestInfo` for downstream use

### 4.3 ProfileContext Data Structure

```csharp
public record ProfileContext(
    string ProfileName,
    ProfileContentType ContentType,  // Read or Write
    ProfileDefinition Definition,
    bool WasExplicitlySpecified
);

public record ProfileDefinition(
    string ProfileName,
    IReadOnlyList<ResourceProfile> Resources
);

public record ResourceProfile(
    string ResourceName,
    ContentTypeDefinition? ReadContentType,
    ContentTypeDefinition? WriteContentType
);

public record ContentTypeDefinition(
    MemberSelection MemberSelection,
    IReadOnlyList<PropertyRule> Properties,
    IReadOnlyList<ObjectRule> Objects,
    IReadOnlyList<CollectionRule> Collections,
    IReadOnlyList<ExtensionRule> Extensions
);

public enum MemberSelection { IncludeOnly, ExcludeOnly, IncludeAll, ExcludeAll }

public record PropertyRule(string Name);

public record ObjectRule(
    string Name,
    MemberSelection MemberSelection,
    IReadOnlyList<PropertyRule>? Properties,
    IReadOnlyList<ObjectRule>? NestedObjects,
    IReadOnlyList<CollectionRule>? Collections
);

public record ExtensionRule(
    string Name,
    MemberSelection MemberSelection,
    IReadOnlyList<PropertyRule>? Properties,
    IReadOnlyList<ObjectRule>? Objects,
    IReadOnlyList<CollectionRule>? Collections
);

public record CollectionRule(
    string Name,
    MemberSelection MemberSelection,
    IReadOnlyList<PropertyRule>? Properties,
    IReadOnlyList<CollectionItemFilter>? ItemFilters  // For descriptor-based filtering
);

public record CollectionItemFilter(
    string PropertyName,           // e.g., "AddressTypeDescriptor"
    FilterMode FilterMode,         // IncludeOnly or ExcludeOnly
    IReadOnlyList<string> Values   // Full descriptor URIs
);

public enum FilterMode { IncludeOnly, ExcludeOnly }
```

---

## 5. Read Path (GET Operations)

### 5.1 Response Filtering Flow

```text
┌─────────────────────────────────────────────────────────────────────────────┐
│                         GET Request Processing                              │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────┐                                                        │
│  │  GetByIdHandler │                                                        │
│  │  or             │                                                        │
│  │  GetByQueryHandler                                                       │
│  └────────┬────────┘                                                        │
│           │                                                                 │
│           ▼                                                                 │
│  ┌─────────────────────────────────────────┐                                │
│  │  Fetch document(s) from repository      │                                │
│  └────────┬────────────────────────────────┘                                │
│           │                                                                 │
│           ▼                                                                 │
│  ┌─────────────────────────────────────────┐                                │
│  │  Is ProfileContext present in           │                                │
│  │  RequestInfo?                           │                                │
│  └────────┬────────────────────────────────┘                                │
│           │                    │                                            │
│          Yes                   No                                           │
│           │                    │                                            │
│           ▼                    ▼                                            │
│  ┌─────────────────┐  ┌─────────────────┐                                   │
│  │ Apply Profile   │  │ Return full     │                                   │
│  │ Filter          │  │ document        │                                   │
│  └────────┬────────┘  └────────┬────────┘                                   │
│           │                    │                                            │
│           ▼                    │                                            │
│  ┌─────────────────────────────┴──────────┐                                 │
│  │  ProfileResponseFilter.Filter()        │                                 │
│  │                                        │                                 │
│  │  For each property in document:        │                                 │
│  │    if MemberSelection == IncludeOnly:  │                                 │
│  │      keep only if in profile list      │                                 │
│  │    if MemberSelection == ExcludeOnly:  │                                 │
│  │      remove if in profile list         │                                 │
│  │                                        │                                 │
│  │  For each collection:                  │                                 │
│  │    apply collection-level rules        │                                 │
│  │    apply item-level filters            │                                 │
│  └────────┬───────────────────────────────┘                                 │
│           │                                                                 │
│           ▼                                                                 │
│  ┌─────────────────────────────────────────┐                                │
│  │  Return filtered response               │                                │
│  └─────────────────────────────────────────┘                                │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 5.2 ProfileResponseFilter Implementation Approach

The filter operates on the JSON document after retrieval:

```csharp
public static class ProfileResponseFilter
{
    public static JsonNode Filter(JsonNode document, ResourceProfile profile)
    {
        var readContent = profile.ReadContentType;
        if (readContent == null) return document;

        var result = document.DeepClone();

        // Filter top-level properties
        FilterProperties(result, readContent.Properties, readContent.MemberSelection);

        // Filter collections
        foreach (var collectionRule in readContent.Collections)
        {
            FilterCollection(result, collectionRule);
        }

        return result;
    }

    private static void FilterProperties(
        JsonNode node,
        IReadOnlyList<PropertyRule> rules,
        MemberSelection selection)
    {
        // Implementation varies by MemberSelection:
        // - IncludeOnly: Remove all properties NOT in rules list (except identity fields)
        // - ExcludeOnly: Remove all properties IN rules list
    }
}
```

### 5.3 Identity Field Protection

Certain fields are **always included** regardless of profile rules:

- Primary key fields (e.g., `id`)
- Identity fields that form the natural key (e.g., `studentUniqueId`)
- Reference fields that are part of the identity

This prevents profiles from creating unusable responses.

---

## 6. Write Path (POST/PUT Operations)

### 6.1 Write Validation Flow

```text
┌─────────────────────────────────────────────────────────────────────────────┐
│                       POST/PUT Request Processing                           │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────────────────────────────┐                                │
│  │  ParseBodyMiddleware                    │                                │
│  │  (parse JSON body)                      │                                │
│  └────────┬────────────────────────────────┘                                │
│           │                                                                 │
│           ▼                                                                 │
│  ┌─────────────────────────────────────────┐                                │
│  │  ValidateDocumentMiddleware             │                                │
│  │  (existing JSON schema validation)      │                                │
│  │  (existing overpost removal)            │                                │
│  └────────┬────────────────────────────────┘                                │
│           │                                                                 │
│           ▼                                                                 │
│  ┌─────────────────────────────────────────┐                                │
│  │  >>> ProfileWriteValidationMiddleware <<< (NEW)                          │
│  │                                        │                                 │
│  │  Is ProfileContext present?            │                                 │
│  └────────┬────────────────────────────────┘                                │
│           │                    │                                            │
│          Yes                   No                                           │
│           │                    │                                            │
│           ▼                    ▼                                            │
│  ┌─────────────────┐  ┌─────────────────┐                                   │
│  │ Validate write  │  │ Continue        │                                   │
│  │ constraints     │  │ pipeline        │                                   │
│  └────────┬────────┘  └────────┬────────┘                                   │
│           │                    │                                            │
│           ▼                    │                                            │
│  ┌─────────────────────────────┴──────────┐                                 │
│  │  For IncludeOnly profiles:             │                                 │
│  │    - Strip fields not in profile       │                                 │
│  │    (similar to overpost removal)       │                                 │
│  │                                        │                                 │
│  │  For ExcludeOnly profiles:             │                                 │
│  │    - Strip excluded fields             │                                 │
│  │                                        │                                 │
│  │  Note: Full body is accepted, profile  │                                 │
│  │  filtering happens at operation level  │                                 │
│  └────────┬───────────────────────────────┘                                 │
│           │                                                                 │
│           ▼                                                                 │
│  ┌─────────────────────────────────────────┐                                │
│  │  Continue to UpsertHandler              │                                │
│  └─────────────────────────────────────────┘                                │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 6.2 Write Behavior

#### 6.2.1 Creatability Validation (POST)

Before a resource can be created, DMS must validate that the profile does not
exclude any **required** members. A resource is **not creatable** if the
profile's WriteContentType excludes:

- Any required (non-nullable) properties
- Any required references
- Any required collections (or required members within collection items)
- Any required embedded objects (or required members within embedded objects)

If a profile makes a resource non-creatable, POST requests return a
**400 Bad Request** with a `DataPolicyException`:

```json
{
    "detail": "The data cannot be saved because a data policy has been applied to the request that prevents it.",
    "type": "urn:ed-fi:api:data-policy-enforced",
    "title": "Data Policy Enforced",
    "status": 400,
    "correlationId": "abc-123-def",
    "errors": [
        "The Profile definition for 'Student-Exclude-Required' excludes (or does not include) one or more required data elements needed to create the resource."
    ]
}
```

For child items (collections/embedded objects), if the profile excludes required
members of a child type, adding those child items also triggers this error:

```json
{
    "detail": "The data cannot be saved because a data policy has been applied to the request that prevents it.",
    "type": "urn:ed-fi:api:data-policy-enforced",
    "title": "Data Policy Enforced",
    "status": 400,
    "correlationId": "abc-123-def",
    "errors": [
        "The Profile definition for 'Student-Exclude-Required' excludes (or does not include) one or more required data elements needed to create a child item of type 'StudentAddress' in the resource."
    ]
}
```

**ODS/API Reference:** `ProfileBasedCreateEntityDecorator.cs` and
`ProfileResourceContentTypes.IsCreatable()` implement this validation.

#### 6.2.2 Optional Field Stripping

For **optional** fields that the profile excludes, DMS silently strips those
fields (similar to existing overpost removal). This ensures:

- Clients don't need to know exactly which optional fields a profile allows
- Existing integrations continue to work when profiles are applied

**Example:**

Profile `Student-Exclude-BirthDate` excludes `birthDate` from writes.
Since `birthDate` is a required field for Student, this profile would make
the resource **non-creatable** (see 6.2.1 above).

For a profile that excludes an **optional** field like `middleName`:

```json
// Client sends:
{
  "studentUniqueId": "12345",
  "firstName": "John",
  "lastSurname": "Doe",
  "birthDate": "2010-05-15",
  "middleName": "William"  // Optional field excluded by profile
}

// DMS processes as:
{
  "studentUniqueId": "12345",
  "firstName": "John",
  "lastSurname": "Doe",
  "birthDate": "2010-05-15"
  // middleName silently removed
}
```

The stripped optional fields are not persisted, and the client receives a
normal success response (201 Created or 200 OK).

#### 6.2.3 Collection Item Filter Enforcement

For collections with descriptor-based item filters (see Section 7.3), items
that don't match the filter criteria are silently stripped during writes.
For example, if a profile only allows Physical addresses, any submitted
Billing or Home addresses are removed before persistence.

---

## 7. Property and Element Filtering

### 7.1 Property Types

The `<Property>` element is used to filter all scalar-like properties,
including:

| Property Type | Example | Filtered Via |
|---------------|---------|--------------|
| Scalar | `firstName`, `birthDate` | `<Property name="..." />` |
| Descriptor | `gradeTypeDescriptor` | `<Property name="..." />` |
| Reference | `schoolReference` | `<Property name="..." />` |

**Note:** There is no dedicated `<Reference>` element. References are filtered
using the standard `<Property>` element just like scalar properties.

### 7.2 Collection-Level Rules

Collections can have their own `memberSelection`:

| Value | Behavior |
|-------|----------|
| `IncludeAll` | Include entire collection with all items |
| `ExcludeAll` | Exclude entire collection |
| `IncludeOnly` | Include collection, filter to specified properties |
| `ExcludeOnly` | Include collection, exclude specified properties |

### 7.3 Descriptor-Based Item Filtering

Collections can filter items based on descriptor property values. This allows
profiles to include or exclude specific collection items based on their
descriptor values (e.g., only include Physical addresses).

#### Filter XML Structure

The `<Filter>` element is placed inside a `<Collection>` element:

```xml
<Collection name="CollectionName" memberSelection="IncludeOnly|ExcludeOnly">
    <Property name="PropertyName" />
    <!-- ... more properties ... -->
    <Filter propertyName="DescriptorPropertyName" filterMode="IncludeOnly|ExcludeOnly">
        <Value>uri://ed-fi.org/DescriptorName#Value1</Value>
        <Value>uri://ed-fi.org/DescriptorName#Value2</Value>
    </Filter>
</Collection>
```

#### Filter Attributes

| Attribute | Required | Description |
|-----------|----------|-------------|
| `propertyName` | Yes | Descriptor property to filter on |
| `filterMode` | Yes | `IncludeOnly` (whitelist) or `ExcludeOnly` (blacklist) |
| `<Value>` | Yes (1+) | Full descriptor URI(s) to match |

#### Example 1: IncludeOnly Mode (Whitelist)

Only include addresses with `AddressTypeDescriptor` values A2 or A4:

```xml
<Profile name="Test-Profile-Resource-Child-Collection-Filtered-To-IncludeOnly-Specific-Descriptors">
    <Resource name="School">
        <ReadContentType memberSelection="IncludeOnly">
            <Collection name="EducationOrganizationAddresses" memberSelection="IncludeOnly">
                <Property name="StreetNumberName" />
                <Property name="City" />
                <Property name="StateAbbreviationDescriptor" />
                <Filter propertyName="AddressTypeDescriptor" filterMode="IncludeOnly">
                    <Value>uri://ed-fi.org/AddressTypeDescriptor#A2</Value>
                    <Value>uri://ed-fi.org/AddressTypeDescriptor#A4</Value>
                </Filter>
            </Collection>
        </ReadContentType>
    </Resource>
</Profile>
```

**Behavior:** Only address items where `AddressTypeDescriptor` equals A2 or A4
are returned. All other addresses are filtered out.

#### Example 2: ExcludeOnly Mode (Blacklist)

Exclude addresses with Billing, Home, or Mailing types (effectively keeping
only Physical):

```xml
<Profile name="Test-StudentEducationOrganizationAssociation-Exclude-All-Addrs-Except-Physical">
    <Resource name="StudentEducationOrganizationAssociation">
        <ReadContentType memberSelection="IncludeOnly">
            <Collection name="StudentEducationOrganizationAssociationAddresses" memberSelection="IncludeOnly">
                <Filter propertyName="AddressTypeDescriptor" filterMode="ExcludeOnly">
                    <Value>uri://ed-fi.org/AddressTypeDescriptor#Billing</Value>
                    <Value>uri://ed-fi.org/AddressTypeDescriptor#Home</Value>
                    <Value>uri://ed-fi.org/AddressTypeDescriptor#Mailing</Value>
                </Filter>
            </Collection>
        </ReadContentType>
    </Resource>
</Profile>
```

**Behavior:** Address items with Billing, Home, or Mailing types are excluded.
All other addresses (e.g., Physical) are returned.

#### Filter Processing Flow

```text
┌─────────────────────────────────────────────────────────────────────────────┐
│                    Collection Item Filter Processing                        │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  For each item in collection:                                               │
│                                                                             │
│  ┌─────────────────────────────────────────┐                                │
│  │  Get item's descriptor value            │                                │
│  │  (e.g., item.AddressTypeDescriptor)     │                                │
│  └────────────────┬────────────────────────┘                                │
│                   │                                                         │
│                   ▼                                                         │
│  ┌─────────────────────────────────────────┐                                │
│  │  Is filterMode IncludeOnly?             │                                │
│  └────────────────┬────────────────────────┘                                │
│                   │                                                         │
│          ┌───────┴───────┐                                                  │
│         Yes              No (ExcludeOnly)                                   │
│          │               │                                                  │
│          ▼               ▼                                                  │
│  ┌───────────────┐  ┌───────────────┐                                       │
│  │ Value in list?│  │ Value in list?│                                       │
│  └───────┬───────┘  └───────┬───────┘                                       │
│          │                  │                                               │
│     ┌────┴────┐        ┌────┴────┐                                          │
│    Yes        No      Yes        No                                         │
│     │         │        │         │                                          │
│     ▼         ▼        ▼         ▼                                          │
│  ┌──────┐ ┌──────┐  ┌──────┐ ┌──────┐                                       │
│  │INCLUDE│ │EXCLUDE│ │EXCLUDE│ │INCLUDE│                                    │
│  └──────┘ └──────┘  └──────┘ └──────┘                                       │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

#### Data Structure for Filter

```csharp
public record CollectionItemFilter(
    string PropertyName,           // e.g., "AddressTypeDescriptor"
    FilterMode FilterMode,         // IncludeOnly or ExcludeOnly
    IReadOnlyList<string> Values   // Full descriptor URIs
);

public enum FilterMode { IncludeOnly, ExcludeOnly }
```

#### Implementation Considerations

1. **Descriptor URI matching**: Values are full URIs
   (e.g., `uri://ed-fi.org/AddressTypeDescriptor#Physical`).
   Matching should be case-insensitive.

2. **Multiple filters**: A collection can have multiple `<Filter>` elements on
   different properties. Items must satisfy ALL filters (AND logic).

3. **Empty result**: If all items are filtered out, return an empty array
   `[]`, not `null`.

4. **Missing descriptor**: If an item doesn't have the filtered descriptor
   property, behavior depends on filterMode:
   - `IncludeOnly`: Exclude the item (doesn't match any allowed value)
   - `ExcludeOnly`: Include the item (doesn't match any excluded value)

5. **Write operations**: For POST/PUT, collection items that don't match the
   filter criteria are silently stripped. For example, if a profile only allows
   Physical addresses, any submitted Billing or Home addresses are removed
   before persistence.

### 7.4 Nested Object Filtering

Profiles support filtering properties within nested objects using the
`<Object>` element. Objects can be nested recursively to any depth.

#### Object XML Structure

```xml
<ReadContentType memberSelection="IncludeOnly|ExcludeOnly|IncludeAll">
    <Object name="NestedObjectName" memberSelection="IncludeOnly|ExcludeOnly|IncludeAll|ExcludeAll">
        <Property name="PropertyName" />
        <Object name="DeeplyNestedObject" memberSelection="...">
            <!-- Can nest further -->
        </Object>
        <Collection name="NestedCollection" memberSelection="...">
            <!-- Collections within objects -->
        </Collection>
    </Object>
</ReadContentType>
```

#### Member Selection Modes for Objects

| Mode | Behavior |
|------|----------|
| `IncludeOnly` | Only listed properties/objects/collections included |
| `ExcludeOnly` | Listed items excluded, all others included |
| `IncludeAll` | All properties included (can filter child elements) |
| `ExcludeAll` | Entire object excluded from response |

#### Example 1: Exclude Specific Properties from Nested Object

Exclude the `Title` property from the `AssessmentContentStandard` nested object:

```xml
<Profile name="Assessment-Writable-Includes-Non-Creatable-Embedded-Object">
    <Resource name="Assessment">
        <WriteContentType memberSelection="IncludeAll">
            <Object name="AssessmentContentStandard" memberSelection="ExcludeOnly">
                <Property name="Title" />
            </Object>
        </WriteContentType>
    </Resource>
</Profile>
```

**Behavior:** All properties of `AssessmentContentStandard` are included
except `Title`.

#### Example 2: Include Only Specific Properties from Nested Object

Include only `MinimumWeight` from a deeply nested object:

```xml
<Profile name="Sample-Staff-Extension-Include-Only-Deeply">
    <Resource name="Staff">
        <ReadContentType memberSelection="IncludeOnly">
            <Extension name="Sample" memberSelection="IncludeOnly">
                <Object name="StaffPetPreference" memberSelection="IncludeOnly">
                    <Property name="MinimumWeight" />
                </Object>
            </Extension>
        </ReadContentType>
    </Resource>
</Profile>
```

**Behavior:** Only `MinimumWeight` is returned from `StaffPetPreference`.
Other properties like `MaximumWeight` are excluded.

#### Data Structure for Object Rules

```csharp
public record ObjectRule(
    string Name,
    MemberSelection MemberSelection,
    IReadOnlyList<PropertyRule>? Properties,
    IReadOnlyList<ObjectRule>? NestedObjects,      // Recursive nesting
    IReadOnlyList<CollectionRule>? Collections
);
```

### 7.5 Extension Filtering

Ed-Fi extensions are filtered using the `<Extension>` element, which works
similarly to `<Object>` but targets extension namespaces.

#### Extension XML Structure

```xml
<ReadContentType memberSelection="IncludeOnly">
    <Extension name="ExtensionNamespace" memberSelection="IncludeOnly|ExcludeOnly|IncludeAll|ExcludeAll">
        <Property name="ExtensionProperty" />
        <Object name="ExtensionObject" memberSelection="...">
            <!-- Nested object within extension -->
        </Object>
        <Collection name="ExtensionCollection" memberSelection="...">
            <!-- Collection within extension -->
        </Collection>
    </Extension>
</ReadContentType>
```

#### Example: Filter Extension Properties

```xml
<Profile name="Student-Extension-Filtered">
    <Resource name="Student">
        <ReadContentType memberSelection="IncludeAll">
            <Extension name="Sample" memberSelection="IncludeOnly">
                <Property name="PetName" />
                <!-- Other Sample extension properties excluded -->
            </Extension>
        </ReadContentType>
    </Resource>
</Profile>
```

**Behavior:** All core Student properties are included, but only `PetName` from
the Sample extension is returned.

#### Data Structure for Extension Rules

```csharp
public record ExtensionRule(
    string Name,                                    // Extension namespace (e.g., "Sample")
    MemberSelection MemberSelection,
    IReadOnlyList<PropertyRule>? Properties,
    IReadOnlyList<ObjectRule>? Objects,
    IReadOnlyList<CollectionRule>? Collections
);
```

### 7.6 Complete Data Structures

The complete data structure including all element types:

```csharp
public record ContentTypeDefinition(
    MemberSelection MemberSelection,
    IReadOnlyList<PropertyRule> Properties,
    IReadOnlyList<ObjectRule> Objects,
    IReadOnlyList<CollectionRule> Collections,
    IReadOnlyList<ExtensionRule> Extensions
);
```

---

## 8. Error Handling

### 8.1 Error Response Matrix

The following error responses match the ODS/API implementation:

| Scenario | HTTP | Error Type |
|----------|------|------------|
| Invalid/malformed profile header format | 400 | `profile:invalid-profile-usage` |
| Invalid usage type in header (not "readable"/"writable") | 400 | `profile:invalid-profile-usage` |
| Wrong usage type for HTTP method (writable with GET) | 400 | `profile:invalid-profile-usage` |
| Wrong usage type for HTTP method (readable with POST/PUT) | 400 | `profile:invalid-profile-usage` |
| Resource in header doesn't match requested resource | 400 | `profile:invalid-profile-usage` |
| Profile doesn't cover the requested resource | 400 | `profile:invalid-profile-usage` |
| Profile doesn't exist (GET) | 406 | `profile:invalid-profile-usage` |
| Profile doesn't exist (POST/PUT) | 415 | `profile:invalid-profile-usage` |
| Profile is misconfigured/invalid | 406 | `profile:invalid-profile-usage` |
| Resource not available for usage type in profile | 405 | `profile:method-usage` |
| Multiple profiles assigned, none specified | 403 | `security:data-policy:incorrect-usage` |
| Client not authorized for specified profile | 403 | `security:data-policy:incorrect-usage` |
| Profile excludes required members (non-creatable) | 400 | `data-policy-enforced` |
| Profile excludes required child item members | 400 | `data-policy-enforced` |

**Note:** All error types use the `urn:ed-fi:api:` prefix.

### 8.2 Error Response Format

All profile-related errors return a **Problem Details** JSON structure
following RFC 7807 format:

```json
{
    "detail": "Human-readable explanation of the error",
    "type": "urn:ed-fi:api:{error-type}",
    "title": "Short summary of the problem",
    "status": 400,
    "correlationId": "unique-request-id",
    "errors": [
        "Specific error message(s)"
    ]
}
```

### 8.3 Example Error Responses

#### Invalid Profile Header Format (400)

```json
{
    "detail": "The request construction was invalid with respect to usage of a data policy.",
    "type": "urn:ed-fi:api:profile:invalid-profile-usage",
    "title": "Invalid Profile Usage",
    "status": 400,
    "correlationId": "abc-123-def",
    "errors": [
        "The format of the profile-based 'Accept' header was invalid."
    ]
}
```

#### Wrong Usage Type for HTTP Method (400)

For writable content-type with GET:

```json
{
    "detail": "The request construction was invalid with respect to usage of a data policy.",
    "type": "urn:ed-fi:api:profile:invalid-profile-usage",
    "title": "Invalid Profile Usage",
    "status": 400,
    "correlationId": "abc-123-def",
    "errors": [
        "A profile-based content type that is writable cannot be used with GET requests."
    ]
}
```

For readable content-type with POST/PUT:

```json
{
    "detail": "The request construction was invalid with respect to usage of a data policy.",
    "type": "urn:ed-fi:api:profile:invalid-profile-usage",
    "title": "Invalid Profile Usage",
    "status": 400,
    "correlationId": "abc-123-def",
    "errors": [
        "A profile-based content type that is readable cannot be used with POST requests."
    ]
}
```

#### Profile Doesn't Exist (406 for GET, 415 for POST/PUT)

```json
{
    "detail": "The request construction was invalid with respect to usage of a data policy.",
    "type": "urn:ed-fi:api:profile:invalid-profile-usage",
    "title": "Invalid Profile Usage",
    "status": 406,
    "correlationId": "abc-123-def",
    "errors": [
        "The profile specified by the content type in the 'Accept' header is not supported by this host."
    ]
}
```

#### Profile Doesn't Cover Resource (400)

```json
{
    "detail": "The request construction was invalid with respect to usage of a data policy. The resource is not contained by the profile used by (or applied to) the request.",
    "type": "urn:ed-fi:api:profile:invalid-profile-usage",
    "title": "Invalid Profile Usage",
    "status": 400,
    "correlationId": "abc-123-def",
    "errors": [
        "Resource 'Student' is not accessible through the 'Test-Profile' profile specified by the content type."
    ]
}
```

#### Resource Not Available for Usage Type (405)

When a profile covers a resource but not for the requested usage type
(e.g., profile defines only ReadContentType but client tries to POST):

```json
{
    "detail": "The request construction was invalid with respect to usage of a data policy. An attempt was made to access a resource that is not writable using the profile.",
    "type": "urn:ed-fi:api:profile:method-usage",
    "title": "Method Not Allowed",
    "status": 405,
    "correlationId": "abc-123-def",
    "errors": [
        "Resource class 'Student' is not writable using API profile 'Test-Profile'."
    ]
}
```

#### Multiple Profiles Assigned, None Specified (403)

```json
{
    "detail": "Access to the resource could not be authorized. The request was not constructed correctly for the data policy applied to this data for the caller.",
    "type": "urn:ed-fi:api:security:data-policy:incorrect-usage",
    "title": "Forbidden",
    "status": 403,
    "correlationId": "abc-123-def",
    "errors": [
        "Based on profile assignments, one of the following profile-specific content types is required when requesting this resource: 'application/vnd.ed-fi.student.profile-a.readable+json', 'application/vnd.ed-fi.student.profile-b.readable+json'"
    ]
}
```

#### Client Not Authorized for Specified Profile (403)

Same response format as above - the error message lists the profiles that
ARE available to the client.

#### Resource Name Mismatch (400)

When the resource specified in the profile header doesn't match the endpoint:

```json
{
    "detail": "The request construction was invalid with respect to usage of a data policy.",
    "type": "urn:ed-fi:api:profile:invalid-profile-usage",
    "title": "Invalid Profile Usage",
    "status": 400,
    "correlationId": "abc-123-def",
    "errors": [
        "The resource specified by the profile-based content type ('School') does not match the requested resource ('Student')."
    ]
}
```

#### Profile Makes Resource Non-Creatable (400)

When a profile excludes required members, making the resource non-creatable:

```json
{
    "detail": "The data cannot be saved because a data policy has been applied to the request that prevents it.",
    "type": "urn:ed-fi:api:data-policy-enforced",
    "title": "Data Policy Enforced",
    "status": 400,
    "correlationId": "abc-123-def",
    "errors": [
        "The Profile definition for 'Student-Exclude-BirthDate' excludes (or does not include) one or more required data elements needed to create the resource."
    ]
}
```

#### Profile Makes Child Item Non-Creatable (400)

When a profile excludes required members of a child item type:

```json
{
    "detail": "The data cannot be saved because a data policy has been applied to the request that prevents it.",
    "type": "urn:ed-fi:api:data-policy-enforced",
    "title": "Data Policy Enforced",
    "status": 400,
    "correlationId": "abc-123-def",
    "errors": [
        "The Profile definition for 'Student-Exclude-AddressType' excludes (or does not include) one or more required data elements needed to create a child item of type 'StudentEducationOrganizationAssociationAddress' in the resource."
    ]
}
```

---

## 9. Caching Strategy

### 9.1 Cache Architecture

DMS caches all profiles for an application in a single cache entry, keyed by
`ApplicationId`.

```text
┌─────────────────────────────────────────────────────────────────────────────┐
│                          Profile Caching Architecture                       │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │  In-Memory Application Profiles Cache                               │    │
│  │  ┌─────────────────────────────────────────────────────────────┐    │    │
│  │  │  Key: ApplicationId                                         │    │    │
│  │  │  Value: Dictionary<ProfileName, ParsedProfileDefinition>    │    │    │
│  │  │  TTL: Configurable (default 30 minutes)                     │    │    │
│  │  └─────────────────────────────────────────────────────────────┘    │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                     │                                       │
│                                     │ Cache miss                            │
│                                     ▼                                       │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │  Configuration Service (CMS)                                        │    │
│  │  ┌─────────────────────────────────────────────────────────────┐    │    │
│  │  │  GET /v2/applications/{id}/profiles                         │    │    │
│  │  │  Returns: Array of all profiles for the application         │    │    │
│  │  └─────────────────────────────────────────────────────────────┘    │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 9.2 Cache Flow

```text
┌─────────────┐     ┌─────────────────────────────────────────────────────────┐
│   Request   │     │                    DMS Profile Service                  │
│   with      │────>│                                                         │
│   Profile   │     │  1. Extract ApplicationId from authenticated client     │
└─────────────┘     │  2. Check cache for ApplicationId                       │
                    │     │                                                   │
                    │     ├── Cache hit: Lookup profile by name from cache    │
                    │     │                                                   │
                    │     └── Cache miss:                                     │
                    │         a. Call CMS: GET /v2/applications/{id}/profiles │
                    │         b. Parse all profile XMLs                       │
                    │         c. Store in cache as Dictionary<Name, Parsed>   │
                    │         d. Lookup requested profile by name             │
                    │                                                         │
                    │  3. Return ProfileContext for pipeline                  │
                    └─────────────────────────────────────────────────────────┘
```

### 9.3 Cache Configuration

```json
{
  "ProfileCache": {
    "ApplicationProfilesTtlSeconds": 1800,
    "MaxApplicationsCached": 1000
  }
}
```

**Note:** Default TTL of 30 minutes (1800 seconds) matches ODS/API behavior.

### 9.4 Cache Invalidation

| Trigger | Action |
|---------|--------|
| Profile updated in CMS | TTL expiration (eventual consistency) |
| Application profiles changed | TTL expiration (eventual consistency) |
| Manual cache clear | Admin endpoint or application restart |

**Note:** For immediate invalidation, consider implementing a webhook or
polling mechanism in a future iteration.

---

## 10. Implementation Components

### 10.1 New Files

| Location | File | Description |
|----------|------|-------------|
| Core | `Profile/ProfileContext.cs` | Context data structures |
| Core | `Profile/ProfileDefinitionParser.cs` | XML parser |
| Core | `Profile/ProfileResponseFilter.cs` | Response filtering logic |
| Core | `Profile/ProfileHeaderParser.cs` | Header parsing/validation |
| Core | `Profile/IProfileService.cs` | Profile resolution interface |
| Core | `Profile/ProfileService.cs` | Resolution with caching |
| Core | `Middleware/ProfileResolutionMiddleware.cs` | Pipeline middleware |
| Core | `Middleware/ProfileWriteValidationMiddleware.cs` | Write validation |
| Core.External | `Interface/IProfileCacheService.cs` | Cache abstraction |
| Frontend | `Infrastructure/ProfileCacheService.cs` | In-memory cache impl |

### 10.2 Modified Files

| Location | File | Change |
|----------|------|--------|
| Core | `Pipeline/RequestInfo.cs` | Add `ProfileContext?` property |
| Core | `ApiService.cs` | Add profile middleware |
| Core | `Handler/GetByIdHandler.cs` | Apply profile filtering |
| Core | `Handler/GetByQueryHandler.cs` | Apply profile filtering |
| Frontend | `appsettings.json` | Add cache configuration |

---

## 11. Sequence Diagrams

### 11.1 GET Request with Explicit Profile

```text
┌──────┐          ┌─────┐         ┌─────────────┐        ┌─────┐        ┌────────────┐
│Client│          │ DMS │         │ProfileService│        │Cache│        │    CMS     │
└──┬───┘          └──┬──┘         └──────┬──────┘        └──┬──┘        └─────┬──────┘
   │                 │                   │                  │                 │
   │ GET /students/123                   │                  │                 │
   │ Accept: application/vnd.ed-fi.student.exclude-birthdate.readable+json   │
   │────────────────>│                   │                  │                 │
   │                 │                   │                  │                 │
   │                 │ ParseHeader()     │                  │                 │
   │                 │──────────────────>│                  │                 │
   │                 │                   │                  │                 │
   │                 │                   │ GetAppProfiles() │                 │
   │                 │                   │─────────────────>│                 │
   │                 │                   │                  │                 │
   │                 │                   │   Cache miss     │                 │
   │                 │                   │<─────────────────│                 │
   │                 │                   │                  │                 │
   │                 │                   │ GET /v2/applications/{id}/profiles│
   │                 │                   │────────────────────────────────────>
   │                 │                   │                  │                 │
   │                 │                   │                  │  [All Profiles] │
   │                 │                   │<────────────────────────────────────
   │                 │                   │                  │                 │
   │                 │                   │ Parse & cache all│                 │
   │                 │                   │─────────────────>│                 │
   │                 │                   │                  │                 │
   │                 │                   │ Lookup by name   │                 │
   │                 │                   │─────────────────>│                 │
   │                 │                   │                  │                 │
   │                 │  ProfileContext   │                  │                 │
   │                 │<──────────────────│                  │                 │
   │                 │                   │                  │                 │
   │                 │ Fetch document from repository       │                 │
   │                 │─────────────────────────────────────────────────────>  │
   │                 │                   │                  │                 │
   │                 │ Apply ProfileResponseFilter          │                 │
   │                 │───────┐           │                  │                 │
   │                 │       │           │                  │                 │
   │                 │<──────┘           │                  │                 │
   │                 │                   │                  │                 │
   │  Filtered JSON  │                   │                  │                 │
   │<────────────────│                   │                  │                 │
   │                 │                   │                  │                 │
```

### 11.2 POST Request with Profile Validation

```text
┌──────┐          ┌─────┐         ┌─────────────┐        ┌──────────┐
│Client│          │ DMS │         │ProfileService│        │Repository│
└──┬───┘          └──┬──┘         └──────┬──────┘        └────┬─────┘
   │                 │                   │                    │
   │ POST /students  │                   │                    │
   │ Content-Type: application/vnd.ed-fi.student.exclude-birthdate.writable+json
   │ Body: { ... }   │                   │                    │
   │────────────────>│                   │                    │
   │                 │                   │                    │
   │                 │ ParseHeader()     │                    │
   │                 │──────────────────>│                    │
   │                 │                   │                    │
   │                 │  ProfileContext   │                    │
   │                 │<──────────────────│                    │
   │                 │                   │                    │
   │                 │ ValidateAuthorization()                │
   │                 │──────────────────>│                    │
   │                 │      OK           │                    │
   │                 │<──────────────────│                    │
   │                 │                   │                    │
   │                 │ JSON Schema Validation                 │
   │                 │ (existing middleware)                  │
   │                 │───────┐           │                    │
   │                 │       │           │                    │
   │                 │<──────┘           │                    │
   │                 │                   │                    │
   │                 │ Profile Write Validation               │
   │                 │ (strip excluded fields)                │
   │                 │───────┐           │                    │
   │                 │       │           │                    │
   │                 │<──────┘           │                    │
   │                 │                   │                    │
   │                 │ UpsertDocument()  │                    │
   │                 │───────────────────────────────────────>│
   │                 │                   │                    │
   │                 │      Success      │                    │
   │                 │<───────────────────────────────────────│
   │                 │                   │                    │
   │  201 Created    │                   │                    │
   │<────────────────│                   │                    │
   │                 │                   │                    │
```
