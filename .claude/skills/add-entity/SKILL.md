---
name: add-entity
description: Add a domain entity end-to-end — entity, EF config, migration, seed, service stub, test skeleton — then schema review. Usage: /add-entity Ticket
---

Add the entity named in `$ARGUMENTS` following the ROADMAP §3 parity matrix (check the matching osTicket subsystem for the concept checklist — never copy code):

1. `src/RapidsolDestek.Domain/Entities/<Name>.cs` — POCO, Guid key, UTC `timestamptz` timestamps, no tenancy fields (single-tenant).
2. EF configuration in `src/RapidsolDestek.Infrastructure/` (`AppDbContext.OnModelCreating` or an `IEntityTypeConfiguration`), snake_case via conventions, deliberate FK delete behaviors, indexes for every FK + lookup column.
3. Migration: `dotnet ef migrations add Add<Name> -p src/RapidsolDestek.Infrastructure -s src/RapidsolDestek.Web -o Migrations` — NEVER hand-edit migration files.
4. Seed data in the seeder if the ROADMAP §2 canon defines fixtures for it.
5. Service stub in Domain (interface + minimal implementation) and a test skeleton via the **test-writer** agent.
6. Launch the **schema-reviewer** agent on the result; apply CHANGES REQUIRED findings and re-review until APPROVE.
7. `/verify`, then commit as one slice: `S3: entity <Name>`.
