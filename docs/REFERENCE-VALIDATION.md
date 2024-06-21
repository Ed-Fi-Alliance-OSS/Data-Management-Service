# Reference validation

Please refer [Referential integrity
checking](https://github.com/Ed-Fi-Alliance-OSS/Project-Tanager/blob/main/docs/DMS/PRIMARY-DATA-STORAGE/README.md#references-table)
for more details.

## Remove reference validation

If a user wants to remove referential integrity checking to facilitate faster
loading of test data, the following database script can be used to remove the
foreign key constraints on the `References` table.

``` sql
ALTER TABLE "references"  DROP CONSTRAINT FK_References_ReferencedAlias
```

> [!NOTE]
> The other foreign key constraints on the tables do not affect the
> referential integrity checking, so there is no need to remove them for this
> purpose.
