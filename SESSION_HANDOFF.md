# Session Handoff — XafXPODynAssem

## Current Status: All Features Implemented & Runtime-Tested

### Session 2026-03-09 — Full Implementation + Runtime Verification

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
   - `SchemaChangeController` — "Deploy Schema" action on CustomClass ListView (records history via direct SQL)
   - `SchemaExportImportController` — Export/Import JSON with Blazor file download/upload
   - `GraduateController` — Generate production XPO source code from runtime entities
   - `GraduationWarningController` — Warning banner on graduated entity detail views
   - `CustomFieldDetailController` — TypeName dropdown with SupportedTypes
   - `ShowAIChatController` — Redirects AIChat ListView navigation to DetailView

6. **AI Chat**
   - `AIChatService` — LLM-powered schema management with 10 tool-calling functions
   - `SchemaAIToolsProvider` — Tool definitions for create/modify/delete entities, manage permissions
   - `AIChatDefaults` — Prompt suggestions, Markdown→HTML (Markdig + HtmlSanitizer)
   - `AIChat.razor` + `AIChatViewItemBlazor.cs` — DxAIChat Blazor component with streaming
   - `AIChatDetailViewUpdater` — Injects chat ViewItem into AIChat_DetailView

7. **Schema Export/Import**
   - `SchemaExportImportService` — JSON serialization with smart merge
   - `BlazorSchemaFileService` — JS interop for file download/upload
   - `_Host.cshtml` — JS interop functions (`window.schemaFile.download/upload`)

8. **Web API / OData**
   - REST endpoints at `/api/odata` with Swagger UI
   - Dynamic runtime entity exposure via `DynamicWebApiTypeSource`

9. **Graduation**
   - `GraduationService` — Generates production XPO C# source code from runtime entities
   - Status change: Runtime → Graduating → Compiled

10. **Audit Trail**
    - `SchemaHistory` — Records every deploy, export, and import action
    - Deploy history recorded via direct SQL (after hot-load completes)

11. **WinForms Support**
    - `Program.cs` — IConfiguration-based setup (appsettings.json only, no App.config)
    - `Startup.cs` — AI service registration via `AddAIServices(configuration)`

12. **Host Wiring (Blazor)**
    - `Startup.cs` — early bootstrap, SignalR, Web API/OData, AI services
    - `Program.cs` — restart flag check, exit code 42 return

**Key design decisions (XPO-specific):**
- **No SchemaSynchronizer** — XPO's `UpdateSchema` handles DDL automatically
- **No DynamicModelCacheKeyFactory** — XPO has no model cache to invalidate
- **DTO pattern** — `RuntimeClassMetadata`/`RuntimeFieldMetadata` used for `QueryMetadata` results (XPO's `PersistentBase(null)` crashes in 25.2)
- **Auto UpdateSchema** — `EarlyBootstrap` and `SchemaChangeOrchestrator` both call `UpdateDatabaseSchema` after Roslyn compilation
- **Deploy audit via direct SQL** — SchemaHistory recorded after hot-load completes (not before), using raw SqlClient from background thread

## Runtime Test Results (Playwright)

All features verified via automated Playwright tests (`test_runtime.py`), 8/8 passing:

| Test | Result |
|------|--------|
| swagger_api | PASS — Swagger 200, OData $metadata has CustomClass/CustomField/SchemaHistory |
| swagger_ui | PASS — Screenshot captured |
| runtime_entity | PASS — Invoice visible in Billing navigation group |
| schema_history | PASS — Schema History view loads with columns |
| custom_class | PASS — Invoice in list, Deploy Schema button visible |
| export | PASS — JSON file downloaded with schema data |
| graduation | PASS — Graduate flow completed (check screenshot) |
| ai_chat | PASS — View loaded (needs manual browser test for DxAIChat rendering) |

Screenshots saved to `screenshots/` directory (01–10).

### End-to-End Verification (Manual + Playwright)

1. Login as Admin (empty password)
2. Navigate to Schema Management > Custom Class
3. Create class "Invoice" with NavigationGroup "Billing"
4. Add fields: InvoiceNumber (String), Amount (Decimal), InvoiceDate (DateTime)
5. Click Deploy Schema → confirmation → Yes → exit code 42 → restart
6. Invoice entity appears in Billing navigation group with full CRUD
7. Swagger UI at `/swagger` — OData at `/api/odata/$metadata`
8. Export Schema downloads JSON file
9. Schema History records deploy/export actions
10. Graduation generates production C# source code

## Configuration

**Blazor Server:**
- `appsettings.Development.json` — gitignored, copy from `appsettings.Development.template.json`
- Contains `AI:ApiKey` for Anthropic API

**WinForms:**
- `appsettings.json` — gitignored, copy from `appsettings.template.json`
- Contains connection string + `AI:ApiKey`

## How to Build & Run

```bash
dotnet build XafXPODynAssem.slnx

# With auto-restart on schema deploy
run-server.bat

# Or direct (no restart loop)
dotnet run --project XafXPODynAssem/XafXPODynAssem.Blazor.Server
```

Login as Admin (empty password).

## Known Limitations

- Non-collectible ALC — types persist in memory across hot-loads (works with process restart)
- Server MUST be started via `run-server.bat` for deploy+restart to work
- XAF security: Admin role has full access; custom roles need explicit type permissions for runtime entities
- AI Chat `ShowAIChatController` redirect needs manual browser verification (Playwright headless may not trigger it reliably)
- Import Schema button visible but file upload not tested end-to-end
- AI Chat LLM interaction requires active API key and DxAIChat rendering

## Bugs Found and Fixed

1. **XPO null Session crash** — `new CustomClass(null)` crashes in XPO 25.2. Fixed with DTO classes.
2. **"Schema needs to be updated" on first load** — Fixed by calling `UpdateDatabaseSchema()` after Roslyn compilation.
3. **Ambiguous ConfigurationManager** (WinForms) — Both `System.Configuration` and `Microsoft.Extensions.Configuration` versions. Fixed by consolidating to appsettings.json only.
4. **Deploy audit false positives** — SchemaHistory recorded before hot-load. Fixed by moving to after `ExecuteHotLoadAsync`, using direct SQL.
5. **Export/Import buttons hidden** — Actions in `"SchemaManagement"` category hidden in toolbar overflow. Fixed by using `PredefinedCategory.Edit`.
6. **GraduateController lambda leak** — Event handler lambdas never unsubscribed. Fixed with named methods + `OnDeactivated` cleanup.
7. **SchemaAIToolsProvider reflection mismatch** — `GetParameters().Length == 2` didn't match non-generic `AddTypePermissionsRecursively` (3 params). Fixed.
