# XafXPODynAssem — AI-Powered Dynamic Assemblies for DevExpress XAF (XPO)

Runtime entity creation for DevExpress XAF applications using XPO persistence. Define new business object types, properties, and relationships at runtime — no recompilation or redeployment needed.

**Not EAV** — generates real CLR types via Roslyn with real SQL columns and FK constraints.

## Features

- **Runtime entity creation** — define classes and fields through the XAF UI, deploy with one click
- **AI Chat** — LLM-powered schema management with 10 tool-calling functions (create/modify/delete entities, manage permissions)
- **Web API / OData** — REST endpoints at `/api/odata` with Swagger UI for runtime and compiled entities
- **Schema Export/Import** — JSON serialization with smart merge for backup and migration
- **Graduation** — generate production XPO C# source code from runtime entities
- **Audit Trail** — `SchemaHistory` records every deploy, export, and import action
- **WinForms + Blazor** — both platforms supported with shared Module

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

### AI Chat Setup

Copy the template and add your API key:

```bash
# Blazor Server
cp XafXPODynAssem/XafXPODynAssem.Blazor.Server/appsettings.Development.template.json \
   XafXPODynAssem/XafXPODynAssem.Blazor.Server/appsettings.Development.json

# WinForms
cp XafXPODynAssem/XafXPODynAssem.Win/appsettings.template.json \
   XafXPODynAssem/XafXPODynAssem.Win/appsettings.json
```

Edit the copied files and replace `YOUR_API_KEY_HERE` with your Anthropic API key. These files are gitignored.

## Tech Stack

- .NET 8 / C#
- DevExpress XAF 25.2 + XPO
- Roslyn (`Microsoft.CodeAnalysis.CSharp` 4.10)
- SQL Server (localdb)
- Blazor Server + WinForms
- SignalR (schema change notifications)
- LlmTornado (multi-provider LLM client for AI Chat)
- OData / Swagger (Web API)

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
  BusinessObjects/     CustomClass, CustomField, SchemaHistory, AIChat
  Services/            RuntimeAssemblyBuilder, AssemblyGenerationManager,
                       SchemaChangeOrchestrator, SupportedTypes,
                       GraduationService, SchemaExportImportService,
                       SchemaDiscoveryService, AIChatService,
                       SchemaAIToolsProvider, TornadoApiProvider
  Controllers/         SchemaChangeController (Deploy),
                       SchemaExportImportController (Export/Import),
                       GraduateController, GraduationWarningController,
                       CustomFieldDetailController
  Validation/          Name/type validation helpers

XafXPODynAssem.Blazor.Server/
  Startup.cs           Bootstrap wiring, SignalR, Web API/OData, AI services
  Program.cs           Exit code 42 restart protocol
  Hubs/                SchemaUpdateHub (SignalR)
  Services/            RestartService, BlazorSchemaFileService

XafXPODynAssem.Win/
  Startup.cs           WinForms app builder with AI services
  Program.cs           IConfiguration loading, EarlyBootstrap
```

## How to Implement (Step by Step)

See [`docs/HOW_TO_IMPLEMENT.md`](docs/HOW_TO_IMPLEMENT.md) for a practical guide on adding runtime entity creation to your own XAF+XPO project.

## Session Handoff

See [`SESSION_HANDOFF.md`](SESSION_HANDOFF.md) for current status, what was built, and known issues.

## License

This project uses DevExpress components which require a valid DevExpress license.
