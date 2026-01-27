# DMS Profile Application Design Document

## DMS-877: Profile Enforcement in Data Management Service

### Overview

This document describes how the Data Management Service (DMS) applies API
Profiles during read and write operations. Profiles constrain the data surface
area of API Resources based on rules defined in XML profile definitions.

### Scope and Limitations

**Version 1 Limitation:** This implementation does not support multiple data
standards in multi-instance deployments. All instances within a deployment
must use the same data standard version.

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
| `resource-name` | Singular resource name (not the plural endpoint) | `student`, `school` |
| `profile-name` | Profile name (case-insensitive) | `student-exclude-birthdate` |
| `usage-type` | `readable` (GET), `writable` (POST/PUT) | `readable` |

**Important:** Resource name matching is **case-insensitive**. The resource name
in the header (e.g., `student`) is compared against the singular resource model
name (e.g., `Student`), not the plural endpoint path (e.g., `/students`).

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

When a client's application has **no** profiles assigned:

- No implicit profile selection or assignment enforcement is applied
- Requests without a profile header proceed with the full resource
- If a profile header is provided, it is honored after normal header validation

When a client's application has **multiple** profiles covering the resource:

- Client **must** specify which profile to use via header
- Request without profile header returns an error
  
**Note:** If the application has one or more profiles but none apply to the
requested resource/verb, assignment enforcement is skipped; the request proceeds
without forcing or blocking a profile choice.

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
   - Enforcement: compare chosen (or implicit) profile to the caller's allowed
  profiles for the target resource/verb; auto-apply when exactly one applicable
  profile exists; block with 403 if the chosen profile is not assigned or
  selection is ambiguous
   - If the application has no assigned profiles, skip assignment enforcement
   - If the application has assigned profiles but none apply to the requested
  resource/verb, skip assignment enforcement (do not block on an unassigned
  header in this edge case)

5. **Store in RequestInfo**
   - Add `ProfileContext` to `RequestInfo` for downstream use
   - Header parsing is evaluated per-request and is not cached

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
    string? LogicalSchema,                    // Optional schema for extension resources
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

// Note: ExcludeAll is defined in the XSD but throws NotImplementedException in ODS/API.
// DMS should NOT support ExcludeAll to maintain compatibility.
public enum MemberSelection { IncludeOnly, ExcludeOnly, IncludeAll }

public record PropertyRule(string Name);

public record ObjectRule(
    string Name,
    MemberSelection MemberSelection,
    string? LogicalSchema,                        // Optional schema for extension objects
    IReadOnlyList<PropertyRule>? Properties,
    IReadOnlyList<ObjectRule>? NestedObjects,
    IReadOnlyList<CollectionRule>? Collections,
    IReadOnlyList<ExtensionRule>? Extensions      // Extensions can appear in nested objects
);

public record ExtensionRule(
    string Name,
    MemberSelection MemberSelection,
    string? LogicalSchema,                    // Optional schema for extension definitions
    IReadOnlyList<PropertyRule>? Properties,
    IReadOnlyList<ObjectRule>? Objects,
    IReadOnlyList<CollectionRule>? Collections
);

