# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AI-powered dynamic assemblies system for DevExpress XAF (XPO). Port of the EF Core version (`C:\Projects\XafDynamicAssemblies`). Enables runtime entity creation — new business object types, properties, and relationships defined at runtime without recompilation. Uses Roslyn for in-process C# compilation and collectible `AssemblyLoadContext` for hot-loading.

**Not EAV** — generates real CLR types with real SQL columns and FK constraints.

## Build & Run

```bash
# Solution file (new .slnx format)
dotnet build XafXPODynAssem.slnx

# Run the Blazor Server app (with restart loop)
run-server.bat

# Or run directly (no restart loop)
dotnet run --project XafXPODynAssem/XafXPODynAssem.Blazor.Server

# Update database via CLI
dotnet run --project XafXPODynAssem/XafXPODynAssem.Blazor.Server -- --updateDatabase

# Build configurations: Debug, Release, EasyTest
dotnet build XafXPODynAssem.slnx -c EasyTest
```

## Tech Stack

- **.NET 8** / C#, DevExpress XAF 25.2, **XPO** (not EF Core)
- **Roslyn** (`Microsoft.CodeAnalysis.CSharp` 4.10) for runtime compilation
- **SQL Server (localdb)**: `(localdb)\mssqllocaldb`, db `XafXPODynAssem`
- **Blazor Server** (UI)

## Architecture

### Solution Structure

```
XafXPODynAssem.Module/          # Shared module — all business logic lives here
  BusinessObjects/              # XPO persistent classes
  Controllers/                  # XAF ViewControllers
  DatabaseUpdate/               # XAF database updater
  Services/                     # Runtime compilation, orchestration
  Validation/                   # Name/type validation helpers
  Module.cs                     # XafXPODynAssemModule — bootstrap, QueryMetadata

XafXPODynAssem.Blazor.Server/   # Blazor Server host
  Startup.cs                    # DI, XAF builder, XPO provider config
  Program.cs                    # Exit code 42 restart loop
  Hubs/SchemaUpdateHub.cs       # SignalR for client notification
  Services/RestartService.cs    # Graceful restart signal
```

### Core Pattern: Dynamic Entity System

Two metadata tables drive everything:

- `CustomClass` (ClassName, NavigationGroup, Description, Status)
- `CustomField` (CustomClass [FK], FieldName, TypeName, IsDefaultField, Description)

**Startup sequence:** Query metadata → Roslyn compiles all runtime classes → ALC loads → TypesInfo registers → XPO UpdateSchema creates tables → XAF views auto-generated.

**Hot-load (restart):** Roslyn validates → exit code 42 → process restart → fresh compilation → XPO schema update → ready.

### Key XPO Differences from EF Core Version

- No SchemaSynchronizer — XPO's `UpdateSchema` handles DDL automatically
- No `DynamicModelCacheKeyFactory` — XPO has no model cache to invalidate
- No `DbContext.RuntimeEntityTypes` — XPO discovers types via TypesInfo
- Generated classes use `Session` constructor + `SetPropertyValue` pattern
- References are just typed properties (no FK ID property needed)
- QueryMetadata uses `Microsoft.Data.SqlClient` instead of Npgsql
- Enum storage: integers (0=Runtime, 1=Graduating, 2=Compiled)
- PK column: `Oid` (not `ID`)
- Soft delete: `GCRecord IS NULL` (not deleted)
- `DetachedFields`/`AllFields` pattern for DTO objects created with null Session

### Key Implementation Classes

| Class | Responsibility |
|---|---|
| `RuntimeAssemblyBuilder` | Generates XPO C# source per CustomClass, Roslyn-compiles into one assembly |
| `AssemblyGenerationManager` | Manages versioned collectible ALCs, drain/unload/load lifecycle |
| `SchemaChangeOrchestrator` | Coordinates hot-load: compile → register → restart via exit code 42 |

## XAF + XPO Conventions

- Business objects derive from `BaseObject` (XPO path: `DevExpress.Persistent.BaseImpl.BaseObject`)
- All XPO objects need `Session` constructor: `public Foo(Session s) : base(s) { }`
- Properties use backing field + `SetPropertyValue(nameof(X), ref x, value)` pattern
- Collections use `XPCollection<T>` via `GetCollection<T>(nameof(X))`
- Module registration pattern: `RequiredModuleTypes.Add(typeof(...))` in Module constructor
- Database auto-updates when debugger is attached; throws version mismatch error in production
- Connection string key: `ConnectionString` in `appsettings.json`

## Session Handoff

See `SESSION_HANDOFF.md` for current status, what was done, known issues, and next steps.

## File Locations

- Entities: `Module/BusinessObjects/CustomClass.cs`, `CustomField.cs`
- Runtime assembly: `Module/Services/RuntimeAssemblyBuilder.cs`, `AssemblyGenerationManager.cs`
- Orchestration: `Module/Services/SchemaChangeOrchestrator.cs`
- Module bootstrap: `Module/Module.cs` (EarlyBootstrap, QueryMetadata, BootstrapRuntimeEntities)
- Controllers: `Module/Controllers/SchemaChangeController.cs`, `CustomFieldDetailController.cs`
- Validation: `Module/Validation/CustomClassValidation.cs`, `CustomFieldValidation.cs`
- Type mapping: `Module/Services/SupportedTypes.cs`
- Restart: `Blazor.Server/Services/RestartService.cs`, `Blazor.Server/Program.cs` (exit code 42)
- SignalR: `Blazor.Server/Hubs/SchemaUpdateHub.cs`
