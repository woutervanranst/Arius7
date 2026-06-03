---
name: csharp-sqlite
description: Use when writing or reviewing C# code that uses SQLite with Microsoft.Data.Sqlite, including connection strings, transactions, WAL, type mapping, parameters, BLOB streaming, encryption, Dapper, user-defined functions, extensions, and provider limitations.
invocable: false
---

# SQLite in C# with Microsoft.Data.Sqlite

## When to Use This Skill

Use this skill when:
- Adding SQLite data access to a .NET app using `Microsoft.Data.Sqlite`
- Reviewing SQLite connection strings, transactions, retries, or concurrency behavior
- Mapping C# types to SQLite values or tuning parameter types
- Using SQLite with Dapper
- Adding BLOB streaming, online backup, collations, user-defined functions, loadable extensions, encryption, or custom native SQLite builds
- Troubleshooting `SqliteException`, locking, metadata, or ADO.NET provider limitations

## Default Package Choice

Use `Microsoft.Data.Sqlite` for normal application code. It is the lightweight ADO.NET provider used by EF Core's SQLite provider and works independently of EF Core.

```bash
dotnet add package Microsoft.Data.Sqlite
```

If the app needs a different native SQLite library, do not use the default package. Use `Microsoft.Data.Sqlite.Core` plus the selected SQLitePCLRaw bundle or provider.

Common bundle choices:

| Need | Package direction |
|------|-------------------|
| Default cross-platform SQLite with FTS4, FTS5, JSON1, and R*Tree | `Microsoft.Data.Sqlite` or `Microsoft.Data.Sqlite.Core` plus `SQLitePCLRaw.bundle_e_sqlite3` |
| SQLCipher-style encryption using the unsupported open-source build | `Microsoft.Data.Sqlite.Core` plus `SQLitePCLRaw.bundle_e_sqlcipher` |
| System SQLite | `Microsoft.Data.Sqlite.Core` plus `SQLitePCLRaw.bundle_sqlite3` or platform-specific provider setup |
| Windows system SQLite | `Microsoft.Data.Sqlite.Core` plus `SQLitePCLRaw.bundle_winsqlite3` |
| Official SQLCipher builds | `Microsoft.Data.Sqlite.Core` plus Zetetic/native provider setup |

When adding packages in this repository, follow the `package-management` skill and use `dotnet add package`; do not hand-edit project XML.

## Provider Model

`Microsoft.Data.Sqlite` uses `SQLitePCLRaw` to talk to native SQLite.

Use bundles when possible because they initialize automatically. If configuring a provider manually, do it before any `Microsoft.Data.Sqlite` API is used, and avoid bundle packages that can override the provider.

Use low-level SQLitePCLRaw interop only when the ADO.NET provider does not expose the needed native capability. `SqliteConnection` and `SqliteDataReader` expose underlying SQLitePCLRaw handles; those can also expose native pointers for P/Invoke.

## Connection Strings

Prefer `SqliteConnectionStringBuilder` when building connection strings from user input. It prevents connection string injection and makes supported keywords explicit.

Supported keywords to know:

| Keyword | Guidance |
|---------|----------|
| `Data Source` | File path, `:memory:`, empty temp database, named memory database, or URI filename. `DataSource` and `Filename` are aliases. Relative paths are relative to current working directory. |
| `Mode` | `ReadWriteCreate` default, `ReadWrite`, `ReadOnly`, or `Memory`. Use `ReadOnly` when the app must not mutate the database. |
| `Cache` | `Default`, `Private`, or `Shared`. Shared cache changes transaction and locking behavior. |
| `Password` | Sends `PRAGMA key` after open. Only works if the native SQLite library supports encryption. |
| `Foreign Keys` | Sends `PRAGMA foreign_keys = 1` or `0` after open. Not needed if the native library was compiled with default foreign keys enabled. |
| `Recursive Triggers` | Enables recursive triggers after open. |
| `Default Timeout` | Default command timeout in seconds. `Command Timeout` is an alias. Default is 30. |
| `Pooling` | Enabled by default. Disable only with a concrete reason. |
| `Vfs` | Selects the SQLite virtual file system implementation. |

Examples:

```text
Data Source=Application.db
Data Source=Reference.db;Mode=ReadOnly
Data Source=:memory:
Data Source=SharedTests;Mode=Memory;Cache=Shared
Data Source=Encrypted.db;Password=MyEncryptionKey
```

Be careful with `Cache=Shared`: it enables shareable in-memory databases and read-uncommitted scenarios, but it changes locking behavior. Microsoft recommends removing `Cache=Shared` when using write-ahead logging for optimal performance.

