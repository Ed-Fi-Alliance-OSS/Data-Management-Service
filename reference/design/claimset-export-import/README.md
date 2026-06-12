# Documents

- [Claim Set Export / Import API Design](claim-set-export-import-api-design.md)
    Defines the revised payload format for CMS claim set retrieval, export,
    and import in Ed-Fi API v8 so that mappings to the CMS data store
    remain clear and unambiguous. The format changes are intentionally
    limited; see the Payload Differences (v2 vs v3) section for a detailed
    comparison.

- [Claims Migration: Options, Recommendations, and Two-Phase Approach for Ed-Fi API Transition](claims-migration.md)
    Describes the recommended approach for migrating security configuration
    from ODS/API to Ed-Fi API v8 by using a separate utility that is already
    partially implemented. Because the Admin API currently supports export
    only on a claim set by claim set basis, and because the CMS data store
    must first be seeded with the core authorization hierarchy,
    organizations should complete the one-time baseline activity described
    in Option 1: Bulk XML Migration before relying on incremental claim set
    export and import operations.
