# Migrator

The one-shot schema migration tool. It waits for SQL Server, creates the target
database when missing, then applies every `*.sql` file under `MIGRATIONS_PATH`
in ordinal order. Each script is split on `GO` lines and executed batch by
batch; the generated migrations manage their own transactions, and any failure
stops the tool with a non-zero exit.

## Bootstrap SQL (Azure SQL)

Set `BOOTSTRAP_SQL` to the path of a SQL file to apply it after the migration
scripts, on the same connection and with the same batch executor. When the
setting is unset or empty the tool behaves exactly as before.

- `BOOTSTRAP_SQL` must name an existing file; a missing file fails before the
  tool waits for SQL Server, with a message naming the path.
- `${NAME}` tokens in the file are replaced from environment variables, where
  `NAME` matches `[A-Za-z_][A-Za-z0-9_]*`; any other text is left untouched. A
  missing or empty variable fails with a message naming it. Substitution is a
  single pass, so a value that itself contains `${...}` is not expanded again.
- The expanded script is never logged, because it contains database passwords.
  Only the path and batch count are printed.
- The image ships this repository's bootstrap directory at `/app/bootstrap`, so
  `BOOTSTRAP_SQL=/app/bootstrap/azure-app-users.sql` works without a volume.

Azure SQL Database cannot switch databases with `USE [other_database]` and does
not support three-part names, so the committed
[`db/bootstrap/azure-app-users.sql`](../../db/bootstrap/azure-app-users.sql)
dispatches on `DB_NAME()` and is applied once per connection:

| Job connection | Effect |
|----------------|--------|
| `Database=master` | creates/updates the `sme_api_app` and `sme_auth_app` server logins |
| `Database=sme_api` | creates the `sme_api_app` user and grants `db_datareader` + `db_datawriter` |
| `Database=sme_auth` | creates the `sme_auth_app` user and grants `db_datareader` + `db_datawriter` |

Each run needs `SME_API_DB_PASSWORD` and `SME_AUTH_DB_PASSWORD` in its
environment; the migrator's connection string already carries the schema-owner
credentials that are allowed to create logins and users.