## In-Memory Databases

`Data Source=:memory:` creates a private database per connection. The database is deleted when that connection closes.

For a database shared across multiple connections, use a named in-memory database with memory mode and shared cache:

```text
Data Source=InMemorySample;Mode=Memory;Cache=Shared
```

A shareable in-memory database exists only while at least one connection to that named database remains open. In tests, keep an owner connection open for the lifetime of the test fixture when other connections need to see the same in-memory state.

## Async and Concurrency

SQLite does not support asynchronous I/O. Async ADO.NET methods execute synchronously in `Microsoft.Data.Sqlite`. Avoid calling async database methods merely for SQLite I/O; they do not make SQLite nonblocking.

For performance and concurrency, prefer write-ahead logging:

```sql
PRAGMA journal_mode = 'wal';
```

SQLite permits concurrent access, but `Microsoft.Data.Sqlite` objects are not thread-safe. Do not share `SqliteConnection`, `SqliteCommand`, or `SqliteDataReader` concurrently across threads. Create and open a new connection per operation or unit of work; pooling keeps this cheap.

SQLite is aggressive about locks. Busy or locked errors are automatically retried until the command timeout is reached. Tune timeouts deliberately:

```csharp
connection.DefaultTimeout = 60;
command.CommandTimeout = 60;
```

Use `DefaultTimeout` for implicit commands created by the provider, such as transaction startup.

## Transactions

Use ADO.NET transactions; `System.Transactions` is not supported.

SQLite allows only one transaction with pending writes at a time. `BeginTransaction` and command execution may time out when another transaction holds locks.

Always use transactions for batches of writes and bulk insert/update work. Reuse the same parameterized command inside the transaction so the compiled statement can be reused.

```csharp
using var transaction = connection.BeginTransaction();
using var command = connection.CreateCommand();
command.Transaction = transaction;
command.CommandText = "INSERT INTO item (name) VALUES ($name)";
var nameParameter = command.Parameters.Add("$name", SqliteType.Text);

foreach (var name in names)
{
    nameParameter.Value = name;
    command.ExecuteNonQuery();
}

transaction.Commit();
```

SQLite transactions are serializable by default. Read-uncommitted behavior is only available with shared cache, and `Microsoft.Data.Sqlite` treats the requested isolation level as a minimum that may be promoted.

Deferred transactions are available in provider version 5.0 and later. They start the database transaction only when the first command runs and can upgrade from read to write as needed. If a deferred transaction fails during upgrade because the database is locked, retry the entire transaction.

Savepoints are available in provider version 6.0 and later. Use them for nested transaction-like behavior or partial rollback inside a larger transaction.

Unsupported SQLite isolation levels: `Chaos` and `Snapshot`.

## Parameters

Always use parameters for user data. SQLite accepts parameter names with `:`, `@`, or `$` prefixes, but `Microsoft.Data.Sqlite` only supports named parameters, not positional parameters.

```csharp
command.CommandText = "SELECT id FROM user WHERE email = $email";
command.Parameters.AddWithValue("$email", email);
```

Use `SqliteParameter.Size` to truncate TEXT or BLOB values deliberately:

```csharp
command.Parameters.AddWithValue("$name", name).Size = 30;
```

Use `SqliteParameter.SqliteType` when the default mapping is not the storage format you want:

| .NET value | Alternative SQLite type | Typical reason |
|------------|-------------------------|----------------|
| `Char` | `Integer` | UTF-16 code unit |
| `DateOnly` | `Real` | Julian day value |
| `DateTime` | `Real` | Julian day value |
| `DateTimeOffset` | `Real` | Julian day value |
| `Guid` | `Blob` | Compact binary representation |
| `TimeOnly` | `Real` | Days |
| `TimeSpan` | `Real` | Days |

SQLite does not support output parameters. Return values in query results.

## Type Mapping

SQLite has only four primitive storage types: `INTEGER`, `REAL`, `TEXT`, and `BLOB`. APIs that return database values as `object` only return `long`, `double`, `string`, or `byte[]`.

Default mappings to remember:

