# Session Handoff — XafXPODynAssem

## Current Status: Runtime Entity System Fully Working — End-to-End Verified

### Session 2026-03-09 — Initial Implementation + Runtime Verification

**What was built:**

All core dynamic assembly infrastructure ported from the EF Core version (`C:\Projects\XafDynamicAssemblies`):

1. **Metadata Business Objects** (XPO)
   - `CustomClass` — runtime entity definition with Status lifecycle (Runtime/Graduating/Compiled)
   - `CustomField` — field definitions with type validation, XAF attributes, reference support
   - Both use XPO patterns: `Session` constructor, `SetPropertyValue`, `XPCollection<T>`
   - Validation rules via `RuleFromBoolProperty`

2. **Roslyn Compilation**
   - `RuntimeAssemblyBuilder` — generates XPO-style C# source and compiles via Roslyn
   - `AssemblyGenerationManager` — manages collectible ALC lifecycle
   - `CollectibleLoadContext` — custom ALC (non-collectible for now)
   - `RuntimeClassMetadata` / `RuntimeFieldMetadata` — lightweight DTOs for metadata (avoids XPO null Session crash)
   - Generated code uses: `BaseObject` base class, `Session` constructor, backing fields, `SetPropertyValue`

3. **Orchestration**
   - `SchemaChangeOrchestrator` — semaphore-guarded hot-load: compile → UpdateSchema → register → restart
   - `RestartService` — graceful shutdown signal
   - `SchemaUpdateHub` — SignalR hub for client notifications
   - Exit code 42 protocol in `Program.cs` + `run-server.bat`

4. **Module Integration**
   - `Module.cs` — `EarlyBootstrap()`, `BootstrapRuntimeEntities()`, `QueryMetadata()`, `RefreshRuntimeTypes()`
   - `QueryMetadata` reads directly via `SqlClient` (before XAF initializes), returns `RuntimeClassMetadata` DTOs
   - `EarlyBootstrap` calls `UpdateDatabaseSchema` after compilation to create tables before XAF initializes
   - Degraded mode support when compilation fails

5. **Controllers**
   - `SchemaChangeController` — "Deploy Schema" action on CustomClass ListView
   - `CustomFieldDetailController` — TypeName dropdown with SupportedTypes

6. **Host Wiring**
   - `Startup.cs` — early bootstrap, SignalR endpoint, orchestrator → exit code 42
   - `Program.cs` — restart flag check, exit code 42 return

**Key design decisions (XPO-specific):**
- **No SchemaSynchronizer** — XPO's `UpdateSchema` handles DDL automatically
- **No DynamicModelCacheKeyFactory** — XPO has no model cache to invalidate
- **DTO pattern** — `RuntimeClassMetadata`/`RuntimeFieldMetadata` used for `QueryMetadata` results (XPO's `PersistentBase(null)` crashes in 25.2)
- **Auto UpdateSchema** — `EarlyBootstrap` and `SchemaChangeOrchestrator` both call `UpdateDatabaseSchema` after Roslyn compilation to ensure tables exist before XAF initializes

**Files created/modified:**
- `Module/BusinessObjects/CustomClass.cs` — NEW
- `Module/BusinessObjects/CustomField.cs` — NEW
- `Module/Services/RuntimeAssemblyBuilder.cs` — NEW
- `Module/Services/AssemblyGenerationManager.cs` — NEW
- `Module/Services/SchemaChangeOrchestrator.cs` — NEW (includes UpdateDatabaseSchema)
- `Module/Services/SupportedTypes.cs` — NEW
- `Module/Services/RuntimeClassMetadata.cs` — NEW (DTO for metadata)
- `Module/Validation/CustomClassValidation.cs` — NEW
- `Module/Validation/CustomFieldValidation.cs` — NEW
- `Module/Controllers/SchemaChangeController.cs` — NEW
- `Module/Controllers/CustomFieldDetailController.cs` — NEW
- `Module/Module.cs` — REPLACED (added bootstrap + QueryMetadata)
- `Blazor.Server/Startup.cs` — REPLACED (added bootstrap + SignalR + restart)
- `Blazor.Server/Program.cs` — REPLACED (added exit code 42)
- `Blazor.Server/Services/RestartService.cs` — NEW
- `Blazor.Server/Hubs/SchemaUpdateHub.cs` — NEW
- `run-server.bat` — NEW

## End-to-End Verification (Playwright)

All of the following have been verified via automated Playwright tests:

1. Login as Admin (empty password)
2. Navigate to Schema Management > Custom Class
3. Create class "Invoice" with NavigationGroup "Billing"
4. Add field "InvoiceNumber" (System.String)
5. Click Deploy Schema → confirmation dialog → Yes
6. Server shuts down (exit code 42)
7. Restart server → EarlyBootstrap compiles Invoice → UpdateSchema creates table
8. "Billing" navigation group appears with "Invoice" entity
9. Invoice list view shows columns: Invoice Number, Amount, Invoice Date
10. Create record INV-002 → persisted to database
11. Database has Invoice table with: Oid, InvoiceNumber (nvarchar), Amount (money), InvoiceDate (datetime)

## How to Build & Run

```bash
dotnet build XafXPODynAssem.slnx
run-server.bat
```

Login as Admin (empty password), navigate to Schema Management, create a CustomClass with fields, click Deploy Schema.

## Bugs Found and Fixed

1. **XPO null Session crash** — `new CustomClass(null)` in QueryMetadata crashes with NullReferenceException in XPO 25.2's `PersistentBase(Session)`. Fixed by creating `RuntimeClassMetadata`/`RuntimeFieldMetadata` DTO classes.

2. **"Schema needs to be updated" on first load** — After Roslyn compilation, the database tables didn't exist yet. XAF tried to use the new types but XPO threw "Schema needs to be updated". Fixed by calling `UpdateDatabaseSchema()` in both `EarlyBootstrap` and `SchemaChangeOrchestrator`.

## Known Limitations

- Non-collectible ALC — types persist in memory across hot-loads (works with process restart)
- Server MUST be started via `run-server.bat` for deploy+restart to work
- XAF security: Admin role has full access; custom roles need explicit type permissions for runtime entities

## All Features Ported from EF Core Version

All features from the EF Core version have been ported:

- **SchemaHistory** — audit trail for schema changes (export/import/deploy)
- **Graduation** — generate production XPO source code, mark entities as Compiled
- **SchemaDiscoveryService** — type introspection, AI system prompt generation
- **Schema Export/Import** — JSON serialization with smart merge, Blazor file download/upload
- **Web API (OData)** — endpoints at `/api/odata` with Swagger, dynamic runtime entity exposure
- **AI Chat** — LLM-powered schema management with 10 tool-calling functions (LlmTornado)

## Needs Runtime Testing

The new features build successfully but have not been runtime-tested yet:
- AI Chat requires `AI:ApiKey` configuration in appsettings.json
- Web API/Swagger at `/swagger` and `/api/odata`
- Export/Import needs JS interop functions for file upload/download
- Graduation generate + status change flow
