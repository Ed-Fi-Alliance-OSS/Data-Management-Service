# Reference validation

Please refer [Referential integrity
checking](https://github.com/Ed-Fi-Alliance-OSS/Project-Tanager/blob/main/docs/DMS/PRIMARY-DATA-STORAGE/README.md#references-table)
for more details.

## Remove reference validation

If a user wants to remove referential integrity checking to facilitate faster
loading of test data, the following database script can be used to remove the
foreign key constraints on the `References` table.

``` sql
ALTER TABLE Reference  DROP CONSTRAINT FK_Reference_ReferencedAlias
```

> [!NOTE]
> `FK_Reference_ParentDocument` still enforces cascades but does not
> participate in reference validation, and the legacy
> `FK_Reference_ReferencedDocument` constraint has been removed from the schema.