| .NET type | SQLite type | Notes |
|-----------|-------------|-------|
| `bool` | `INTEGER` | `0` or `1` |
| Integer numeric types | `INTEGER` | `UInt64` large values can overflow |
| `float`, `double` | `REAL` | |
| `decimal` | `TEXT` | Avoids lossy REAL conversion |
| `string`, `char` | `TEXT` | UTF-8 for default `char` mapping |
| `byte[]` | `BLOB` | |
| `DateOnly` | `TEXT` | `yyyy-MM-dd` |
| `DateTime` | `TEXT` | `yyyy-MM-dd HH:mm:ss.FFFFFFF` |
| `DateTimeOffset` | `TEXT` | `yyyy-MM-dd HH:mm:ss.FFFFFFFzzz` |
| `TimeOnly` | `TEXT` | `HH:mm:ss.fffffff` |
| `TimeSpan` | `TEXT` | `d.hh:mm:ss.fffffff` |
| `Guid` | `TEXT` | Canonical GUID string |

Prefer the four primitive SQLite type names in schemas: `INTEGER`, `REAL`, `TEXT`, and `BLOB`. SQLite lets you invent column type names, but affinity rules can surprise you. For example, `STRING` has numeric affinity and may attempt INTEGER or REAL conversion.

SQLite accepts length, precision, and scale facets in column declarations, but it does not enforce them. Enforce those constraints in the app or with explicit SQL constraints.

## Dapper

Dapper works with `Microsoft.Data.Sqlite`, but there are provider-specific traps:

- SQLite parameter names are case-sensitive.
- Dapper expects `@`-prefixed parameters. Other prefixes can fail with Dapper even though the provider accepts them.
- Dapper reads through the `SqliteDataReader` object indexer, so raw values are only `long`, `double`, `string`, or `byte[]`.
- Add Dapper type handlers for `DateTimeOffset`, `Guid`, and `TimeSpan` if they appear in query results.

## Metadata

`DbConnection.GetSchema()` is not implemented. Query SQLite metadata directly instead.

Useful sources:

```sql
SELECT name, type, sql
FROM sqlite_master;

SELECT t.name AS tbl_name, c.name, c.type, c.notnull, c.dflt_value, c.pk
FROM sqlite_master AS t,
     pragma_table_info(t.name) AS c
WHERE t.type = 'table';
```

For result-set metadata, use `SqliteDataReader.GetSchemaTable()`. It can report origin table, origin column, nullability, key/unique flags, expression status, SQLite type name, and default .NET type where available.

## Batching and Readers

SQLite does not natively need network-style batching, but `Microsoft.Data.Sqlite` supports multiple statements in one command as a convenience.

When using `ExecuteReader` with batches:

- Execution proceeds to the first statement that returns results.
- `NextResult()` advances to the next result-producing statement or end of batch.
- Disposing or closing the reader executes remaining statements that were not consumed.
- Always dispose readers. If the finalizer later tries to execute remaining statements, errors are ignored.

## BLOB Streaming

Use `SqliteBlob` for large BLOB values to avoid loading entire objects into memory.

Pattern:

1. Insert the row normally.
2. Allocate BLOB space with SQLite `zeroblob()`.
3. Use `last_insert_rowid()` or another rowid alias to identify the row.
4. Open a `SqliteBlob` stream to write or read.

When reading with `GetStream()`, select the rowid or one of its aliases in addition to the BLOB column. Without the rowid, the provider loads the entire object into memory instead of returning a `SqliteBlob` stream.

## Online Backup

Use `SqliteConnection.BackupDatabase()` to back up a database while the app is running. The current provider API backs up as quickly as possible and blocks other connections from writing during the backup.

Plan backup timing accordingly for write-heavy applications.

## Collations, LIKE, and User-Defined Functions

SQLite built-in collations:

| Collation | Behavior |
|-----------|----------|
| `RTRIM` | Ignores trailing whitespace |
| `NOCASE` | ASCII-only case-insensitive comparison for A-Z |
| `BINARY` | Byte-wise case-sensitive comparison |

Use `SqliteConnection.CreateCollation()` to add or override collations, such as replacing `NOCASE` with Unicode-aware comparison.

The `LIKE` operator does not honor collations. Its default behavior is ASCII-only case-insensitive, similar to `NOCASE`. Use `PRAGMA case_sensitive_like = 1` for case-sensitive LIKE, or override the corresponding scalar function when custom semantics are required.

Use `CreateFunction()` for scalar functions and `CreateAggregate()` for aggregate functions. Prefer the `state` or `seed` arguments over closures. Mark deterministic functions with `isDeterministic` so SQLite can optimize query compilation.

Operators implemented by overridable scalar functions:

| Operator | Function |
|----------|----------|
| `X GLOB Y` | `glob(Y, X)` |
| `X LIKE Y` | `like(Y, X)` |
| `X LIKE Y ESCAPE Z` | `like(Y, X, Z)` |
| `X MATCH Y` | `match(Y, X)` |
| `X REGEXP Y` | `regexp(Y, X)` |

