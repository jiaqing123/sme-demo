# Declarative Schema Workshop

- **Status:** Proposal
- **Audience:** the team; nobody here has used state-based schema tooling except
  the author of this note
- **Time:** about half a day to prepare, 45 minutes to run

## 1. Why this exists

The team knows one way to change a database: edit the EF entity, generate a
migration, and let the application apply it. That works, and it is the default
for this demo.

State-based (declarative) schema management is the other family: you declare
what the schema should look like, and a tool works out the difference against
the target database and applies it. It is worth understanding because it is what
most organisations with a shared database, a DBA function, or several
applications on one schema actually use.

The goal of this workshop is not to convert the project. It is to let everyone
see the difference on a real schema, in 45 minutes, with no risk to the demo.

## 2. The rule that keeps the demo safe

**The product demo stays on EF migrations. The declarative schema is a second,
parallel track.**

| Track | Owner of the schema | Used by |
|-------|--------------------|---------|
| A — default | EF Core migrations | the app, the demo, acceptance tests |
| B — workshop | `.sqlproj` compiled to a `.dacpac` | a separate database, `sme_declarative`, on the same SQL Server container |

Track B never feeds the running app in the default compose file. It exists to
be inspected, published, and compared. If the team later decides to adopt
state-based for real, the move is deliberate: stop EF migrations, baseline the
schema into the project, and scaffold entities from the database.

This containment matters. Two hand-maintained definitions of one schema drift,
and drift is discovered at the worst moment. Track B is kept honest by a CI
drift check (§5), not by good intentions.

## 3. What the workshop shows

1. A schema expressed as SQL files instead of C# builder calls.
2. A build artefact (`.dacpac`) that can be reviewed, versioned, and deployed.
3. A **deployment report**: the exact operations the tool intends to run, before
   it runs them.
4. The same change made both ways, side by side.
5. A destructive change being refused by a guard rather than executed.

## 4. Setup

```
db/declarative/
  SmeDemo.Database.sqlproj
  Tables/
    cards.sql
    issuer_sync.sql
  Scripts/            (optional post-deployment, not used here)
```

- SDK-style project (`Microsoft.Build.Sql`), so `dotnet build` works on Linux
  and in CI, not only in Visual Studio.
- Scope: `app.cards` and `app.issuer_sync` only. Leave `auth.users` out; it
  belongs to another service and would blur the ownership lesson.
- Publish with the `mcr.microsoft.com/sqlpackage/sqlpackage` image:

```
SqlPackage /Action:Publish \
  /SourceFile:bin/Debug/SmeDemo.Database.dacpac \
  /TargetConnectionString:"Server=mssql;Database=sme_declarative;User Id=sa;..." \
  /p:CreateNewDatabase=True \
  /p:BlockOnPossibleDataLoss=True
```

- Preview instead of executing:

```
SqlPackage /Action:DeployReport ...   # human-readable list of operations
SqlPackage /Action:Script ...         # the SQL that would run
```

## 5. Keeping the two tracks honest

After a publish, compare the declarative database against the migration-built
one:

```
SqlPackage /Action:DriftReport \
  /SourceFile:...dacpac \
  /TargetConnectionString:"...Database=sme_api..."
```

An empty drift report means the two definitions agree. This check runs in CI;
if the two tracks diverge, the build fails. That check is itself a teaching
point: it is the price of maintaining a second representation.

## 6. The 45-minute walkthrough

**0–10 min — the problem.** Show a migration that renames a column in C#. Ask:
"without opening the generated script, what SQL does this run? Does it move the
data?" Most people cannot answer. That is the gap state-based tooling closes.

**10–20 min — declare and publish.** Build the `.dacpac`, publish it to an empty
`sme_declarative`, and show the deploy report. Point out that the report lists
every create, every index, and every constraint, in order.

**20–30 min — the same change, both ways.** Add a `nickname` column.

| | Migration track | Declarative track |
|---|---|---|
| Edit | C# entity property | one line in `cards.sql` |
| Produce | `dotnet ef migrations add` | rebuild `.dacpac` |
| Review | generated `Up()` + SQL script | the SQL diff in the pull request |
| Apply | migrator container | `SqlPackage /Action:Publish` |

Both end with the same column. The difference is what a reviewer has to read.

**30–40 min — the destructive change.** Drop the column, and show
`BlockOnPossibleDataLoss` refusing to publish without an explicit override. Then
show what the migration track would have produced: a `DROP COLUMN` statement
with no guard. Neither tool is safe by default; one of them asks.

**40–45 min — who owns the schema?** The honest summary: state-based moves
ownership from C# to SQL. It pays off when the database is a contract between
teams. It costs you the code-first loop and adds a toolchain. This project, with
one application and one owner, is the wrong place to adopt it — which is exactly
why it is a good place to learn it.

## 7. Talking points to have ready

- "Is declarative better?" No. It is better at a specific job: when several
  consumers need to agree on a schema, or when the schema is the deliverable.
- "Do we lose EF?" No. EF becomes a consumer: `dotnet ef dbcontext scaffold`
  generates entities from the database. The development loop changes, not the
  runtime.
- "What about data migrations?" State-based tools handle schema, not data.
  Backfills remain hand-written, batched, and reviewed — same as today.
- "Why not both?" Because two definitions drift. Pick an owner.

## 8. What adopting it for real would require

1. SQL literacy on the team, at least for review.
2. A CI runner that can build a `.sqlproj` (the SDK-style project handles this).
3. A reason: shared database, several consumers, or an external DBA.
4. Someone who owns the database project and reviews every change to it.

Without (3), the tooling is ceremony. With it, the tooling is the contract.

## 9. Cost

About two to three days: half a day to mirror two tables, half a day to wire the
publish and drift check, one day for the walkthrough script and rehearsal. The
application code is untouched, and the demo remains shippable at every point.
