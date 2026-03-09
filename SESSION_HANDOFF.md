# Session Handoff — XafXPODynAssem

## Current Status: Core Implementation Complete — Build Passes, Needs Runtime Testing

### Session 2026-03-09 — Initial Implementation (Port from EF Core)

**What was built:**

All core dynamic assembly infrastructure ported from the EF Core version (`C:\Projects\XafDynamicAssemblies`):

1. **Metadata Business Objects** (XPO)
   - `CustomClass` — runtime entity definition with Status lifecycle (Runtime/Graduating/Compiled)
   - `CustomField` — field definitions with type validation, XAF attributes, reference support
   - Both use XPO patterns: `Session` constructor, `SetPropertyValue`, `XPCollection<T>`
   - Validation rules via `RuleFromBoolProperty`
   - `DetachedFields`/`AllFields` pattern for DTO usage in `QueryMetadata`

2. **Roslyn Compilation**
   - `RuntimeAssemblyBuilder` — generates XPO-style C# source and compiles via Roslyn
   - `AssemblyGenerationManager` — manages collectible ALC lifecycle
   - `CollectibleLoadContext` — custom ALC (non-collectible for now)
   - Generated code uses: `BaseObject` base class, `Session` constructor, backing fields, `SetPropertyValue`

3. **Orchestration**
   - `SchemaChangeOrchestrator` — semaphore-guarded hot-load: compile → register → restart
   - `RestartService` — graceful shutdown signal
   - `SchemaUpdateHub` — SignalR hub for client notifications
   - Exit code 42 protocol in `Program.cs` + `run-server.bat`

4. **Module Integration**
   - `Module.cs` — `EarlyBootstrap()`, `BootstrapRuntimeEntities()`, `QueryMetadata()`, `RefreshRuntimeTypes()`
   - `QueryMetadata` reads directly via `SqlClient` (before XAF initializes)
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
- **DetachedFields pattern** — XPO `XPCollection` requires a Session; detached objects (from `QueryMetadata`) use a plain `List<CustomField>` via `DetachedFields`/`AllFields` accessor

**Files created/modified:**
- `Module/BusinessObjects/CustomClass.cs` — NEW
- `Module/BusinessObjects/CustomField.cs` — NEW
- `Module/Services/RuntimeAssemblyBuilder.cs` — NEW
- `Module/Services/AssemblyGenerationManager.cs` — NEW
- `Module/Services/SchemaChangeOrchestrator.cs` — NEW
- `Module/Services/SupportedTypes.cs` — NEW
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

## How to Build & Run

```bash
dotnet build XafXPODynAssem.slnx
run-server.bat
```

Login as Admin (empty password), navigate to Schema Management, create a CustomClass with fields, click Deploy Schema.

## How to Verify

```bash
# Build check
dotnet build XafXPODynAssem.slnx

# Runtime test (manual)
# 1. Start via run-server.bat
# 2. Login as Admin
# 3. Navigate to Schema Management > Custom Class
# 4. Create class "TestEntity" with NavigationGroup "Test"
# 5. Add field "Name" (System.String), "Amount" (System.Decimal)
# 6. Click Deploy Schema
# 7. Server restarts, TestEntity appears in navigation
# 8. Create a TestEntity record, verify fields work
```

## Known Issues / Not Yet Tested

- Runtime entity creation has not been tested end-to-end yet (build passes but no runtime verification)
- XPO `UpdateSchema` behavior with Roslyn-compiled types needs verification
- `new CustomClass(null)` / `new CustomField(null)` in `QueryMetadata` — works as DTO but may log XPO warnings
- Non-collectible ALC — types persist in memory across hot-loads (works with process restart)
- Server MUST be started via `run-server.bat` for deploy+restart to work

## Not Yet Implemented (from EF Core version)

- Web API (OData) endpoints
- AI Chat (AIChatService, SchemaAIToolsProvider)
- Schema Export/Import
- Graduation (GraduationService, GraduateController)
- SchemaHistory audit trail
- SchemaDiscoveryService
- Playwright tests
