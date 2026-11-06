# SME Demo

A demo system for small and medium-sized businesses, built as three layers:

- **Front-end UI** — the interface people actually use.
- **API service** — business logic, authentication, and integration.
- **Data storage** — persistence for business data.

## Stack

- **Front end:** React + TypeScript (demo UI), English, mobile-first.
- **API:** ASP.NET Core, three-tier with MediatR and AutoMapper.
- **Auth:** a separate ASP.NET Core JWT issuer service.
- **Data:** SQL Server, plus Azure Table and Blob Storage via Azurite — all in
  Docker, offline.

## Status

Requirements and design. No code yet.

The first milestone is an agreed requirements document covering the user
roles, core workflows, data model, and non-functional constraints.

## Repository layout

```
docs/requirements.md                System requirements and architecture
docs/licensing.md                   Third-party licensing notes for management review
docs/declarative-schema-workshop.md 45-minute walkthrough of state-based schema tooling
```

## Audience

Internal demo and discussion material for the product team, management, and
colleagues from other groups.