public record CollectionRule(
    string Name,
    MemberSelection MemberSelection,
    string? LogicalSchema,                            // Optional schema (inherited from ClassDefinition)
    IReadOnlyList<PropertyRule>? Properties,
    IReadOnlyList<ObjectRule>? NestedObjects,         // Collections can contain nested objects
    IReadOnlyList<CollectionRule>? NestedCollections, // Collections can contain nested collections
    IReadOnlyList<ExtensionRule>? Extensions,         // Collections can contain extensions
    CollectionItemFilter? ItemFilter                  // Single filter per collection (XSD maxOccurs="1")
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

### 5.3 Response Content-Type Header

When a profile is active (either explicitly specified or implicitly selected),
the response `Content-Type` header **must** be set to the profile-specific
media type. This is required for ODS/API compatibility.

**Format:**

```text
Content-Type: application/vnd.ed-fi.{resourceName}.{profileName}.readable+json
```

**Example:**

```text
Content-Type: application/vnd.ed-fi.student.student-minimum-data.readable+json
```

**Implementation:**

```csharp
private string GetReadContentType(ProfileContext? profileContext, string resourceName)
{
    if (profileContext == null)
    {
        return "application/json";
    }

    return $"application/vnd.ed-fi.{resourceName.ToLower()}.{profileContext.ProfileName.ToLower()}.readable+json";
}
```

**Rationale:** Setting the response Content-Type allows clients to:

1. Confirm which profile was applied (important for implicit selection)
2. Verify the profile matches their expectations
3. Handle profile-specific response parsing if needed

**ODS/API Reference:** `DataManagementControllerBase.GetReadContentType()` and
`ProfilesContentTypeHelper.CreateContentType()` implement this behavior.

### 5.4 Identity Field Protection

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

**Note:** Update (PUT) behavior is different. Updates are allowed as long as
key fields are present, even if the profile excludes other required members.
This is because the existing resource already has those required values stored.

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

#### 6.2.4 PUT Merge Behavior for Excluded Fields

During PUT operations, excluded fields behave differently depending on whether
they are **key fields** (part of the natural key/identity) or **non-key fields**:

##### Non-Key Field Exclusion (Preserved)

When a profile excludes a **non-key** property within a collection item, the
excluded property's value from the **existing document** is preserved during
PUT. This mirrors ODS/API behavior where `SynchronizeTo` skips excluded
non-key properties, leaving existing values in place.

**Example:** A profile excludes `nameOfCounty` from addresses (a non-key field):

- POST with full data: `nameOfCounty: "Travis"` is saved
- PUT with different value: `nameOfCounty: "Harris"` is ignored
- Result: `nameOfCounty: "Travis"` is preserved from existing document

##### Key Field Exclusion (Not Supported)

Excluding a **key field** (part of the collection item's natural key) is
**not supported** for PUT merge. Key fields determine which existing item
the incoming item should be matched against. In ODS/API, attempting to
exclude a key field results in a `DataPolicyException` because the signature
(identity) of the item cannot be determined without all key fields.

**Key fields for collections** are defined by the `ArrayUniquenessConstraint`
in the ApiSchema. For example, for addresses, the key fields are:

- `addressTypeDescriptor`
- `city`
- `postalCode`
- `stateAbbreviationDescriptor`
- `streetNumberName`

**Non-key fields** (which CAN be excluded and preserved) include:

- `nameOfCounty`
- `congressionalDistrict`
- `latitude`
- `longitude`
- etc.

##### Item Matching for Merge

To preserve excluded non-key fields, DMS must match incoming collection items
with existing items. Matching is performed using the collection's key fields
(from `ArrayUniquenessConstraint`). When an `ItemFilter` is present on the
collection, the filter's property is used as the matching key instead.

**Merge Process:**

1. Fetch existing document from database
2. For each collection with excluded non-key properties:
   a. For each incoming item, find matching existing item by key fields
   b. Copy excluded property values from existing item to incoming item
3. Continue with normal PUT processing

**ODS/API Reference:** The `SynchronizeTo` method in entity mappings implements
this behavior by conditionally skipping assignment of excluded properties,
while the `MapTo` method always assigns primary key properties regardless of
profile exclusions.

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
| `IncludeOnly` | Include collection, filter to specified properties |
| `ExcludeOnly` | Include collection, exclude specified properties |

**Note:** `ExcludeAll` is defined in the ODS XSD schema but throws
`NotImplementedException` at runtime. DMS should not support `ExcludeAll`
to maintain compatibility with ODS/API behavior.

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
   Matching is **case-sensitive** (consistent with ODS/API behavior).

2. **Single filter per collection**: The XSD schema specifies `maxOccurs="1"` for
   the `<Filter>` element. Each collection can have at most one filter.

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
    <Object name="NestedObjectName" memberSelection="IncludeOnly|ExcludeOnly|IncludeAll">
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
    string? LogicalSchema,                        // Optional schema for extension objects
    IReadOnlyList<PropertyRule>? Properties,
    IReadOnlyList<ObjectRule>? NestedObjects,     // Recursive nesting
    IReadOnlyList<CollectionRule>? Collections,
    IReadOnlyList<ExtensionRule>? Extensions      // Extensions can appear in nested objects
);
```

### 7.5 Extension Filtering

Ed-Fi extensions are filtered using the `<Extension>` element, which works
similarly to `<Object>` but targets extension namespaces.

#### Extension XML Structure

```xml
<ReadContentType memberSelection="IncludeOnly">
    <Extension name="ExtensionNamespace" memberSelection="IncludeOnly|ExcludeOnly|IncludeAll">
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
    string? LogicalSchema,                          // Optional schema for extension definitions
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

### 8.1 Graceful Failure for Invalid Profiles

While profiles are validated when inserted into CMS, DMS must still handle
invalid or misconfigured profiles gracefully at runtime. This could occur due to:

- Data corruption
- Manual database modifications

When DMS encounters an invalid profile, it should return an appropriate error
response (see 8.2) rather than throwing an unhandled exception.

### 8.2 Error Response Matrix

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

### 8.3 Error Response Format

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

### 8.4 Example Error Responses

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
    "title": "Method Not Allowed with Profile",
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
    "detail": "A data policy failure was encountered. The request was not constructed correctly for the data policy that has been applied to this data for the caller.",
    "type": "urn:ed-fi:api:security:data-policy:incorrect-usage",
    "title": "Data Policy Failure Due to Incorrect Usage",
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
`TenantId` and `ProfileName`.

Lifecycle expectations:

- Profile definitions are loaded once from CMS and cached with TTL.
- Parsed profile-specific resource models are built from those definitions and
cached alongside them with the same TTL.
- Per-request header parsing remains uncached and is evaluated on every call.

```text
┌─────────────────────────────────────────────────────────────────────────────┐
│                          Profile Caching Architecture                       │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │  In-Memory Application Profiles Cache                               │    │
│  │  ┌─────────────────────────────────────────────────────────────┐    │    │
│  │  │  Key: (TenantId, ApplicationId)                             │    │    │
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
│   Profile   │     │  1. Extract TenantId and ApplicationId from request     │
└─────────────┘     │  2. Check cache for (TenantId, ApplicationId)           │
                    │     │                                                   │
                    │     ├── Cache hit: Lookup profile by name from cache    │
                    │     │                                                   │
                    │     └── Cache miss:                                     │
                    │         a. Call CMS: GET /v2/applications/{id}/profiles │
                    │         b. Parse all profile XMLs                       │
                    │         c. Build and cache parsed profile definitions and resource models (same TTL) │
                    │         d. Build/reset profile-specific API metadata/OpenAPI cache entries (pre-warm optional) │
                    │         e. Store in cache as Dictionary<Name, Parsed>   │
                    │         f. Lookup requested profile by name             │
                    │                                                         │
                    │  3. Return ProfileContext for pipeline                  │
                    └─────────────────────────────────────────────────────────┘
```

### 9.3 Cache Configuration

```json
{
  "ProfileCache": {
    "ApplicationProfilesTtlSeconds": 1800
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

---

## 12. Profile-Specific OpenAPI Schemas

### 12.1 Overview

ODS/API publishes profile-specific OpenAPI documents that reflect the
constrained resource schemas for each profile. DMS must provide equivalent
functionality to enable client code generation and API documentation.

### 12.2 Endpoint

```text
GET /metadata/data/v3/profiles/{profileName}/swagger.json
```

### 12.3 Runtime Generation

DMS must generate profile-specific OpenAPI documents at runtime because:

1. Profiles are stored in CMS and can be created/modified dynamically
2. The base OpenAPI schema is already generated at runtime from ApiSchema
3. Profile definitions are fetched and cached from CMS

### 12.4 Schema Modification Approach

For each profile, DMS generates a modified OpenAPI document by:

1. Starting with the base resource OpenAPI schema
2. Applying profile rules to filter properties:
   - `IncludeOnly`: Remove properties not in the allowed list
   - `ExcludeOnly`: Remove properties in the excluded list
3. Generating separate schemas for readable vs writable content types
4. Updating `required` arrays to reflect profile constraints
5. Adding profile-specific content type headers to operations

### 12.5 Response Structure

The generated OpenAPI document follows standard OpenAPI 3.0 format with:

- Profile name in the `info.title`
- Only resources covered by the profile included
- Schema properties filtered per profile rules
- Appropriate `Accept`/`Content-Type` headers documented

### 12.6 Caching

Profile OpenAPI documents should be cached with the same TTL as profile
definitions (default 30 minutes) to avoid regeneration on every request.

---

## 13. OpenAPI Schema Transformation Rules

### 13.1 Overview

When generating profile-specific OpenAPI specifications, DMS applies a
multi-step transformation pipeline to the base API schema. This section
documents the detailed rules and logic for creating profile-filtered schemas.

### 13.2 Transformation Pipeline

The transformation follows a 7-step pipeline:

1. **Filter Paths** - Remove paths for resources not covered by the profile and disallowed operations
2. **Remove Unused Parameters** - Clean up component parameters no longer referenced
3. **Remove Unused Schemas (First Pass)** - Clean up schemas not referenced by remaining paths
4. **Create Profile Schemas** - Generate `_readable` and `_writable` schema variants
5. **Remove Base Schemas** - Remove original schemas that now have suffixed versions
6. **Remove Unused Schemas (Final Pass)** - Final cleanup after profile schema creation
7. **Remove Unused Tags** - Clean up tags not referenced by remaining paths

### 13.3 Schema Suffix Rules

#### 13.3.1 Suffix Application

- **GET operations**: Use `_readable` suffix for response schemas
- **POST/PUT operations**: Use `_writable` suffix for request body schemas
- **DELETE operations**: Part of Write profiles; filtered out from Read profiles

#### 13.3.2 Double-Suffix Prevention

**Critical Rule**: Never apply a suffix to a schema name that already ends with
`_readable` or `_writable`. This prevents creating malformed schemas like
`EdFi_School_readable_readable`.

**Implementation**: Before adding a suffix, check:

```
if schemaName.endsWith("_readable") OR schemaName.endsWith("_writable")
    skip suffix application
```

#### 13.3.3 Recursive Suffix Application

When creating a suffixed schema (e.g., `EdFi_Student_readable`):

1. Clone the base schema
2. Apply property filtering (if root resource)
3. Recursively create suffixed versions for all nested schema references
4. Update all `$ref` values in the clone to point to suffixed versions
5. **Important**: Do NOT apply property filtering to nested/referenced schemas

### 13.4 Content Type Transformation

#### 13.4.1 Format

Profile-specific content types follow this pattern:

```
application/vnd.ed-fi.{resource}.{profile}.{usage}+json
```

Where:

- `{resource}` = lowercase singular resource name (e.g., `student`)
- `{profile}` = lowercase profile name (e.g., `exclude-birthdate`)
- `{usage}` = `readable` or `writable`

#### 13.4.2 Operation Updates

**For GET operations (Response)**:

- Original: `responses[*].content["application/json"]`
- Becomes: `responses[*].content["application/vnd.ed-fi.student.profilename.readable+json"]`
- Schema `$ref` updated to use `_readable` suffix

**For POST/PUT operations (Request)**:

- Original: `requestBody.content["application/json"]`
- Becomes: `requestBody.content["application/vnd.ed-fi.student.profilename.writable+json"]`
- Schema `$ref` updated to use `_writable` suffix

### 13.5 Readable Schema Rules

Readable schemas are used for GET response payloads and must include server-generated
and identity fields while respecting profile filtering rules.

#### 13.5.1 Always-Included Properties

The following properties are **always included** in readable schemas regardless of
profile filtering rules:

- `id` - Server-generated surrogate key
- `link` - Self-reference link
- `_etag` - Concurrency token
- `_lastModifiedDate` - Last modification timestamp
- Properties ending with `Reference` - Resource references
- Properties ending with `UniqueId` - Natural key identifiers

#### 13.5.2 Property Filtering Rules

After preserving required properties, apply `MemberSelection` rules:

**IncludeOnly Mode**:

- Remove any property NOT explicitly listed in the profile's property collection
- Exception: Always-included properties (listed above) are retained

**ExcludeOnly Mode**:

- Remove only properties explicitly listed in the profile's property collection
- Exception: Always-included properties cannot be excluded

**IncludeAll Mode**:

- Keep all properties (no filtering applied)

#### 13.5.3 Additional Includes

Beyond individual properties, readable schemas must include:

- All properties specified in `Objects` collection (nested objects)
- All properties specified in `Collections` collection (array properties)
- `_ext` property if the profile has any `Extensions` rules

#### 13.5.4 Required Array Updates

After removing filtered properties:

1. Locate the schema's `required` array
2. Remove any property names that were filtered out
3. Preserve the order of remaining required properties

### 13.6 Writable Schema Rules

Writable schemas are used for POST/PUT request payloads and must exclude
server-generated fields while including natural identity properties.

#### 13.6.1 Always-Excluded Properties

The following properties are **always excluded** from writable schemas:

- `id` - Server-generated surrogate key (not client-provided)
- `_etag` - Server-managed concurrency token
- `_lastModifiedDate` - Server-managed timestamp
- `link` - Server-generated self-reference

#### 13.6.2 Always-Included Properties

The following properties are **always included** for identity purposes:

- Properties ending with `Reference` - Required for referential integrity
- Properties ending with `UniqueId` - Natural key identifiers

#### 13.6.3 Property Filtering Rules

After applying always-excluded/included rules, apply `MemberSelection` rules:

**IncludeOnly Mode**:

- Remove any property NOT explicitly listed in the profile's property collection
- Exception: Always-included identity properties are retained

**ExcludeOnly Mode**:

- Remove only properties explicitly listed in the profile's property collection
- Exception: Identity properties cannot be excluded

**IncludeAll Mode**:

- Include all properties (except always-excluded fields)

#### 13.6.4 Additional Includes

Same rules as readable schemas for Objects, Collections, and Extensions.

#### 13.6.5 Required Array Updates

Same process as readable schemas - update to reflect filtered properties.

### 13.7 Nested Schema References

When a schema contains references to other schemas (e.g., nested objects,
collections, or inline arrays), all referenced schemas must also be created
with matching suffixes.

#### 13.7.1 Reference Discovery

The transformation recursively discovers all `$ref` values:

- Direct property references: `"$ref": "#/components/schemas/EdFi_Address"`
- Array item references: `"items": { "$ref": "#/components/schemas/..." }`
- Nested object references at any depth

#### 13.7.2 Recursive Schema Creation

For each discovered reference:

1. Extract the schema name from the `$ref` path
2. Skip if schema name already has a profile suffix
3. Create a suffixed version of the referenced schema
4. **Do NOT** apply property filtering (only applies to root resource schemas)
5. Recursively process any nested references within that schema
6. Update the `$ref` value to point to the suffixed version

#### 13.7.3 Deduplication

Track created schemas in a `HashSet<string>` to avoid recreating schemas
that have already been processed.

### 13.8 Schema Cleanup

#### 13.8.1 Remove Base Schemas

After creating all suffixed schemas, remove the original base schemas if they
have suffixed versions:

- If `EdFi_Student_readable` or `EdFi_Student_writable` exists
- Remove `EdFi_Student` from the components/schemas collection

This ensures clients only see the profile-specific schemas.

#### 13.8.2 Remove Unreferenced Schemas

Perform two passes of unreferenced schema removal:

**Pass 1** (after path filtering):

- Remove schemas not referenced by any remaining path operation
- Keep transitively referenced schemas (e.g., schemas referenced by kept schemas)

**Pass 2** (after profile schema creation):

- Final cleanup to remove any schemas orphaned by the transformation process
- Use graph traversal starting from path operations to identify kept schemas

### 13.9 Path and Operation Filtering

#### 13.9.1 Path Removal

Remove entire paths (endpoints) if:

- The resource is not included in the profile's resource collection
- Example: If profile only covers `Student`, remove `/schools`, `/programs`, etc.

#### 13.9.2 Operation Removal

For paths that ARE included in the profile, remove individual operations based
on content type support:

**Remove GET operation if**:

- Profile resource has no `ReadContentType` definition
- Indicates the resource is write-only in this profile

**Remove POST operation if**:

- Profile resource has no `WriteContentType` definition
- Indicates the resource is read-only in this profile

**Remove PUT operation if**:

- Profile resource has no `WriteContentType` definition
- Same rule as POST - writable content type covers both

**DELETE operations**:

- Never removed based on profile rules
- Profiles do not apply to DELETE operations

### 13.10 Example Transformation

#### 13.10.1 Base Schema (Before)

```json
{
  "EdFi_Student": {
    "type": "object",
    "properties": {
      "id": { "type": "string" },
      "studentUniqueId": { "type": "string" },
      "firstName": { "type": "string" },
      "lastName": { "type": "string" },
      "birthDate": { "type": "string", "format": "date" },
      "_etag": { "type": "string" }
    },
    "required": ["studentUniqueId", "firstName", "lastName"]
  }
}
```

#### 13.10.2 Profile Definition

```xml
<Profile name="ExcludeBirthDate">
  <Resource name="Student">
    <ReadContentType memberSelection="ExcludeOnly">
      <Property name="birthDate"/>
    </ReadContentType>
    <WriteContentType memberSelection="ExcludeOnly">
      <Property name="birthDate"/>
    </WriteContentType>
  </Resource>
</Profile>
```

#### 13.10.3 Readable Schema (After)

```json
{
  "EdFi_Student_readable": {
    "type": "object",
    "properties": {
      "id": { "type": "string" },
      "studentUniqueId": { "type": "string" },
      "firstName": { "type": "string" },
      "lastName": { "type": "string" },
      "_etag": { "type": "string" }
      // birthDate removed (ExcludeOnly)
      // id, _etag retained (always-included)
    },
    "required": ["studentUniqueId", "firstName", "lastName"]
  }
}
```

#### 13.10.4 Writable Schema (After)

```json
{
  "EdFi_Student_writable": {
    "type": "object",
    "properties": {
      "studentUniqueId": { "type": "string" },
      "firstName": { "type": "string" },
      "lastName": { "type": "string" }
      // birthDate removed (ExcludeOnly)
      // id, _etag excluded (always-excluded from writable)
    },
    "required": ["studentUniqueId", "firstName", "lastName"]
  }
}
```

### 13.11 OpenAPI Info Section

The transformed OpenAPI document must update the `info` section:

**Title**:

```json
{
  "info": {
    "title": "{ProfileName} Resources",
    "description": "Profile-filtered API for {ProfileName}. Based on: {OriginalTitle}"
  }
}
```

**Servers Array**:

- Preserve the `servers` array from the base specification
- Update with appropriate base URLs for the deployment
