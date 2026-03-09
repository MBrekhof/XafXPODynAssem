# How to Implement Dynamic Runtime Entities in XAF + XPO

A practical guide for adding user-defined business objects at runtime to a DevExpress XAF application using XPO persistence. No recompilation or redeployment required.

This approach generates **real CLR types** via Roslyn compilation -- not EAV (Entity-Attribute-Value) tables. Each runtime entity gets its own SQL table with real columns and foreign key constraints.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Architecture Overview](#2-architecture-overview)
3. [Step-by-Step: Add to Your Project](#3-step-by-step-add-to-your-project)
   - [3.1 Add the NuGet Package](#31-add-the-nuget-package)
   - [3.2 Create the Metadata Business Objects](#32-create-the-metadata-business-objects)
   - [3.3 Create the Service Classes](#33-create-the-service-classes)
   - [3.4 Wire Up Module.cs](#34-wire-up-modulecs)
   - [3.5 Wire Up Startup.cs](#35-wire-up-startupcs)
   - [3.6 Wire Up Program.cs](#36-wire-up-programcs)
   - [3.7 Add the Deploy Schema Controller](#37-add-the-deploy-schema-controller)
   - [3.8 Add the Restart Wrapper Script](#38-add-the-restart-wrapper-script)
4. [Key XPO Differences from EF Core](#4-key-xpo-differences-from-ef-core)
5. [Customization Points](#5-customization-points)
6. [Troubleshooting](#6-troubleshooting)
7. [Limitations](#7-limitations)
8. [Additional Features](#8-additional-features)

---

## 1. Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| .NET | 8.0+ | |
| DevExpress XAF | 25.2+ | XPO persistence (not EF Core) |
| SQL Server | Any (LocalDB works) | Metadata tables stored here |
| `Microsoft.CodeAnalysis.CSharp` | 4.10+ | Roslyn compiler for runtime code generation |

Install the Roslyn NuGet in your **Module** project:

```
dotnet add package Microsoft.CodeAnalysis.CSharp --version 4.10.0
```

You also need the `Microsoft.Data.SqlClient` package in your Module project for early metadata queries:

```
dotnet add package Microsoft.Data.SqlClient
```

---

## 2. Architecture Overview

The system has five stages that run in order:

```
1. METADATA          User defines classes/fields via XAF UI
   (CustomClass +      Stored in SQL as regular XPO objects
    CustomField)
        |
        v
2. ROSLYN COMPILE    C# source generated from metadata,
   (RuntimeAssembly    compiled in-memory to a real .NET assembly
    Builder)
        |
        v
3. ASSEMBLY LOAD     Assembly loaded into a custom AssemblyLoadContext
   (AssemblyGeneration  Types extracted and tracked
    Manager)
        |
        v
4. XPO REGISTRATION  Types registered in XafTypesInfo,
   (SchemaChange        XPO UpdateSchema creates/alters SQL tables,
    Orchestrator)       types added to Module.AdditionalExportedTypes
        |
        v
5. RESTART           Exit code 42 signals the wrapper script
   (Program.cs +       to restart the process; on next boot,
    run-server.bat)    EarlyBootstrap recompiles and registers
                       types before XAF initializes
```

### Why a restart?

XAF builds its internal model (Application Model, navigation, views) once during startup. Adding types after that point does not create views or navigation entries. The exit-code-42 restart protocol ensures that on the next startup, all runtime types are compiled and registered **before** `AddXaf()` runs, so XAF sees them as first-class citizens.

### Component Responsibilities

| Component | Location | Role |
|---|---|---|
| `CustomClass` | Module/BusinessObjects | Metadata: defines a runtime entity (name, nav group, status) |
| `CustomField` | Module/BusinessObjects | Metadata: defines a field on a runtime entity (name, type, attributes) |
| `RuntimeClassMetadata` | Module/Services | Lightweight DTO for passing metadata outside XPO Session context |
| `RuntimeAssemblyBuilder` | Module/Services | Generates C# source from metadata; compiles via Roslyn |
| `AssemblyGenerationManager` | Module/Services | Manages the AssemblyLoadContext lifecycle; holds compiled types |
| `SchemaChangeOrchestrator` | Module/Services | Coordinates hot-load: compile, register, signal restart |
| `SupportedTypes` | Module/Services | Whitelist of CLR types allowed for fields |
| `RestartService` | Blazor.Server/Services | Signals IHostApplicationLifetime to stop; sets restart flag |
| `SchemaUpdateHub` | Blazor.Server/Hubs | SignalR hub to notify connected clients of schema changes |
| `SchemaChangeController` | Module/Controllers | XAF action: "Deploy Schema" button on CustomClass list view |

---

## 3. Step-by-Step: Add to Your Project

### 3.1 Add the NuGet Package

In your `.Module` project (not the Blazor project):

```xml
<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.10.0" />
<PackageReference Include="Microsoft.Data.SqlClient" Version="5.2.2" />
```

### 3.2 Create the Metadata Business Objects

These are regular XPO business objects that store the definitions of your runtime entities. Users interact with these through the standard XAF UI.

#### CustomClass.cs

Place in `YourModule/BusinessObjects/CustomClass.cs`.

```csharp
using System.ComponentModel;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ConditionalAppearance;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl;
using DevExpress.Persistent.Validation;
using DevExpress.Xpo;

public enum CustomClassStatus
{
    Runtime = 0,     // Active runtime entity
    Graduating = 1,  // Being converted to compiled code (future use)
    Compiled = 2     // Has been graduated to static code (future use)
}

[DefaultClassOptions]
[NavigationItem("Schema Management")]
[DefaultProperty(nameof(ClassName))]
public class CustomClass : BaseObject
{
    public CustomClass(Session session) : base(session) { }

    string className;
    public string ClassName
    {
        get => className;
        set => SetPropertyValue(nameof(ClassName), ref className, value);
    }

    string navigationGroup;
    public string NavigationGroup
    {
        get => navigationGroup;
        set => SetPropertyValue(nameof(NavigationGroup), ref navigationGroup, value);
    }

    string description;
    [Size(SizeAttribute.Unlimited)]
    public string Description
    {
        get => description;
        set => SetPropertyValue(nameof(Description), ref description, value);
    }

    CustomClassStatus status;
    public CustomClassStatus Status
    {
        get => status;
        set => SetPropertyValue(nameof(Status), ref status, value);
    }

    [Association("CustomClass-Fields"), Aggregated]
    public XPCollection<CustomField> Fields => GetCollection<CustomField>(nameof(Fields));
}
```

**Property reference:**

| Property | Purpose |
|---|---|
| `ClassName` | The C# class name for the generated type. Must be a valid identifier. |
| `NavigationGroup` | XAF navigation group where this entity appears (e.g., "Sales", "HR"). |
| `Description` | Optional description for documentation purposes. |
| `Status` | Lifecycle state. Only `Runtime` (0) entities are compiled. |
| `Fields` | Aggregated collection of `CustomField` objects defining properties. |

#### CustomField.cs

Place in `YourModule/BusinessObjects/CustomField.cs`.

```csharp
using System.ComponentModel;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl;
using DevExpress.Persistent.Validation;
using DevExpress.Xpo;

[DefaultClassOptions]
[NavigationItem("Schema Management")]
[DefaultProperty(nameof(FieldName))]
public class CustomField : BaseObject
{
    public CustomField(Session session) : base(session) { }

    CustomClass customClass;
    [Association("CustomClass-Fields")]
    public CustomClass CustomClass
    {
        get => customClass;
        set => SetPropertyValue(nameof(CustomClass), ref customClass, value);
    }

    string fieldName;
    public string FieldName
    {
        get => fieldName;
        set => SetPropertyValue(nameof(FieldName), ref fieldName, value);
    }

    string typeName = "System.String";
    public string TypeName
    {
        get => typeName;
        set => SetPropertyValue(nameof(TypeName), ref typeName, value);
    }

    bool isRequired;
    public bool IsRequired
    {
        get => isRequired;
        set => SetPropertyValue(nameof(IsRequired), ref isRequired, value);
    }

    bool isDefaultField;
    public bool IsDefaultField
    {
        get => isDefaultField;
        set => SetPropertyValue(nameof(IsDefaultField), ref isDefaultField, value);
    }

    string description;
    public string Description
    {
        get => description;
        set => SetPropertyValue(nameof(Description), ref description, value);
    }

    string referencedClassName;
    public string ReferencedClassName
    {
        get => referencedClassName;
        set => SetPropertyValue(nameof(ReferencedClassName), ref referencedClassName, value);
    }

    int sortOrder;
    public int SortOrder
    {
        get => sortOrder;
        set => SetPropertyValue(nameof(SortOrder), ref sortOrder, value);
    }

    bool isImmediatePostData;
    public bool IsImmediatePostData
    {
        get => isImmediatePostData;
        set => SetPropertyValue(nameof(IsImmediatePostData), ref isImmediatePostData, value);
    }

    int? stringMaxLength;
    public int? StringMaxLength
    {
        get => stringMaxLength;
        set => SetPropertyValue(nameof(StringMaxLength), ref stringMaxLength, value);
    }

    bool isVisibleInListView = true;
    public bool IsVisibleInListView
    {
        get => isVisibleInListView;
        set => SetPropertyValue(nameof(IsVisibleInListView), ref isVisibleInListView, value);
    }

    bool isVisibleInDetailView = true;
    public bool IsVisibleInDetailView
    {
        get => isVisibleInDetailView;
        set => SetPropertyValue(nameof(IsVisibleInDetailView), ref isVisibleInDetailView, value);
    }

    bool isEditable = true;
    public bool IsEditable
    {
        get => isEditable;
        set => SetPropertyValue(nameof(IsEditable), ref isEditable, value);
    }

    string toolTip;
    public string ToolTip
    {
        get => toolTip;
        set => SetPropertyValue(nameof(ToolTip), ref toolTip, value);
    }

    string displayName;
    public string DisplayName
    {
        get => displayName;
        set => SetPropertyValue(nameof(DisplayName), ref displayName, value);
    }
}
```

**Property reference:**

| Property | Purpose |
|---|---|
| `FieldName` | C# property name on the generated class. Must be a valid identifier. |
| `TypeName` | CLR type name (e.g., `System.String`, `System.Int32`). Use `Reference` for FK relationships. |
| `IsRequired` | If true, value types are non-nullable. |
| `IsDefaultField` | Marks this field as the `[DefaultProperty]` for XAF (shown in lookups/references). |
| `ReferencedClassName` | For `Reference` type fields: the name of the target class. |
| `SortOrder` | Controls the display order of fields in generated views. |
| `IsImmediatePostData` | Adds `[ImmediatePostData]` attribute (triggers UI refresh on change). |
| `StringMaxLength` | For `System.String` fields: sets `[Size(N)]`. Null uses XPO default (100). |
| `IsVisibleInListView` | Controls `[VisibleInListView]` attribute. |
| `IsVisibleInDetailView` | Controls `[VisibleInDetailView]` attribute. |
| `IsEditable` | Controls `[Editable]` attribute. |
| `ToolTip` | Adds `[ToolTip]` attribute with the given text. |
| `DisplayName` | Adds `[DisplayName]` attribute to override the property name in UI. |

### 3.3 Create the Service Classes

#### RuntimeClassMetadata.cs

A lightweight DTO used to pass metadata between the raw SQL query and the Roslyn compiler. This exists because metadata must be read **before** XPO/XAF initializes (no Session available).

Place in `YourModule/Services/RuntimeClassMetadata.cs`.

```csharp
namespace YourApp.Module.Services
{
    public class RuntimeClassMetadata
    {
        public string ClassName { get; set; }
        public string NavigationGroup { get; set; }
        public string Description { get; set; }
        public List<RuntimeFieldMetadata> Fields { get; set; } = new();
    }

    public class RuntimeFieldMetadata
    {
        public string FieldName { get; set; }
        public string TypeName { get; set; } = "System.String";
        public bool IsRequired { get; set; }
        public bool IsDefaultField { get; set; }
        public string Description { get; set; }
        public string ReferencedClassName { get; set; }
        public int SortOrder { get; set; }
        public bool IsImmediatePostData { get; set; }
        public int? StringMaxLength { get; set; }
        public bool IsVisibleInListView { get; set; } = true;
        public bool IsVisibleInDetailView { get; set; } = true;
        public bool IsEditable { get; set; } = true;
        public string ToolTip { get; set; }
        public string DisplayName { get; set; }
    }
}
```

#### SupportedTypes.cs

Whitelist of CLR type names that users can select when defining fields.

Place in `YourModule/Services/SupportedTypes.cs`.

```csharp
namespace YourApp.Module.Services
{
    public static class SupportedTypes
    {
        private static readonly HashSet<string> ValidTypes = new()
        {
            "System.String",
            "System.Int32",
            "System.Int64",
            "System.Decimal",
            "System.Double",
            "System.Single",
            "System.Boolean",
            "System.DateTime",
            "System.Guid",
            "System.Byte[]",
            "Reference",
        };

        public static IReadOnlyList<string> AllTypeNames => ValidTypes.ToList();

        public static bool IsSupported(string clrTypeName)
        {
            return ValidTypes.Contains(clrTypeName);
        }
    }
}
```

#### RuntimeAssemblyBuilder.cs

This is the core of the system. It generates C# source code from metadata and compiles it with Roslyn into a real .NET assembly.

Place in `YourModule/Services/RuntimeAssemblyBuilder.cs`.

```csharp
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace YourApp.Module.Services
{
    public class CompilationResult
    {
        public Assembly Assembly { get; set; }
        public AssemblyLoadContext LoadContext { get; set; }
        public Type[] RuntimeTypes { get; set; } = Array.Empty<Type>();
        public Dictionary<string, string> GeneratedSources { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public bool Success => Errors.Count == 0;
    }

    public static class RuntimeAssemblyBuilder
    {
        private const string RuntimeNamespace = "YourApp.RuntimeEntities";

        public static CompilationResult Compile(List<RuntimeClassMetadata> classes)
        {
            var result = new CompilationResult();
            if (classes.Count == 0)
            {
                result.RuntimeTypes = Array.Empty<Type>();
                return result;
            }

            var syntaxTrees = new List<SyntaxTree>();
            foreach (var cc in classes)
            {
                var source = GenerateSource(cc);
                result.GeneratedSources[cc.ClassName] = source;
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(source,
                    CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12)));
            }

            var references = GetMetadataReferences();
            var compilation = CSharpCompilation.Create(
                assemblyName: $"RuntimeEntities_{Guid.NewGuid():N}",
                syntaxTrees: syntaxTrees,
                references: references,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release));

            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);

            foreach (var diag in emitResult.Diagnostics)
            {
                if (diag.Severity == DiagnosticSeverity.Error)
                    result.Errors.Add(diag.ToString());
                else if (diag.Severity == DiagnosticSeverity.Warning)
                    result.Warnings.Add(diag.ToString());
            }

            if (!emitResult.Success) return result;

            ms.Seek(0, SeekOrigin.Begin);
            var alc = new CollectibleLoadContext();
            var assembly = alc.LoadFromStream(ms);

            result.Assembly = assembly;
            result.LoadContext = alc;
            result.RuntimeTypes = assembly.GetExportedTypes();
            return result;
        }

        public static string GenerateSource(RuntimeClassMetadata cc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.ComponentModel;");
            sb.AppendLine("using DevExpress.ExpressApp;");
            sb.AppendLine("using DevExpress.ExpressApp.DC;");
            sb.AppendLine("using DevExpress.Persistent.Base;");
            sb.AppendLine("using DevExpress.Persistent.BaseImpl;");
            sb.AppendLine("using DevExpress.Xpo;");
            sb.AppendLine();
            sb.AppendLine($"namespace {RuntimeNamespace}");
            sb.AppendLine("{");

            // Class-level attributes
            sb.AppendLine("    [DefaultClassOptions]");
            if (!string.IsNullOrWhiteSpace(cc.NavigationGroup))
                sb.AppendLine($"    [NavigationItem(\"{Escape(cc.NavigationGroup)}\")]");

            // Find the default property for lookups
            var defaultField = cc.Fields.FirstOrDefault(f => f.IsDefaultField)
                ?? cc.Fields
                    .Where(f => f.TypeName == "System.String")
                    .OrderBy(f => f.SortOrder)
                    .FirstOrDefault();
            if (defaultField != null)
                sb.AppendLine($"    [DefaultProperty(\"{defaultField.FieldName}\")]");

            // XPO class inherits from BaseObject
            sb.AppendLine($"    public class {cc.ClassName} : BaseObject");
            sb.AppendLine("    {");

            // XPO requires a constructor that takes Session
            sb.AppendLine($"        public {cc.ClassName}(Session session) : base(session) {{ }}");
            sb.AppendLine();

            // Generate each property with XPO backing field pattern
            foreach (var field in cc.Fields.OrderBy(f => f.SortOrder))
            {
                if (string.IsNullOrWhiteSpace(field.FieldName)) continue;

                var clrType = MapClrType(field.TypeName);
                var nullable = !field.IsRequired && IsValueType(field.TypeName) ? "?" : "";
                var backingType = $"{clrType}{nullable}";
                var backingName = char.ToLowerInvariant(field.FieldName[0]) + field.FieldName[1..];

                // Backing field
                sb.AppendLine($"        {backingType} {backingName};");

                // XAF display attributes
                if (field.IsImmediatePostData)
                    sb.AppendLine("        [ImmediatePostData]");
                if (!field.IsVisibleInListView)
                    sb.AppendLine("        [VisibleInListView(false)]");
                if (!field.IsVisibleInDetailView)
                    sb.AppendLine("        [VisibleInDetailView(false)]");
                if (!string.IsNullOrWhiteSpace(field.DisplayName))
                    sb.AppendLine($"        [DisplayName(\"{Escape(field.DisplayName)}\")]");

                // XPO size attribute for strings
                if (field.TypeName == "System.String" && field.StringMaxLength.HasValue)
                    sb.AppendLine($"        [Size({field.StringMaxLength.Value})]");
                else if (field.TypeName == "System.String")
                    sb.AppendLine("        [Size(SizeAttribute.DefaultStringMappingFieldSize)]");

                // Property using SetPropertyValue (XPO change tracking)
                sb.AppendLine($"        public {backingType} {field.FieldName}");
                sb.AppendLine("        {");
                sb.AppendLine($"            get => {backingName};");
                sb.AppendLine($"            set => SetPropertyValue(nameof({field.FieldName}), ref {backingName}, value);");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Collects all metadata references needed for Roslyn compilation:
        /// trusted platform assemblies + any loaded DevExpress assemblies.
        /// </summary>
        private static List<MetadataReference> GetMetadataReferences()
        {
            var references = new List<MetadataReference>();
            var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Platform assemblies (System.*, netstandard, etc.)
            var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")?.ToString();
            if (trusted != null)
            {
                foreach (var path in trusted.Split(Path.PathSeparator))
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            references.Add(MetadataReference.CreateFromFile(path));
                            loadedPaths.Add(path);
                        }
                        catch { }
                    }
                }
            }

            // DevExpress and other loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try
                {
                    var loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc) && File.Exists(loc) && !loadedPaths.Contains(loc))
                    {
                        references.Add(MetadataReference.CreateFromFile(loc));
                        loadedPaths.Add(loc);
                    }
                }
                catch { }
            }

            return references;
        }

        private static string MapClrType(string typeName) => typeName switch
        {
            "System.String" => "string",
            "System.Int32" => "int",
            "System.Int64" => "long",
            "System.Decimal" => "decimal",
            "System.Double" => "double",
            "System.Single" => "float",
            "System.Boolean" => "bool",
            "System.DateTime" => "DateTime",
            "System.Guid" => "Guid",
            "System.Byte[]" => "byte[]",
            _ => typeName
        };

        private static bool IsValueType(string typeName) =>
            typeName is "System.Int32" or "System.Int64" or "System.Decimal"
                or "System.Double" or "System.Single" or "System.Boolean"
                or "System.DateTime" or "System.Guid";

        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Custom AssemblyLoadContext for runtime-compiled assemblies.
    /// Set isCollectible: false because XPO holds references to types
    /// that prevent unloading. The process-restart strategy handles cleanup.
    /// </summary>
    public class CollectibleLoadContext : AssemblyLoadContext
    {
        public CollectibleLoadContext() : base(isCollectible: false) { }

        protected override Assembly Load(AssemblyName assemblyName) => null;
    }
}
```

**Key points about the generated code:**

- Every class inherits from `DevExpress.Persistent.BaseImpl.BaseObject` (XPO base class with Guid `Oid` primary key).
- Every class has a `public ClassName(Session session) : base(session) { }` constructor -- this is **mandatory** for XPO.
- Properties use the XPO `SetPropertyValue` pattern with backing fields, not auto-properties. This is how XPO tracks changes and triggers dirty-state/validation.
- `[DefaultClassOptions]` makes the type visible in XAF navigation.
- `[NavigationItem("GroupName")]` controls which nav group the entity appears under.

#### AssemblyGenerationManager.cs

Manages the lifecycle of compiled assemblies. Holds a reference to the current compilation result and provides thread-safe load/unload.

Place in `YourModule/Services/AssemblyGenerationManager.cs`.

```csharp
using System.Runtime.Loader;

namespace YourApp.Module.Services
{
    public class AssemblyGenerationManager
    {
        private CompilationResult _currentResult;
        private readonly object _lock = new();

        public Type[] RuntimeTypes => _currentResult?.RuntimeTypes ?? Array.Empty<Type>();
        public bool HasLoadedAssembly => _currentResult?.Assembly != null;

        public CompilationResult LoadNewAssembly(List<RuntimeClassMetadata> classes)
        {
            lock (_lock)
            {
                var result = RuntimeAssemblyBuilder.Compile(classes);
                if (result.Success)
                {
                    UnloadCurrent();
                    _currentResult = result;
                }
                return result;
            }
        }

        public void UnloadCurrent()
        {
            lock (_lock)
            {
                if (_currentResult?.LoadContext != null)
                {
                    if (_currentResult.LoadContext.IsCollectible)
                    {
                        var weakRef = new WeakReference(_currentResult.LoadContext);
                        _currentResult.LoadContext.Unload();
                        _currentResult = null;
                        for (int i = 0; i < 5 && weakRef.IsAlive; i++)
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        }
                    }
                    else
                    {
                        _currentResult = null;
                    }
                }
            }
        }
    }
}
```

#### SchemaChangeOrchestrator.cs

Coordinates the deploy workflow: re-read metadata, compile, register types in XafTypesInfo, run XPO `UpdateSchema`, and signal restart.

Place in `YourModule/Services/SchemaChangeOrchestrator.cs`.

```csharp
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Xpo;
using DevExpress.Persistent.Base;
using DevExpress.Xpo;
using DevExpress.Xpo.DB;
using DevExpress.Xpo.Metadata;

namespace YourApp.Module.Services
{
    public class SchemaChangeOrchestrator
    {
        private static readonly Lazy<SchemaChangeOrchestrator> _instance = new(() => new());
        private static readonly SemaphoreSlim _semaphore = new(1, 1);
        private int _schemaVersion;
        private HashSet<string> _previousTypeNames = new();

        public static SchemaChangeOrchestrator Instance => _instance.Value;

        /// <summary>Fires after a schema change. int = new version number.</summary>
        public event Action<int> SchemaChanged;

        public bool RestartNeeded { get; private set; }

        public void SetKnownTypeNames(IEnumerable<string> typeNames)
        {
            _previousTypeNames = new HashSet<string>(typeNames);
        }

        /// <summary>
        /// Main entry point called when user clicks "Deploy Schema".
        /// Re-reads metadata, compiles, registers types, updates DB schema,
        /// then fires SchemaChanged which triggers the restart.
        /// </summary>
        public async Task ExecuteHotLoadAsync()
        {
            if (!await _semaphore.WaitAsync(TimeSpan.FromSeconds(30)))
                return;

            try
            {
                // Replace YourModule with your actual module class name
                var connStr = YourAppModule.RuntimeConnectionString;
                if (string.IsNullOrEmpty(connStr)) return;

                var classes = YourAppModule.QueryMetadata(connStr);

                var result = YourAppModule.AssemblyManager.LoadNewAssembly(classes);
                if (!result.Success)
                {
                    RestartNeeded = true;
                    SchemaChanged?.Invoke(Interlocked.Increment(ref _schemaVersion));
                    return;
                }

                // Register in XafTypesInfo so XAF knows about the types
                foreach (var type in result.RuntimeTypes)
                    XafTypesInfo.Instance.RegisterEntity(type);

                YourAppModule.Instance?.RefreshRuntimeTypes(result.RuntimeTypes);

                // Create/alter SQL tables via XPO
                UpdateDatabaseSchema(result.RuntimeTypes);

                RestartNeeded = true;
                _previousTypeNames = new HashSet<string>(result.RuntimeTypes.Select(t => t.Name));
                SchemaChanged?.Invoke(Interlocked.Increment(ref _schemaVersion));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Uses XPO's built-in UpdateSchema to create/alter database tables.
        /// No custom DDL needed -- this is a major advantage over EF Core.
        /// </summary>
        internal static void UpdateDatabaseSchema(Type[] runtimeTypes, string connectionString = null)
        {
            var connStr = connectionString ?? YourAppModule.RuntimeConnectionString;
            if (string.IsNullOrEmpty(connStr)) return;

            var dict = new ReflectionDictionary();
            foreach (var type in runtimeTypes)
                dict.GetDataStoreSchema(type);

            using var dataLayer = XpoDefault.GetDataLayer(connStr, dict, AutoCreateOption.DatabaseAndSchema);
            using var session = new Session(dataLayer);
            session.UpdateSchema();
        }
    }
}
```

### 3.4 Wire Up Module.cs

Your XAF module class needs four additions:

1. **Static properties** for the connection string and assembly manager
2. **`EarlyBootstrap()`** -- called before `AddXaf()` to compile types early
3. **`QueryMetadata()`** -- reads CustomClass/CustomField via raw SQL (no XPO Session)
4. **`RefreshRuntimeTypes()`** -- adds/removes types from `AdditionalExportedTypes`

Add these to your existing `ModuleBase` subclass:

```csharp
using System.Reflection;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Xpo;
using DevExpress.Persistent.Base;
using Microsoft.Data.SqlClient;

public sealed class YourAppModule : ModuleBase
{
    // --- Static infrastructure ---
    public static string RuntimeConnectionString { get; set; }
    public static AssemblyGenerationManager AssemblyManager { get; } = new();
    public static YourAppModule Instance { get; private set; }
    public static bool DegradedMode { get; private set; }

    private readonly HashSet<Type> _addedRuntimeTypes = new();

    public YourAppModule()
    {
        Instance = this;
        // ... your existing constructor code ...

        // Register metadata types so XAF creates their tables
        AdditionalExportedTypes.Add(typeof(CustomClass));
        AdditionalExportedTypes.Add(typeof(CustomField));
    }

    public override void Setup(XafApplication application)
    {
        base.Setup(application);

        // Runs during XAF init -- registers already-compiled types
        if (!string.IsNullOrEmpty(RuntimeConnectionString))
            BootstrapRuntimeEntities(application);
    }

    /// <summary>
    /// Call BEFORE AddXaf() in Startup.cs.
    /// Reads metadata via raw SQL, compiles types, creates DB tables.
    /// </summary>
    public static void EarlyBootstrap()
    {
        if (string.IsNullOrEmpty(RuntimeConnectionString)) return;

        try
        {
            var classes = QueryMetadata(RuntimeConnectionString);
            if (classes.Count == 0) return;

            if (!AssemblyManager.HasLoadedAssembly || AssemblyManager.RuntimeTypes.Length == 0)
            {
                var result = AssemblyManager.LoadNewAssembly(classes);
                if (result.Success)
                    SchemaChangeOrchestrator.UpdateDatabaseSchema(result.RuntimeTypes, RuntimeConnectionString);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal on first run (database may not exist yet)
            Tracing.Tracer.LogText($"[EarlyBootstrap] Skipped: {ex.Message}");
        }
    }

    private void BootstrapRuntimeEntities(XafApplication application)
    {
        try
        {
            var classes = QueryMetadata(RuntimeConnectionString);
            if (classes.Count == 0) return;

            Type[] runtimeTypes;
            if (AssemblyManager.HasLoadedAssembly && AssemblyManager.RuntimeTypes.Length > 0)
                runtimeTypes = AssemblyManager.RuntimeTypes;
            else
            {
                var result = AssemblyManager.LoadNewAssembly(classes);
                if (!result.Success) { DegradedMode = true; return; }
                runtimeTypes = result.RuntimeTypes;
            }

            RefreshRuntimeTypes(runtimeTypes);
            SchemaChangeOrchestrator.Instance.SetKnownTypeNames(runtimeTypes.Select(t => t.Name));
        }
        catch (Exception ex)
        {
            DegradedMode = true;
            Tracing.Tracer.LogError($"[DEGRADED MODE] Bootstrap failed: {ex.Message}");
        }
    }

    public void RefreshRuntimeTypes(Type[] runtimeTypes)
    {
        foreach (var oldType in _addedRuntimeTypes)
            AdditionalExportedTypes.Remove(oldType);
        _addedRuntimeTypes.Clear();

        foreach (var type in runtimeTypes)
        {
            AdditionalExportedTypes.Add(type);
            _addedRuntimeTypes.Add(type);
        }
    }

    /// <summary>
    /// Reads metadata via raw SqlClient. This runs BEFORE XPO/XAF is initialized,
    /// so we cannot use Sessions or ObjectSpaces. XPO stores Guid PKs as "Oid"
    /// and uses GCRecord for soft delete (NULL = not deleted).
    /// Status = 0 means Runtime (active).
    /// </summary>
    internal static List<RuntimeClassMetadata> QueryMetadata(string connectionString)
    {
        var classes = new List<RuntimeClassMetadata>();

        using var conn = new SqlConnection(connectionString);
        conn.Open();

        // Check if table exists (first run = no table yet)
        using (var checkCmd = new SqlCommand(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'CustomClass') THEN 1 ELSE 0 END",
            conn))
        {
            if ((int)checkCmd.ExecuteScalar() == 0)
                return classes;
        }

        var classMap = new Dictionary<Guid, RuntimeClassMetadata>();
        using (var cmd = new SqlCommand(
            @"SELECT [Oid], [ClassName], [NavigationGroup], [Description], [Status]
              FROM [CustomClass]
              WHERE [Status] = 0 AND [GCRecord] IS NULL", conn))
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var cc = new RuntimeClassMetadata
                {
                    ClassName = reader.GetString(1),
                    NavigationGroup = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                };
                classMap[reader.GetGuid(0)] = cc;
                classes.Add(cc);
            }
        }

        if (classes.Count == 0) return classes;

        var classIds = string.Join(",", classMap.Keys.Select(id => $"'{id}'"));
        using (var cmd = new SqlCommand(
            $@"SELECT [CustomClass], [FieldName], [TypeName], [IsRequired], [IsDefaultField],
                      [Description], [ReferencedClassName], [SortOrder],
                      [IsImmediatePostData], [StringMaxLength],
                      [IsVisibleInListView], [IsVisibleInDetailView], [IsEditable],
                      [ToolTip], [DisplayName]
               FROM [CustomField]
               WHERE [CustomClass] IN ({classIds}) AND [GCRecord] IS NULL
               ORDER BY [SortOrder], [FieldName]", conn))
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var classId = reader.GetGuid(0);
                if (classMap.TryGetValue(classId, out var cc))
                {
                    cc.Fields.Add(new RuntimeFieldMetadata
                    {
                        FieldName = reader.GetString(1),
                        TypeName = reader.IsDBNull(2) ? "System.String" : reader.GetString(2),
                        IsRequired = !reader.IsDBNull(3) && reader.GetBoolean(3),
                        IsDefaultField = !reader.IsDBNull(4) && reader.GetBoolean(4),
                        Description = reader.IsDBNull(5) ? null : reader.GetString(5),
                        ReferencedClassName = reader.IsDBNull(6) ? null : reader.GetString(6),
                        SortOrder = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                        IsImmediatePostData = !reader.IsDBNull(8) && reader.GetBoolean(8),
                        StringMaxLength = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                        IsVisibleInListView = reader.IsDBNull(10) || reader.GetBoolean(10),
                        IsVisibleInDetailView = reader.IsDBNull(11) || reader.GetBoolean(11),
                        IsEditable = reader.IsDBNull(12) || reader.GetBoolean(12),
                        ToolTip = reader.IsDBNull(13) ? null : reader.GetString(13),
                        DisplayName = reader.IsDBNull(14) ? null : reader.GetString(14),
                    });
                }
            }
        }

        return classes;
    }
}
```

**Why raw SQL instead of XPO?** The metadata must be read *before* XAF initializes. At that point, there is no `XafApplication`, no `ObjectSpace`, and no XPO `Session`. Using `Microsoft.Data.SqlClient` directly is the only option. The query filters on `Status = 0` (Runtime) and `GCRecord IS NULL` (not soft-deleted by XPO).

### 3.5 Wire Up Startup.cs

Two critical lines must go **before** `services.AddXaf(...)`:

```csharp
public void ConfigureServices(IServiceCollection services)
{
    // ... existing service registrations ...

    // 1. Set connection string BEFORE AddXaf
    YourAppModule.RuntimeConnectionString =
        Configuration.GetConnectionString("ConnectionString");

    // 2. Early bootstrap: compile runtime types BEFORE XAF init
    YourAppModule.EarlyBootstrap();

    // 3. Now AddXaf -- runtime types are already compiled and will be
    //    picked up by Module.Setup() -> BootstrapRuntimeEntities()
    services.AddXaf(Configuration, builder =>
    {
        // ... your existing XAF configuration ...
    });

    // ... rest of ConfigureServices ...
}
```

In the `Configure` method, add the SignalR hub endpoint and wire the restart logic:

```csharp
public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    // ... existing middleware ...

    app.UseEndpoints(endpoints =>
    {
        endpoints.MapXafEndpoints();
        endpoints.MapBlazorHub();
        endpoints.MapHub<SchemaUpdateHub>("/schemaUpdateHub");  // ADD THIS
        endpoints.MapFallbackToPage("/_Host");
        endpoints.MapControllers();
    });

    // Wire RestartService for graceful shutdown
    var lifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();
    RestartService.Configure(lifetime);

    // Wire schema change -> SignalR notification -> exit code 42
    var hubContext = app.ApplicationServices
        .GetRequiredService<IHubContext<SchemaUpdateHub>>();
    var orchestrator = SchemaChangeOrchestrator.Instance;
    orchestrator.SchemaChanged += (version) =>
    {
        _ = hubContext.Clients.All.SendAsync("SchemaChanged", version, orchestrator.RestartNeeded);

        if (orchestrator.RestartNeeded)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);  // Give clients time to receive notification
                Environment.Exit(42);
            });
        }
    };
}
```

### 3.6 Wire Up Program.cs

The exit code 42 protocol. When `SchemaChanged` fires with `RestartNeeded = true`, the process exits with code 42. The wrapper script (next section) sees this and restarts.

In your `Main` method:

```csharp
public static int Main(string[] args)
{
    // ... existing argument handling ...

    RestartService.ResetRestartFlag();
    IHost host = CreateHostBuilder(args).Build();
    host.Run();

    // After host.Run() returns (either normal shutdown or restart request):
    if (RestartService.IsRestartRequested)
    {
        Console.WriteLine("[RESTART] Process exiting for restart (exit code 42)...");
        return 42;
    }

    return 0;
}
```

Also add the `RestartService` and `SchemaUpdateHub` to your Blazor.Server project:

**Services/RestartService.cs:**

```csharp
public static class RestartService
{
    private static IHostApplicationLifetime _lifetime;
    private static volatile bool _restartRequested;

    public static bool IsRestartRequested => _restartRequested;

    public static void Configure(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    public static void RequestRestart()
    {
        _restartRequested = true;
        _lifetime?.StopApplication();
    }

    public static void ResetRestartFlag()
    {
        _restartRequested = false;
    }
}
```

**Hubs/SchemaUpdateHub.cs:**

```csharp
using Microsoft.AspNetCore.SignalR;

public class SchemaUpdateHub : Hub
{
    // Empty -- used only for server-to-client push via IHubContext<SchemaUpdateHub>
}
```

### 3.7 Add the Deploy Schema Controller

This adds a "Deploy Schema" button to the CustomClass list view.

Place in `YourModule/Controllers/SchemaChangeController.cs`.

```csharp
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.Persistent.Base;

public class SchemaChangeController : ViewController<ListView>
{
    private SimpleAction _deployAction;

    public SchemaChangeController()
    {
        TargetObjectType = typeof(CustomClass);

        _deployAction = new SimpleAction(this, "DeploySchema", PredefinedCategory.Edit)
        {
            Caption = "Deploy Schema",
            ConfirmationMessage = "Deploy all runtime schema changes? The server may briefly restart.",
            ImageName = "Action_Reload",
            ToolTip = "Compile and deploy all runtime entity changes",
        };
        _deployAction.Execute += DeployAction_Execute;
    }

    private void DeployAction_Execute(object sender, SimpleActionExecuteEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            await SchemaChangeOrchestrator.Instance.ExecuteHotLoadAsync();
        });
    }
}
```

### 3.8 Add the Restart Wrapper Script

The server must be launched through this wrapper script for the restart protocol to work. When the process exits with code 42, the script waits 2 seconds and restarts it.

**run-server.bat** (place in your solution root):

```batch
@echo off
:loop
echo [run-server] Starting YourApp.Blazor.Server...
dotnet run --project YourApp/YourApp.Blazor.Server
if %ERRORLEVEL% == 42 (
    echo [run-server] Exit code 42 detected. Restarting...
    timeout /t 2 /nobreak >nul
    goto loop
)
echo [run-server] Server exited with code %ERRORLEVEL%.
```

For Linux/Docker deployments, create an equivalent shell script:

```bash
#!/bin/bash
while true; do
    echo "[run-server] Starting YourApp.Blazor.Server..."
    dotnet run --project YourApp/YourApp.Blazor.Server
    EXIT_CODE=$?
    if [ $EXIT_CODE -ne 42 ]; then
        echo "[run-server] Server exited with code $EXIT_CODE."
        exit $EXIT_CODE
    fi
    echo "[run-server] Exit code 42 detected. Restarting..."
    sleep 2
done
```

---

## 4. Key XPO Differences from EF Core

If you have seen the EF Core version of dynamic assemblies (such as [XafDynamicAssemblies](https://github.com/MBrekhof/XafDynamicAssemblies)), here are the critical differences:

| Concern | EF Core Approach | XPO Approach |
|---|---|---|
| **Schema sync** | Custom `SchemaSynchronizer` with raw DDL | `XpoDefault.GetDataLayer()` + `session.UpdateSchema()` handles everything automatically |
| **Model cache** | `DynamicModelCacheKeyFactory` to invalidate EF model cache | Not needed -- XPO has no model cache |
| **Generated code style** | `virtual` auto-properties | Backing fields + `SetPropertyValue(nameof(...), ref field, value)` |
| **Constructor** | Parameterless | `public ClassName(Session session) : base(session) { }` -- **mandatory** |
| **Reference properties** | FK ID property + `[ForeignKey]` + navigation property | Just a typed property (e.g., `public Customer Customer { get => ...; set => ...; }`) -- XPO infers the FK |
| **Associations** | Configured in DbContext | `[Association]` attribute + `XPCollection<T>` |
| **Base class** | Custom `DynamicBaseObject` or `DbContext` entity | `DevExpress.Persistent.BaseImpl.BaseObject` (provides Guid `Oid` PK) |
| **Soft delete** | Custom implementation | Built-in via `GCRecord` column (NULL = not deleted) |

The XPO approach is significantly simpler because XPO's `UpdateSchema` eliminates the need for any custom DDL generation.

---

## 5. Customization Points

### Add New Field Types

Edit `SupportedTypes.cs` to add more types. For example, to support `TimeSpan`:

```csharp
ValidTypes.Add("System.TimeSpan");
```

Then add the mapping in `RuntimeAssemblyBuilder.MapClrType`:

```csharp
"System.TimeSpan" => "TimeSpan",
```

And mark it as a value type if applicable:

```csharp
private static bool IsValueType(string typeName) =>
    typeName is /* existing */ or "System.TimeSpan";
```

### Add Custom Attributes to Generated Code

Extend `EmitFieldAttributes` in `RuntimeAssemblyBuilder` to emit additional XAF attributes based on field metadata. For example, to add `[RuleRequiredField]` for required fields:

```csharp
if (field.IsRequired)
    sb.AppendLine($"        [DevExpress.Persistent.Validation.RuleRequiredField]");
```

### Support Cross-Entity References

Reference fields (where `TypeName = "Reference"`) generate properties typed to another runtime class. The `ReferencedClassName` must match the `ClassName` of another `CustomClass` in the same compilation batch. Since all classes are compiled into the same assembly, cross-references resolve automatically.

### Custom Navigation and Grouping

The `NavigationGroup` property on `CustomClass` maps directly to XAF's `[NavigationItem("GroupName")]` attribute. You can extend the metadata to support:

- Custom icons (`ImageName`)
- Specific list/detail view IDs
- Custom editor types

### Validation Rules

Add validation properties to `CustomClass` and `CustomField` using XAF's `[RuleFromBoolProperty]` pattern. The reference implementation validates:

- Class names are valid C# identifiers
- Class names are not C# keywords
- Field names are not reserved XPO column names (`Oid`, `GCRecord`, `ObjectType`, `OptimisticLockField`)
- Type names are in the supported types whitelist
- Reference fields have a `ReferencedClassName`

---

## 6. Troubleshooting

### "Compilation failed" on Deploy Schema

**Symptom:** Clicking Deploy Schema fails and the server does not restart.

**Cause:** Roslyn compilation errors, usually from invalid class/field names or unsupported type names.

**Fix:** Check the application log (trace output) for Roslyn diagnostic messages. Common issues:
- Field name is a C# keyword (`class`, `int`, `string`)
- Field name starts with a digit
- Two fields on the same class have the same name
- Referenced class name does not match any `CustomClass.ClassName`

### Runtime entities do not appear in navigation after restart

**Symptom:** Server restarts but the new entity is not visible.

**Cause:** `EarlyBootstrap()` is not running before `AddXaf()`, or the connection string is not set.

**Fix:** Verify in `Startup.ConfigureServices` that these two lines come **before** `services.AddXaf(...)`:

```csharp
YourAppModule.RuntimeConnectionString = Configuration.GetConnectionString("ConnectionString");
YourAppModule.EarlyBootstrap();
```

### Server does not restart after Deploy Schema

**Symptom:** Deploy Schema runs but the process does not exit with code 42.

**Cause:** The server was started with `dotnet run` directly instead of `run-server.bat`.

**Fix:** Always start via `run-server.bat`. The exit code 42 mechanism requires a wrapper process to restart the server.

### "Table 'CustomClass' does not exist" on first run

**Symptom:** `EarlyBootstrap` logs "Skipped" on the very first run.

**Cause:** This is expected. On first startup, XAF has not created the database yet, so `QueryMetadata` finds no tables and returns an empty list. After XAF creates the database (via `--updateDatabase` or auto-create), subsequent starts will find the tables.

**Fix:** No action needed. Run `dotnet run -- --updateDatabase` once to initialize the database, then use `run-server.bat` for normal operation.

### XPO warnings about null Session

**Symptom:** Log shows warnings about `new CustomClass(null)` or similar.

**Cause:** The `QueryMetadata` method reads metadata via raw SQL and constructs `RuntimeClassMetadata` DTOs, not XPO objects. This is by design -- no XPO Session is available at that point.

**Fix:** These warnings (if any) are harmless and can be ignored. The DTO classes (`RuntimeClassMetadata`, `RuntimeFieldMetadata`) are plain C# objects with no XPO dependency.

### Degraded mode after failed compilation

**Symptom:** The application starts but runtime entities are not available. `DegradedMode` is `true`.

**Cause:** Roslyn compilation failed during `BootstrapRuntimeEntities`. The app continues running with only its static (compiled) business objects.

**Fix:** Check the trace log for the specific compilation error. Fix the metadata (class names, field names, types) via the Schema Management UI, then click Deploy Schema again.

---

## 7. Limitations

The following features are **not yet supported** in this implementation:

| Feature | Status | Notes |
|---|---|---|
| **Collectible ALC** | Not collectible | The `AssemblyLoadContext` is non-collectible because XPO holds type references. The process-restart strategy handles memory cleanup. |
| **Hot-reload without restart** | Not supported | XAF builds its Application Model once at startup. New types require a full process restart to get navigation, views, and editors. |
| **Enum fields** | Not supported | Only the types listed in `SupportedTypes` are available. Enum support would require generating enum types alongside classes. |
| **Collection/association properties** | Not supported | Only scalar and single-reference properties are generated. `XPCollection<T>` back-references would require two-way metadata. |
| **Inheritance** | Not supported | All generated classes inherit directly from `BaseObject`. No support for class hierarchies among runtime entities. |
| **Computed/calculated properties** | Not supported | All properties are persistent storage fields. `PersistentAlias` or non-persistent calculated properties are not generated. |
| **Custom business logic** | Graduation available | Generated classes have no methods, only properties. Use the built-in Graduation feature to generate production C# source code for complex logic. |
| **Web API / OData** | Implemented | Runtime entities marked with `IsApiExposed` are available at `/api/odata` with Swagger UI at `/swagger`. |
| **Multi-database** | Not supported | All runtime entities use the same connection string as the main application. |
| **Schema migration** | Additive only | XPO `UpdateSchema` creates new tables/columns but does not rename or drop existing ones. Renaming a class or field creates a new column and the old one remains. |
| **Concurrent deploy** | Semaphore-guarded | Only one deploy can run at a time (30-second timeout on the semaphore). |

### Security Considerations

- Runtime entities inherit the XAF security system. However, you may need to grant permissions for new types via the Role editor after they are created.
- The `QueryMetadata` method constructs SQL with string concatenation of Guid values (from the `classIds` variable). These are Guid primary keys read from the database, not user input, so SQL injection risk is minimal -- but consider parameterized queries if you extend this.
- Class and field names are validated to be legal C# identifiers, which prevents code injection through the Roslyn compilation step.

---

## 8. Additional Features

The reference implementation (`XafXPODynAssem`) includes several features beyond the core runtime entity system described above. These are optional and can be added independently.

### 8.1 Graduation

Generate production-ready XPO C# source code from a runtime entity. After graduation, the entity is marked as `Compiled` and excluded from future runtime compilation. The generated source includes proper XPO patterns (`Session` constructor, `SetPropertyValue`, backing fields) and can be added directly to your project.

**Key files:** `GraduationService.cs`, `GraduateController.cs`, `GraduationWarningController.cs`

### 8.2 Schema Export/Import

Export all runtime entity metadata as a JSON file for backup or migration between environments. Import supports smart merge — new classes are created, existing classes are updated with new fields.

**Key files:** `SchemaExportImportService.cs`, `SchemaExportImportController.cs`, `ISchemaFileService.cs`, `BlazorSchemaFileService.cs`

**Blazor note:** Requires JS interop functions (`schemaFile.download` and `schemaFile.upload`) in `_Host.cshtml` for file download/upload.

### 8.3 Web API / OData

Expose runtime entities as OData REST endpoints at `/api/odata` with Swagger UI at `/swagger`. Entities are exposed when the `IsApiExposed` flag is set on the `CustomClass` metadata.

**Key files:** `Startup.cs` (Web API registration), `CustomClass.IsApiExposed` property

**NuGet:** `DevExpress.ExpressApp.WebApi`, `Swashbuckle.AspNetCore`

### 8.4 AI Chat

LLM-powered schema management using tool-calling (function calling). The AI assistant can create, modify, and delete entities, manage role permissions, and answer questions about the current schema. Uses LlmTornado for multi-provider LLM support.

**Key files:** `AIChatService.cs`, `SchemaAIToolsProvider.cs`, `TornadoApiProvider.cs`, `SchemaDiscoveryService.cs`, `AIChat.cs`, `AIServiceCollectionExtensions.cs`

**NuGet:** `LlmTornado`, `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.Abstractions`

**Configuration:** Requires an `AI` section in `appsettings.json` with API key. See `appsettings.template.json` or `appsettings.Development.template.json` for the required structure.

### 8.5 Audit Trail

Every deploy, export, and import action creates a `SchemaHistory` record with timestamp, user, action type, summary, and optional schema JSON snapshot.

**Key files:** `SchemaHistory.cs` (business object), `SchemaChangeController.cs` (deploy logging), `SchemaExportImportController.cs` (export/import logging)
