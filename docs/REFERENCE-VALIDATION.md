# Remove reference validation

If a user wants to remove referential integrity checking to facilitate faster
loading of test data, the following database scripts can be used to remove the
foreign key constraints on the tables.

## Getting the list of foreign key constraints on the table

``` sql
SELECT conname
FROM pg_constraint
WHERE conrelid = '<table-name>'::regclass
AND contype = 'f';
```

## Remove reference validation constraint from `References` table

``` sql
ALTER TABLE "references"  DROP CONSTRAINT FK_References_ReferencedAlias
```