If a user-defined function throws, SQLite raises an error and the provider throws `SqliteException`. Throw `SqliteException` yourself when you need a specific SQLite error code.

## Extensions

Use `SqliteConnection.LoadExtension()` to load SQLite extensions. The provider keeps the extension loaded even if the connection is closed and reopened.

SQLite loads native extensions through platform APIs, not .NET assembly/native library resolution. You may need to adjust `PATH`, `LD_LIBRARY_PATH`, or `DYLD_LIBRARY_PATH` before loading an extension, especially when binaries come from a NuGet package.

## Encryption

SQLite does not encrypt database files by default. Encryption requires a modified native SQLite library such as SQLCipher, SQLiteCrypt, or wxSQLite3.

For the unsupported open-source SQLCipher bundle, switch packages:

```bash
dotnet remove package Microsoft.Data.Sqlite
dotnet add package Microsoft.Data.Sqlite.Core
dotnet add package SQLitePCLRaw.bundle_e_sqlcipher
```

Use the `Password` connection string keyword to send `PRAGMA key` immediately after opening the connection. This has no effect unless the native SQLite library supports encryption.

When changing an encryption key, issue `PRAGMA rekey`. SQLite does not support parameters in PRAGMA statements, so use SQLite's `quote()` function to avoid injection rather than string-concatenating raw input.

## Errors and Retries

SQLite errors are surfaced as `SqliteException`. Inspect both:

- `SqliteErrorCode`
- `SqliteExtendedErrorCode`

Errors can occur while opening connections, beginning transactions, executing commands, and advancing batched readers via `NextResult()`.

Design retry behavior at the transaction or unit-of-work level, especially for deferred transaction upgrades and write contention. Command timeout retry handles busy/locked waits inside the provider, but it does not make multi-statement application work automatically retry-safe.

## ADO.NET Limitations

Do not assume every ADO.NET abstraction is implemented.

Limitations to remember:

- `DbConnection.GetSchema()` is not implemented.
- `System.Transactions` is not supported.
- `DbDataAdapter` is not implemented for updates; `DataSet` and `DataTable` can only be loaded.
- SQLite has no output parameters.
- Positional parameters are not supported by `Microsoft.Data.Sqlite`.
- SQLite has no stored procedures.
- `Chaos` and `Snapshot` isolation levels are unsupported.
- Authorization callbacks, data change notifications, and virtual table module creation are not exposed by `Microsoft.Data.Sqlite`.

## Review Checklist

When reviewing SQLite C# code, check:

- Is `Microsoft.Data.Sqlite` the right package, or does the app need `Microsoft.Data.Sqlite.Core` plus a custom SQLitePCLRaw bundle?
- Are connection strings built with `SqliteConnectionStringBuilder` when user input is involved?
- Are connections, commands, and readers scoped to one operation and not shared concurrently across threads?
- Are write-heavy paths using WAL where appropriate?
- Are bulk writes inside a transaction and reusing parameterized commands?
- Are command and default timeouts deliberate for concurrent access?
- Are async SQLite APIs avoided unless needed only to satisfy a higher-level interface?
- Are schemas using `INTEGER`, `REAL`, `TEXT`, and `BLOB` instead of misleading custom type names?
- Are parameters named, case-consistent, and prefixed correctly for the data access library in use?
- Are readers disposed, especially for batched commands?
- Are Dapper type handlers present for `DateTimeOffset`, `Guid`, and `TimeSpan` result values?
- Are metadata queries using SQLite PRAGMA/table metadata instead of `GetSchema()`?
- Are large BLOBs streamed with `SqliteBlob` rather than materialized whole?
- Is encryption backed by an encryption-capable native SQLite library?

## Source Material Reviewed

This skill is distilled from every Markdown page under `dotnet/docs/docs/standard/data/sqlite` as of 2026-06-03:

- `adonet-limitations.md`
- `async.md`
- `backup.md`
- `batching.md`
- `blob-io.md`
- `bulk-insert.md`
- `collation.md`
- `compare.md`
- `connection-strings.md`
- `custom-versions.md`
- `dapper-limitations.md`
- `database-errors.md`
- `encryption.md`
- `extensions.md`
- `index.md`
- `in-memory-databases.md`
- `interop.md`
- `metadata.md`
- `parameters.md`
- `transactions.md`
- `types.md`
- `user-defined-functions.md`