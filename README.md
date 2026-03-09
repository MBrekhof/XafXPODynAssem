# XafXPODynAssem — AI-Powered Dynamic Assemblies for DevExpress XAF (XPO)

Runtime entity creation for DevExpress XAF applications using XPO persistence. Define new business object types, properties, and relationships at runtime — no recompilation or redeployment needed.

**Not EAV** — generates real CLR types via Roslyn with real SQL columns and FK constraints.

## How It Works

1. **Define** entity metadata (class name, fields, types) through the UI
2. **Deploy** — Roslyn compiles real C# classes inheriting from XPO `BaseObject`
3. **Use** — runtime entities appear in navigation, support full CRUD, validation, and relationships
4. **Restart** — exit code 42 protocol restarts the process so XAF picks up new types

## Quick Start

```bash
# Build
dotnet build XafXPODynAssem.slnx

# Run with auto-restart on schema deploy
run-server.bat

# Or run directly (no restart loop)
dotnet run --project XafXPODynAssem/XafXPODynAssem.Blazor.Server

# Update database
dotnet run --project XafXPODynAssem/XafXPODynAssem.Blazor.Server -- --updateDatabase
```

**Default credentials:** Admin / (empty password)

## Tech Stack

- .NET 8 / C#
- DevExpress XAF 25.2 + XPO
- Roslyn (`Microsoft.CodeAnalysis.CSharp` 4.10)
- SQL Server (localdb)
- Blazor Server
- SignalR (schema change notifications)

## Architecture

```
Metadata (CustomClass + CustomField)
    ↓ QueryMetadata (raw SQL)
Roslyn Compilation (real C# → real CLR types)
    ↓ AssemblyLoadContext
TypesInfo Registration → XPO UpdateSchema → XAF Views
    ↓ SignalR
Client Notification → Exit Code 42 → Process Restart
```

### Key Differences from EF Core Version

This is a port of [XafDynamicAssemblies](https://github.com/MBrekhof/XafDynamicAssemblies) (EF Core) to XPO:

| Concern | EF Core | XPO |
|---|---|---|
| Schema sync | Custom SchemaSynchronizer | XPO `UpdateSchema` (automatic) |
| Model cache | DynamicModelCacheKeyFactory | Not needed |
| Generated code | `virtual` auto-properties | Backing fields + `SetPropertyValue` |
| Constructor | Parameterless | `Session` constructor |
| References | FK ID + `[ForeignKey]` nav prop | Just typed property |
| Database | PostgreSQL | SQL Server (localdb) |

## Project Structure

```
XafXPODynAssem.Module/
  BusinessObjects/     CustomClass, CustomField (metadata)
  Services/            RuntimeAssemblyBuilder, AssemblyGenerationManager,
                       SchemaChangeOrchestrator, SupportedTypes
  Controllers/         SchemaChangeController (Deploy), CustomFieldDetailController
  Validation/          Name/type validation helpers

XafXPODynAssem.Blazor.Server/
  Startup.cs           Bootstrap wiring, SignalR, restart logic
  Program.cs           Exit code 42 restart protocol
  Hubs/                SchemaUpdateHub (SignalR)
  Services/            RestartService
```

## How to Implement (Step by Step)

See [`docs/plans/2026-03-09-xpo-dynamic-assemblies.md`](docs/plans/2026-03-09-xpo-dynamic-assemblies.md) for the full implementation plan with exact code for every task.

## Session Handoff

See [`SESSION_HANDOFF.md`](SESSION_HANDOFF.md) for current status, what was built, and known issues.

## License

This project uses DevExpress components which require a valid DevExpress license.
