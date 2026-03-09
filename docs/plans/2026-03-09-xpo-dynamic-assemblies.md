# XPO Dynamic Assemblies Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Port the AI-powered dynamic assemblies system from XAF+EF Core to XAF+XPO, enabling runtime entity creation without recompilation using Roslyn and XPO persistence.

**Architecture:** Two metadata tables (CustomClass, CustomField) drive Roslyn compilation of real CLR types at startup. Types inherit from XPO `BaseObject`, use XPO's `Session` constructor and `SetPropertyValue` pattern. XPO handles all DDL (no custom SchemaSynchronizer needed — XPO's `UpdateSchema` is incremental). Hot-load triggers exit code 42 for process restart since XAF's TypesInfo is process-static.

**Tech Stack:** .NET 8, DevExpress XAF 25.2 + XPO, Roslyn (Microsoft.CodeAnalysis.CSharp 4.10), SQL Server (localdb), Blazor Server, SignalR

---

## Key XPO Differences from EF Core Version

| Concern | EF Core Version | XPO Version |
|---|---|---|
| Base class | `DevExpress.Persistent.BaseImpl.EF.BaseObject` | `DevExpress.Persistent.BaseImpl.BaseObject` |
| Property pattern | `public virtual string Foo { get; set; }` | Backing field + `SetPropertyValue(nameof(Foo), ref foo, value)` |
| Constructor | Parameterless | `public Foo(Session session) : base(session) { }` |
| References | `Guid? FooId` + `[ForeignKey]` nav prop | Just `Foo foo; public Foo Foo { get/set }` — XPO handles FK |
| Collections | `IList<T>` / `ObservableCollection<T>` | `XPCollection<T>` via `GetCollection<T>()` |
| Non-mapped props | `[NotMapped]` | `[NonPersistent]` |
| Schema sync | Custom SchemaSynchronizer (PostgreSQL DDL) | XPO `UpdateSchema` handles it automatically |
| Model cache | `DynamicModelCacheKeyFactory` | Not needed — XPO has no model cache |
| DbContext integration | `RuntimeEntityTypes` static array | Not needed — XPO discovers types via TypesInfo |
| Connection | Npgsql (PostgreSQL) | SqlClient (SQL Server localdb) |
| PK column | `"ID"` (uuid) | `"Oid"` (uniqueidentifier) |
| Enum storage | String in PostgreSQL | Integer in SQL Server (default XPO) |

---

### Task 1: Add Roslyn NuGet Package to Module

**Files:**
- Modify: `XafXPODynAssem/XafXPODynAssem.Module/XafXPODynAssem.Module.csproj`

**Step 1: Add the Roslyn package reference**

Add to the `<ItemGroup>` containing PackageReferences:

```xml
<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.10.0" />
```

**Step 2: Build to verify**

Run: `dotnet build XafXPODynAssem.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add XafXPODynAssem/XafXPODynAssem.Module/XafXPODynAssem.Module.csproj
git commit -m "feat: add Roslyn package for runtime compilation"
```

---

### Task 2: Create Validation Helpers

**Files:**
- Create: `XafXPODynAssem/XafXPODynAssem.Module/Validation/CustomClassValidation.cs`
- Create: `XafXPODynAssem/XafXPODynAssem.Module/Validation/CustomFieldValidation.cs`

**Step 1: Create CustomClassValidation.cs**

```csharp
using System.Text.RegularExpressions;

namespace XafXPODynAssem.Module.Validation
{
    public static class CustomClassValidation
    {
        private static readonly Regex ValidIdentifierRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
            "char", "checked", "class", "const", "continue", "decimal", "default",
            "delegate", "do", "double", "else", "enum", "event", "explicit",
            "extern", "false", "finally", "fixed", "float", "for", "foreach",
            "goto", "if", "implicit", "in", "int", "interface", "internal",
            "is", "lock", "long", "namespace", "new", "null", "object",
            "operator", "out", "override", "params", "private", "protected",
            "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch",
            "this", "throw", "true", "try", "typeof", "uint", "ulong",
            "unchecked", "unsafe", "ushort", "using", "virtual", "void",
            "volatile", "while"
        };

        private static readonly HashSet<string> ReservedTypeNames = new(StringComparer.Ordinal)
        {
            "BaseObject", "CustomClass", "CustomField",
            "Object", "String", "Type", "Assembly",
            "XPObject", "XPBaseObject", "Session"
        };

        public static bool IsValidIdentifier(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && ValidIdentifierRegex.IsMatch(name);
        }

        public static bool IsCSharpKeyword(string name)
        {
            return CSharpKeywords.Contains(name);
        }

        public static bool IsReservedTypeName(string name)
        {
            return ReservedTypeNames.Contains(name);
        }
    }
}
```

**Step 2: Create CustomFieldValidation.cs**

```csharp
using System.Text.RegularExpressions;

namespace XafXPODynAssem.Module.Validation
{
    public static class CustomFieldValidation
    {
        private static readonly Regex ValidIdentifierRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private static readonly HashSet<string> ReservedFieldNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Oid", "ObjectType", "GCRecord", "OptimisticLockField"
        };

        public static bool IsValidIdentifier(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && ValidIdentifierRegex.IsMatch(name);
        }

        public static bool IsReservedFieldName(string name)
        {
            return ReservedFieldNames.Contains(name);
        }
    }
}
```

Note: XPO uses `Oid` instead of `Id` for the primary key column.

**Step 3: Build to verify**

Run: `dotnet build XafXPODynAssem.slnx`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add XafXPODynAssem/XafXPODynAssem.Module/Validation/
git commit -m "feat: add validation helpers for class and field names"
```

---

### Task 3: Create SupportedTypes

**Files:**
- Create: `XafXPODynAssem/XafXPODynAssem.Module/Services/SupportedTypes.cs`

**Step 1: Create SupportedTypes.cs**

This is simplified vs the EF Core version — no SQL type mapping needed since XPO handles DDL. Only used for validation and the UI dropdown.

```csharp
namespace XafXPODynAssem.Module.Services
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

**Step 2: Build to verify**

Run: `dotnet build XafXPODynAssem.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add XafXPODynAssem/XafXPODynAssem.Module/Services/SupportedTypes.cs
git commit -m "feat: add SupportedTypes for field type validation"
```

---

### Task 4: Create CustomClass and CustomField Business Objects

**Files:**
- Create: `XafXPODynAssem/XafXPODynAssem.Module/BusinessObjects/CustomClass.cs`
- Create: `XafXPODynAssem/XafXPODynAssem.Module/BusinessObjects/CustomField.cs`

**Step 1: Create CustomClass.cs**

```csharp
using System.ComponentModel;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ConditionalAppearance;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl;
using DevExpress.Persistent.Validation;
using DevExpress.Xpo;
using XafXPODynAssem.Module.Validation;

namespace XafXPODynAssem.Module.BusinessObjects
{
    public enum CustomClassStatus
    {
        Runtime = 0,
        Graduating = 1,
        Compiled = 2
    }

    [DefaultClassOptions]
    [NavigationItem("Schema Management")]
    [DefaultProperty(nameof(ClassName))]
    [Appearance("GraduatedEntity", TargetItems = "*",
        Criteria = "Status = 2",
        Context = "ListView",
        FontColor = "Gray",
        FontStyle = DevExpress.Drawing.DXFontStyle.Italic)]
    [Appearance("GraduatingEntity", TargetItems = "*",
        Criteria = "Status = 1",
        Context = "ListView",
        FontColor = "Orange",
        FontStyle = DevExpress.Drawing.DXFontStyle.Italic)]
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

        bool isApiExposed;
        public bool IsApiExposed
        {
            get => isApiExposed;
            set => SetPropertyValue(nameof(IsApiExposed), ref isApiExposed, value);
        }

        bool generateAsPartial;
        public bool GenerateAsPartial
        {
            get => generateAsPartial;
            set => SetPropertyValue(nameof(GenerateAsPartial), ref generateAsPartial, value);
        }

        string graduatedSource;
        [VisibleInListView(false)]
        [Size(SizeAttribute.Unlimited)]
        public string GraduatedSource
        {
            get => graduatedSource;
            set => SetPropertyValue(nameof(GraduatedSource), ref graduatedSource, value);
        }

        [Association("CustomClass-Fields"), Aggregated]
        public XPCollection<CustomField> Fields => GetCollection<CustomField>(nameof(Fields));

        [NonPersistent]
        [RuleFromBoolProperty("CustomClass_ValidClassName", DefaultContexts.Save,
            "Class Name must be a valid C# identifier (letters, digits, underscores; cannot start with a digit).")]
        [Browsable(false)]
        public bool IsClassNameValid => !string.IsNullOrWhiteSpace(ClassName) && CustomClassValidation.IsValidIdentifier(ClassName);

        [NonPersistent]
        [RuleFromBoolProperty("CustomClass_NotKeyword", DefaultContexts.Save,
            "Class Name cannot be a C# keyword.")]
        [Browsable(false)]
        public bool IsClassNameNotKeyword => string.IsNullOrWhiteSpace(ClassName) || !CustomClassValidation.IsCSharpKeyword(ClassName);

        [NonPersistent]
        [RuleFromBoolProperty("CustomClass_NotReservedType", DefaultContexts.Save,
            "Class Name conflicts with a built-in type name.")]
        [Browsable(false)]
        public bool IsClassNameNotReserved => string.IsNullOrWhiteSpace(ClassName) || !CustomClassValidation.IsReservedTypeName(ClassName);
    }
}
```

**Step 2: Create CustomField.cs**

```csharp
using System.ComponentModel;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl;
using DevExpress.Persistent.Validation;
using DevExpress.Xpo;
using XafXPODynAssem.Module.Services;
using XafXPODynAssem.Module.Validation;

namespace XafXPODynAssem.Module.BusinessObjects
{
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

        // XAF property attributes
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

        [NonPersistent]
        [RuleFromBoolProperty("CustomField_ValidFieldName", DefaultContexts.Save,
            "Field Name must be a valid C# identifier (letters, digits, underscores; cannot start with a digit).")]
        [Browsable(false)]
        public bool IsFieldNameValid => !string.IsNullOrWhiteSpace(FieldName) && CustomFieldValidation.IsValidIdentifier(FieldName);

        [NonPersistent]
        [RuleFromBoolProperty("CustomField_NotReservedField", DefaultContexts.Save,
            "Field Name is reserved (Oid, ObjectType, GCRecord, OptimisticLockField).")]
        [Browsable(false)]
        public bool IsFieldNameNotReserved => string.IsNullOrWhiteSpace(FieldName) || !CustomFieldValidation.IsReservedFieldName(FieldName);

        [NonPersistent]
        [RuleFromBoolProperty("CustomField_ValidTypeName", DefaultContexts.Save,
            "Type Name must be a supported CLR type (or 'Reference' with a Referenced Class Name).")]
        [Browsable(false)]
        public bool IsTypeNameValid => string.IsNullOrWhiteSpace(TypeName) || SupportedTypes.IsSupported(TypeName);

        [NonPersistent]
        [RuleFromBoolProperty("CustomField_ReferenceRequiresClass", DefaultContexts.Save,
            "A Reference field requires a Referenced Class Name.")]
        [Browsable(false)]
        public bool IsReferenceClassValid => TypeName != "Reference" || !string.IsNullOrWhiteSpace(ReferencedClassName);
    }
}
```

**Step 3: Build to verify**

Run: `dotnet build XafXPODynAssem.slnx`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add XafXPODynAssem/XafXPODynAssem.Module/BusinessObjects/CustomClass.cs XafXPODynAssem/XafXPODynAssem.Module/BusinessObjects/CustomField.cs
git commit -m "feat: add CustomClass and CustomField XPO business objects"
```

---

### Task 5: Create RuntimeAssemblyBuilder (XPO Code Generation)

**Files:**
- Create: `XafXPODynAssem/XafXPODynAssem.Module/Services/RuntimeAssemblyBuilder.cs`

**Step 1: Create RuntimeAssemblyBuilder.cs**

This is the core Roslyn compiler. Key difference from EF Core: generates XPO-style classes with `Session` constructor, backing fields, and `SetPropertyValue` pattern. No FK ID properties — XPO handles foreign keys automatically.

```csharp
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XafXPODynAssem.Module.BusinessObjects;

namespace XafXPODynAssem.Module.Services
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
        private const string RuntimeNamespace = "XafXPODynAssem.RuntimeEntities";

        public static CompilationResult ValidateCompilation(List<CustomClass> classes)
        {
            var result = new CompilationResult();
            if (classes.Count == 0) return result;

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
                assemblyName: $"ValidateOnly_{Guid.NewGuid():N}",
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

            return result;
        }

        public static CompilationResult Compile(List<CustomClass> classes)
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
                    optimizationLevel: OptimizationLevel.Release,
                    allowUnsafe: false));

            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);

            foreach (var diag in emitResult.Diagnostics)
            {
                if (diag.Severity == DiagnosticSeverity.Error)
                    result.Errors.Add(diag.ToString());
                else if (diag.Severity == DiagnosticSeverity.Warning)
                    result.Warnings.Add(diag.ToString());
            }

            if (!emitResult.Success)
                return result;

            ms.Seek(0, SeekOrigin.Begin);
            var alc = new CollectibleLoadContext();
            var assembly = alc.LoadFromStream(ms);

            result.Assembly = assembly;
            result.LoadContext = alc;
            result.RuntimeTypes = assembly.GetExportedTypes();

            return result;
        }

        public static string GenerateSource(CustomClass cc)
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

            // Class attributes
            sb.AppendLine("    [DefaultClassOptions]");
            if (!string.IsNullOrWhiteSpace(cc.NavigationGroup))
                sb.AppendLine($"    [NavigationItem(\"{EscapeString(cc.NavigationGroup)}\")]");

            var defaultField = FindDefaultProperty(cc);
            if (defaultField != null)
                sb.AppendLine($"    [DefaultProperty(\"{defaultField.FieldName}\")]");

            sb.AppendLine($"    public class {cc.ClassName} : BaseObject");
            sb.AppendLine("    {");

            // XPO requires Session constructor
            sb.AppendLine($"        public {cc.ClassName}(Session session) : base(session) {{ }}");
            sb.AppendLine();

            // Generate properties
            var fields = cc.Fields
                .Where(f => !string.IsNullOrWhiteSpace(f.FieldName))
                .OrderBy(f => f.SortOrder)
                .ThenBy(f => f.FieldName);

            foreach (var field in fields)
            {
                if (IsReferenceField(field))
                {
                    EmitReferenceProperty(sb, field);
                }
                else
                {
                    EmitScalarProperty(sb, field);
                }
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static void EmitScalarProperty(StringBuilder sb, CustomField field)
        {
            var clrType = MapToClrTypeName(field.TypeName);
            var nullable = !field.IsRequired && IsValueType(field.TypeName) ? "?" : "";
            var backingFieldType = $"{clrType}{nullable}";
            var backingFieldName = ToCamelCase(field.FieldName);

            // Backing field
            sb.AppendLine($"        {backingFieldType} {backingFieldName};");

            // Attributes
            EmitFieldAttributes(sb, field);

            if (field.TypeName == "System.String" && field.StringMaxLength.HasValue)
                sb.AppendLine($"        [Size({field.StringMaxLength.Value})]");
            else if (field.TypeName == "System.String")
                sb.AppendLine("        [Size(SizeAttribute.DefaultStringMappingFieldSize)]");

            if (field.TypeName == "System.Byte[]")
                sb.AppendLine("        [Size(SizeAttribute.Unlimited)]");

            // Property with SetPropertyValue
            sb.AppendLine($"        public {backingFieldType} {field.FieldName}");
            sb.AppendLine("        {");
            sb.AppendLine($"            get => {backingFieldName};");
            sb.AppendLine($"            set => SetPropertyValue(nameof({field.FieldName}), ref {backingFieldName}, value);");
            sb.AppendLine("        }");
        }

        private static void EmitReferenceProperty(StringBuilder sb, CustomField field)
        {
            var refTypeName = field.ReferencedClassName;
            var backingFieldName = ToCamelCase(field.FieldName);

            // Backing field
            sb.AppendLine($"        {refTypeName} {backingFieldName};");

            // Attributes
            EmitFieldAttributes(sb, field);

            // Property with SetPropertyValue
            sb.AppendLine($"        public {refTypeName} {field.FieldName}");
            sb.AppendLine("        {");
            sb.AppendLine($"            get => {backingFieldName};");
            sb.AppendLine($"            set => SetPropertyValue(nameof({field.FieldName}), ref {backingFieldName}, value);");
            sb.AppendLine("        }");
        }

        private static void EmitFieldAttributes(StringBuilder sb, CustomField field)
        {
            if (field.IsImmediatePostData)
                sb.AppendLine("        [ImmediatePostData]");
            if (!field.IsVisibleInListView)
                sb.AppendLine("        [VisibleInListView(false)]");
            if (!field.IsVisibleInDetailView)
                sb.AppendLine("        [VisibleInDetailView(false)]");
            if (!field.IsEditable)
                sb.AppendLine("        [DevExpress.ExpressApp.Editors.Editable(false)]");
            if (!string.IsNullOrWhiteSpace(field.ToolTip))
                sb.AppendLine($"        [ToolTip(\"{EscapeString(field.ToolTip)}\")]");
            if (!string.IsNullOrWhiteSpace(field.DisplayName))
                sb.AppendLine($"        [DisplayName(\"{EscapeString(field.DisplayName)}\")]");
        }

        private static CustomField FindDefaultProperty(CustomClass cc)
        {
            var defaultField = cc.Fields.FirstOrDefault(f => f.IsDefaultField);
            if (defaultField != null) return defaultField;

            defaultField = cc.Fields
                .Where(f => f.TypeName == "System.String" && !string.IsNullOrWhiteSpace(f.FieldName))
                .OrderBy(f => f.SortOrder)
                .FirstOrDefault();
            if (defaultField != null) return defaultField;

            return cc.Fields
                .Where(f => !string.IsNullOrWhiteSpace(f.FieldName))
                .OrderBy(f => f.SortOrder)
                .FirstOrDefault();
        }

        private static bool IsReferenceField(CustomField field)
        {
            return !string.IsNullOrWhiteSpace(field.ReferencedClassName)
                && (field.TypeName == "Reference" || string.IsNullOrWhiteSpace(field.TypeName));
        }

        private static string MapToClrTypeName(string typeName)
        {
            return typeName switch
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
        }

        private static bool IsValueType(string typeName)
        {
            return typeName is "System.Int32" or "System.Int64" or "System.Decimal"
                or "System.Double" or "System.Single" or "System.Boolean"
                or "System.DateTime" or "System.Guid";
        }

        private static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            // Prefix with underscore if it would conflict with a keyword
            var camel = char.ToLowerInvariant(name[0]) + name[1..];
            return camel;
        }

        private static string EscapeString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static List<MetadataReference> GetMetadataReferences()
        {
            var references = new List<MetadataReference>();

            var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")?.ToString();
            if (trustedAssemblies != null)
            {
                foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
                {
                    if (File.Exists(path))
                    {
                        try { references.Add(MetadataReference.CreateFromFile(path)); }
                        catch { }
                    }
                }
            }

            var loadedPaths = new HashSet<string>(references
                .OfType<PortableExecutableReference>()
                .Select(r => r.FilePath ?? ""),
                StringComparer.OrdinalIgnoreCase);

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
    }

    public class CollectibleLoadContext : AssemblyLoadContext
    {
        public CollectibleLoadContext() : base(isCollectible: false) { }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            return null;
        }
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build XafXPODynAssem.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add XafXPODynAssem/XafXPODynAssem.Module/Services/RuntimeAssemblyBuilder.cs
git commit -m "feat: add RuntimeAssemblyBuilder with XPO code generation"
```

---

### Task 6: Create AssemblyGenerationManager

**Files:**
- Create: `XafXPODynAssem/XafXPODynAssem.Module/Services/AssemblyGenerationManager.cs`

**Step 1: Create AssemblyGenerationManager.cs**

This is identical to the EF Core version — it manages the ALC lifecycle and wraps compilation.

```csharp
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using XafXPODynAssem.Module.BusinessObjects;

namespace XafXPODynAssem.Module.Services
{
    public class AssemblyGenerationManager
    {
        private readonly ILogger _logger;
        private CompilationResult _currentResult;
        private readonly object _lock = new();

        public AssemblyGenerationManager(ILogger logger = null)
        {
            _logger = logger;
        }

        public Type[] RuntimeTypes => _currentResult?.RuntimeTypes ?? Array.Empty<Type>();

        public IReadOnlyDictionary<string, string> GeneratedSources =>
            _currentResult?.GeneratedSources ?? new Dictionary<string, string>();

        public bool HasLoadedAssembly => _currentResult?.Assembly != null;

        public CompilationResult LoadNewAssembly(List<CustomClass> classes)
        {
            lock (_lock)
            {
                _logger?.LogInformation("Compiling {Count} runtime classes...", classes.Count);

                var result = RuntimeAssemblyBuilder.Compile(classes);

                if (result.Success)
                {
                    _logger?.LogInformation(
                        "Compilation succeeded. {TypeCount} types generated.",
                        result.RuntimeTypes.Length);

                    foreach (var warning in result.Warnings)
                        _logger?.LogWarning("Roslyn warning: {Warning}", warning);

                    UnloadCurrent();
                    _currentResult = result;
                }
                else
                {
                    _logger?.LogError("Compilation failed with {ErrorCount} errors:", result.Errors.Count);
                    foreach (var error in result.Errors)
                        _logger?.LogError("  {Error}", error);
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
                    _logger?.LogInformation("Unloading previous runtime assembly...");

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

**Step 2: Build to verify**

Run: `dotnet build XafXPODynAssem.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add XafXPODynAssem/XafXPODynAssem.Module/Services/AssemblyGenerationManager.cs
git commit -m "feat: add AssemblyGenerationManager for ALC lifecycle"
```

---

### Task 7: Create SchemaChangeOrchestrator

**Files:**
- Create: `XafXPODynAssem/XafXPODynAssem.Module/Services/SchemaChangeOrchestrator.cs`

**Step 1: Create SchemaChangeOrchestrator.cs**

Adapted from EF Core version. Key difference: no `DbContext.RuntimeEntityTypes` to update. XPO discovers types through TypesInfo and AdditionalExportedTypes.

```csharp
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;

namespace XafXPODynAssem.Module.Services
{
    public class SchemaChangeOrchestrator
    {
        private static readonly Lazy<SchemaChangeOrchestrator> _instance = new(() => new());
        private static readonly SemaphoreSlim _semaphore = new(1, 1);
        private int _schemaVersion;
        private HashSet<string> _previousTypeNames = new();

        public static SchemaChangeOrchestrator Instance => _instance.Value;

        public event Action<int> SchemaChanged;

        public int SchemaVersion => _schemaVersion;

        public bool RestartNeeded { get; private set; }

        public void SetKnownTypeNames(IEnumerable<string> typeNames)
        {
            _previousTypeNames = new HashSet<string>(typeNames);
        }

        public async Task ExecuteHotLoadAsync()
        {
            if (!await _semaphore.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                Tracing.Tracer.LogError("Hot-load timed out waiting for semaphore.");
                return;
            }

            try
            {
                var connStr = XafXPODynAssemModule.RuntimeConnectionString;
                if (string.IsNullOrEmpty(connStr))
                    return;

                // 1. Query current metadata
                var classes = XafXPODynAssemModule.QueryMetadata(connStr);

                if (classes.Count == 0)
                {
                    var hadTypes = _previousTypeNames.Count > 0;
                    _previousTypeNames.Clear();
                    RestartNeeded = hadTypes;
                    var ver = Interlocked.Increment(ref _schemaVersion);
                    SchemaChanged?.Invoke(ver);
                    return;
                }

                // 2. Compile via Roslyn
                var result = XafXPODynAssemModule.AssemblyManager.LoadNewAssembly(classes);
                if (!result.Success)
                {
                    Tracing.Tracer.LogError("Hot-load compilation failed:");
                    foreach (var error in result.Errors)
                        Tracing.Tracer.LogError("  " + error);
                    RestartNeeded = true;
                    var ver = Interlocked.Increment(ref _schemaVersion);
                    SchemaChanged?.Invoke(ver);
                    return;
                }

                // 3. Register types with XAF's TypesInfo
                RegisterTypesInTypesInfo(result.RuntimeTypes);

                // 4. Update module's AdditionalExportedTypes
                XafXPODynAssemModule.Instance?.RefreshRuntimeTypes(result.RuntimeTypes);

                // 5. Always restart — XAF's process-static TypesInfo cannot be reset in-process
                var newTypeNames = new HashSet<string>(result.RuntimeTypes.Select(t => t.Name));
                RestartNeeded = true;
                _previousTypeNames = newTypeNames;

                // 6. Notify (Startup.cs wires this to SignalR + exit code 42)
                var version = Interlocked.Increment(ref _schemaVersion);
                SchemaChanged?.Invoke(version);
            }
            catch (Exception ex)
            {
                Tracing.Tracer.LogError($"Hot-load failed: {ex.Message}");
                RestartNeeded = true;
                SchemaChanged?.Invoke(Interlocked.Increment(ref _schemaVersion));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static void RegisterTypesInTypesInfo(Type[] runtimeTypes)
        {
            foreach (var type in runtimeTypes)
            {
                try
                {
                    XafTypesInfo.Instance.RegisterEntity(type);
                }
                catch (Exception ex)
                {
                    Tracing.Tracer.LogError($"TypesInfo registration failed for {type.Name}: {ex.Message}");
                }
            }
        }
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build XafXPODynAssem.slnx`
Expected: Build will fail — `XafXPODynAssemModule` doesn't have the static members yet. That's OK, we fix it in Task 8.

**Step 3: Commit (even if build fails — commit the service file)**

```bash
git add XafXPODynAssem/XafXPODynAssem.Module/Services/SchemaChangeOrchestrator.cs
git commit -m "feat: add SchemaChangeOrchestrator for hot-load coordination"
```

---

### Task 8: Update Module.cs with Bootstrap Logic

**Files:**
- Modify: `XafXPODynAssem/XafXPODynAssem.Module/Module.cs`

**Step 1: Replace Module.cs with full implementation**

Key changes from EF Core version:
- Uses `Microsoft.Data.SqlClient.SqlConnection` instead of Npgsql
- No `DbContext.RuntimeEntityTypes` to set
- XPO enum columns store integers (Status = 0 for Runtime)
- XPO column names match property names, PK is `Oid`
- No SchemaSynchronizer calls (XPO handles DDL)
- Registers `CustomClass` and `CustomField` in AdditionalExportedTypes

```csharp
using System.Reflection;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Model.Core;
using DevExpress.ExpressApp.Updating;
using DevExpress.ExpressApp.Xpo;
using DevExpress.Persistent.Base;
using Microsoft.Data.SqlClient;
using XafXPODynAssem.Module.BusinessObjects;
using XafXPODynAssem.Module.Services;

namespace XafXPODynAssem.Module
{
    public sealed class XafXPODynAssemModule : ModuleBase
    {
        public static string RuntimeConnectionString { get; set; }

        public static AssemblyGenerationManager AssemblyManager { get; } = new();

        public static XafXPODynAssemModule Instance { get; private set; }

        public static XafApplication CurrentApplication { get; private set; }

        public static bool DegradedMode { get; private set; }

        public static string DegradedModeReason { get; private set; }

        private readonly HashSet<Type> _addedRuntimeTypes = new();

        public static void ResetForRestart()
        {
            AssemblyManager.UnloadCurrent();
            Instance = null;
            CurrentApplication = null;
            DegradedMode = false;
            DegradedModeReason = null;

            XafTypesInfo.HardReset();
            ClearSharedModelManagerCache();
        }

        private static void ClearSharedModelManagerCache()
        {
            try
            {
                var blazorAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "DevExpress.ExpressApp.Blazor");
                if (blazorAsm == null) return;

                var containerType = blazorAsm.GetType(
                    "DevExpress.ExpressApp.AspNetCore.Shared.SharedApplicationModelManagerContainer");
                if (containerType == null) return;

                var instanceField = containerType.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(f => f.FieldType == containerType || f.Name.Contains("instance", StringComparison.OrdinalIgnoreCase));
                if (instanceField != null)
                {
                    instanceField.SetValue(null, null);
                    return;
                }

                foreach (var field in containerType.GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                {
                    if (field.FieldType.Name.Contains("Dictionary") || field.FieldType.Name.Contains("Concurrent"))
                    {
                        var val = field.GetValue(null);
                        var clearMethod = val?.GetType().GetMethod("Clear");
                        clearMethod?.Invoke(val, null);
                    }
                }
            }
            catch { }
        }

        public XafXPODynAssemModule()
        {
            Instance = this;
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.ModelDifference));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.ModelDifferenceAspect));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.BaseObject));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.FileData));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.FileAttachmentBase));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.Event));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.Resource));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.HCategory));
            AdditionalExportedTypes.Add(typeof(BusinessObjects.CustomClass));
            AdditionalExportedTypes.Add(typeof(BusinessObjects.CustomField));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.SystemModule.SystemModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Security.SecurityModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.ConditionalAppearance.ConditionalAppearanceModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Dashboards.DashboardsModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Notifications.NotificationsModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Office.OfficeModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.PivotGrid.PivotGridModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.ReportsV2.ReportsModuleV2));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Scheduler.SchedulerModuleBase));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.TreeListEditors.TreeListEditorsModuleBase));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Validation.ValidationModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.ViewVariantsModule.ViewVariantsModule));
        }

        public override IEnumerable<ModuleUpdater> GetModuleUpdaters(IObjectSpace objectSpace, Version versionFromDB)
        {
            ModuleUpdater updater = new DatabaseUpdate.Updater(objectSpace, versionFromDB);
            return new ModuleUpdater[] { updater };
        }

        public override void Setup(XafApplication application)
        {
            base.Setup(application);
            CurrentApplication = application;

            if (!string.IsNullOrEmpty(RuntimeConnectionString))
            {
                BootstrapRuntimeEntities(application);
            }
        }

        public override void CustomizeTypesInfo(ITypesInfo typesInfo)
        {
            base.CustomizeTypesInfo(typesInfo);
            CalculatedPersistentAliasHelper.CustomizeTypesInfo(typesInfo);
        }

        /// <summary>
        /// Compile runtime types early (before XAF initializes).
        /// Safe to call multiple times — skips if already compiled.
        /// </summary>
        public static void EarlyBootstrap()
        {
            if (string.IsNullOrEmpty(RuntimeConnectionString)) return;

            var classes = QueryMetadata(RuntimeConnectionString);
            if (classes.Count == 0) return;

            if (!AssemblyManager.HasLoadedAssembly || AssemblyManager.RuntimeTypes.Length == 0)
            {
                var result = AssemblyManager.LoadNewAssembly(classes);
                if (!result.Success)
                {
                    Tracing.Tracer.LogError($"[EarlyBootstrap] Compilation failed: {string.Join("; ", result.Errors.Take(3))}");
                }
            }
        }

        private void BootstrapRuntimeEntities(XafApplication application)
        {
            DegradedMode = false;
            DegradedModeReason = null;

            try
            {
                var classes = QueryMetadata(RuntimeConnectionString);
                if (classes.Count == 0)
                {
                    Tracing.Tracer.LogText("No runtime entity metadata found. Skipping compilation.");
                    return;
                }

                Tracing.Tracer.LogText($"Found {classes.Count} runtime class(es). Compiling...");

                Type[] runtimeTypes;
                if (AssemblyManager.HasLoadedAssembly && AssemblyManager.RuntimeTypes.Length > 0)
                {
                    runtimeTypes = AssemblyManager.RuntimeTypes;
                }
                else
                {
                    var result = AssemblyManager.LoadNewAssembly(classes);
                    if (!result.Success)
                    {
                        DegradedMode = true;
                        DegradedModeReason = $"Roslyn compilation failed: {string.Join("; ", result.Errors.Take(3))}";
                        Tracing.Tracer.LogError($"[DEGRADED MODE] {DegradedModeReason}");
                        return;
                    }
                    runtimeTypes = result.RuntimeTypes;
                }

                RefreshRuntimeTypes(runtimeTypes);
                SchemaChangeOrchestrator.Instance.SetKnownTypeNames(runtimeTypes.Select(t => t.Name));

                Tracing.Tracer.LogText($"Runtime entities bootstrapped: {string.Join(", ", runtimeTypes.Select(t => t.Name))}");
            }
            catch (Exception ex)
            {
                DegradedMode = true;
                DegradedModeReason = $"Bootstrap failed: {ex.Message}";
                Tracing.Tracer.LogError($"[DEGRADED MODE] {DegradedModeReason}");
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
        /// Query CustomClass and CustomField metadata directly via SqlClient.
        /// Returns empty list if tables don't exist yet (fresh database).
        /// XPO stores enums as integers: CustomClassStatus.Runtime = 0.
        /// XPO uses "Oid" for the primary key, "GCRecord" for soft delete (NULL = not deleted).
        /// </summary>
        internal static List<CustomClass> QueryMetadata(string connectionString)
        {
            var classes = new List<CustomClass>();

            using var conn = new SqlConnection(connectionString);
            conn.Open();

            // Check if the CustomClass table exists
            using (var checkCmd = new SqlCommand(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'CustomClass') THEN 1 ELSE 0 END",
                conn))
            {
                if ((int)checkCmd.ExecuteScalar() == 0)
                    return classes;
            }

            // Query all runtime classes (Status = 0 = Runtime, GCRecord IS NULL = not deleted)
            var classMap = new Dictionary<Guid, CustomClass>();
            using (var cmd = new SqlCommand(
                @"SELECT [Oid], [ClassName], [NavigationGroup], [Description], [Status]
                  FROM [CustomClass]
                  WHERE [Status] = 0 AND [GCRecord] IS NULL",
                conn))
            {
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var cc = new CustomClass(null) // Detached — for metadata query only
                    {
                        ClassName = reader.GetString(1),
                        NavigationGroup = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                    };
                    var id = reader.GetGuid(0);
                    classMap[id] = cc;
                    classes.Add(cc);
                }
            }

            if (classes.Count == 0)
                return classes;

            // Query all fields for the runtime classes
            var classIds = string.Join(",", classMap.Keys.Select(id => $"'{id}'"));
            using (var cmd = new SqlCommand(
                $@"SELECT [CustomClass], [FieldName], [TypeName], [IsRequired], [IsDefaultField],
                          [Description], [ReferencedClassName], [SortOrder],
                          [IsImmediatePostData], [StringMaxLength],
                          [IsVisibleInListView], [IsVisibleInDetailView], [IsEditable],
                          [ToolTip], [DisplayName]
                   FROM [CustomField]
                   WHERE [CustomClass] IN ({classIds}) AND [GCRecord] IS NULL
                   ORDER BY [SortOrder], [FieldName]",
                conn))
            {
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var classId = reader.GetGuid(0);
                    if (classMap.TryGetValue(classId, out var cc))
                    {
                        cc.Fields.Add(new CustomField(null) // Detached
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
}
```

**Important XPO note for QueryMetadata:** XPO stores the FK for `CustomField.CustomClass` as a column named `[CustomClass]` (the property name), containing the `Oid` (Guid) of the parent `CustomClass`. XPO uses `NULL` for `GCRecord` (not deleted) vs a non-null integer (deleted). The `Status` enum is stored as an integer (0 = Runtime).

**Also important:** `new CustomClass(null)` creates a detached XPO object (no Session). This is acceptable because we only use these objects as DTOs for the metadata query — they are never persisted through this path.

**Step 2: Build to verify**

Run: `dotnet build XafXPODynAssem.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add XafXPODynAssem/XafXPODynAssem.Module/Module.cs
git commit -m "feat: add bootstrap logic, QueryMetadata, and runtime type registration to Module"
```

---

### Task 9: Create Controllers

**Files:**
- Create: `XafXPODynAssem/XafXPODynAssem.Module/Controllers/SchemaChangeController.cs`
- Create: `XafXPODynAssem/XafXPODynAssem.Module/Controllers/CustomFieldDetailController.cs`

**Step 1: Create SchemaChangeController.cs**

```csharp
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.Persistent.Base;
using XafXPODynAssem.Module.BusinessObjects;
using XafXPODynAssem.Module.Services;

namespace XafXPODynAssem.Module.Controllers
{
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
                try
                {
                    await SchemaChangeOrchestrator.Instance.ExecuteHotLoadAsync();
                }
                catch (Exception ex)
                {
                    Tracing.Tracer.LogError($"Deploy schema failed: {ex.Message}");
                }
            });
        }
    }
}
```

**Step 2: Create CustomFieldDetailController.cs**

```csharp
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Editors;
using DevExpress.ExpressApp.Model;
using XafXPODynAssem.Module.BusinessObjects;
using XafXPODynAssem.Module.Services;

namespace XafXPODynAssem.Module.Controllers
{
    public class CustomFieldDetailController : ObjectViewController<DetailView, CustomField>
    {
        protected override void OnActivated()
        {
            base.OnActivated();
            var typeNameItem = View.FindItem("TypeName") as PropertyEditor;
            if (typeNameItem != null)
            {
                typeNameItem.ControlCreated += TypeNameItem_ControlCreated;
            }
        }

        private void TypeNameItem_ControlCreated(object sender, EventArgs e)
        {
            if (sender is PropertyEditor editor)
            {
                var model = editor.Model as IModelCommonMemberViewItem;
                if (model != null)
                {
                    model.PredefinedValues = string.Join(";", SupportedTypes.AllTypeNames);
                }
            }
        }

        protected override void OnDeactivated()
        {
            var typeNameItem = View.FindItem("TypeName") as PropertyEditor;
            if (typeNameItem != null)
            {
                typeNameItem.ControlCreated -= TypeNameItem_ControlCreated;
            }
            base.OnDeactivated();
        }
    }
}
```

**Step 3: Build to verify**

Run: `dotnet build XafXPODynAssem.slnx`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add XafXPODynAssem/XafXPODynAssem.Module/Controllers/
git commit -m "feat: add Deploy Schema and CustomField type dropdown controllers"
```

---

### Task 10: Create RestartService and SchemaUpdateHub

**Files:**
- Create: `XafXPODynAssem/XafXPODynAssem.Blazor.Server/Services/RestartService.cs`
- Create: `XafXPODynAssem/XafXPODynAssem.Blazor.Server/Hubs/SchemaUpdateHub.cs`

**Step 1: Create RestartService.cs**

```csharp
using Microsoft.Extensions.Hosting;

namespace XafXPODynAssem.Blazor.Server.Services
{
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
}
```

**Step 2: Create SchemaUpdateHub.cs**

```csharp
using Microsoft.AspNetCore.SignalR;

namespace XafXPODynAssem.Blazor.Server.Hubs
{
    public class SchemaUpdateHub : Hub
    {
    }
}
```

**Step 3: Build to verify**

Run: `dotnet build XafXPODynAssem.slnx`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add XafXPODynAssem/XafXPODynAssem.Blazor.Server/Services/RestartService.cs XafXPODynAssem/XafXPODynAssem.Blazor.Server/Hubs/SchemaUpdateHub.cs
git commit -m "feat: add RestartService and SignalR hub for schema updates"
```

---

### Task 11: Update Startup.cs with Bootstrap Wiring

**Files:**
- Modify: `XafXPODynAssem/XafXPODynAssem.Blazor.Server/Startup.cs`

**Step 1: Update Startup.cs**

Add the following changes to `ConfigureServices()`:
- Set `RuntimeConnectionString` before XAF initializes
- Call `EarlyBootstrap()` before `services.AddXaf()`

Add to `Configure()`:
- Wire `RestartService`
- Wire `SchemaChangeOrchestrator` to SignalR hub
- Map SignalR hub endpoint

The full updated file should be:

```csharp
using DevExpress.ExpressApp.ApplicationBuilder;
using DevExpress.ExpressApp.Blazor.ApplicationBuilder;
using DevExpress.ExpressApp.Blazor.Services;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.Xpo;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.PermissionPolicy;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.SignalR;
using XafXPODynAssem.Blazor.Server.Hubs;
using XafXPODynAssem.Blazor.Server.Services;

namespace XafXPODynAssem.Blazor.Server
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(typeof(Microsoft.AspNetCore.SignalR.HubConnectionHandler<>), typeof(ProxyHubConnectionHandler<>));

            services.AddRazorPages();
            services.AddServerSideBlazor();
            services.AddHttpContextAccessor();
            services.AddScoped<CircuitHandler, CircuitHandlerProxy>();

            // Set connection string for runtime entity bootstrap (before XAF initializes)
            XafXPODynAssem.Module.XafXPODynAssemModule.RuntimeConnectionString =
                Configuration.GetConnectionString("ConnectionString");

            // Early bootstrap: compile runtime types before XAF init
            XafXPODynAssem.Module.XafXPODynAssemModule.EarlyBootstrap();

            services.AddXaf(Configuration, builder =>
            {
                builder.UseApplication<XafXPODynAssemBlazorApplication>();
                builder.Modules
                    .AddConditionalAppearance()
                    .AddDashboards(options =>
                    {
                        options.DashboardDataType = typeof(DevExpress.Persistent.BaseImpl.DashboardData);
                    })
                    .AddFileAttachments()
                    .AddNotifications()
                    .AddOffice()
                    .AddReports(options =>
                    {
                        options.EnableInplaceReports = true;
                        options.ReportDataType = typeof(DevExpress.Persistent.BaseImpl.ReportDataV2);
                        options.ReportStoreMode = DevExpress.ExpressApp.ReportsV2.ReportStoreModes.XML;
                    })
                    .AddScheduler()
                    .AddValidation(options =>
                    {
                        options.AllowValidationDetailsAccess = false;
                    })
                    .AddViewVariants()
                    .Add<XafXPODynAssem.Module.XafXPODynAssemModule>()
                    .Add<XafXPODynAssemBlazorModule>();
                builder.ObjectSpaceProviders
                    .AddSecuredXpo((serviceProvider, options) =>
                    {
                        string connectionString = null;
                        if (Configuration.GetConnectionString("ConnectionString") != null)
                        {
                            connectionString = Configuration.GetConnectionString("ConnectionString");
                        }
#if EASYTEST
                        if(Configuration.GetConnectionString("EasyTestConnectionString") != null) {
                            connectionString = Configuration.GetConnectionString("EasyTestConnectionString");
                        }
#endif
                        ArgumentNullException.ThrowIfNull(connectionString);
                        options.ConnectionString = connectionString;
                        options.ThreadSafe = true;
                        options.UseSharedDataStoreProvider = true;
                    })
                    .AddNonPersistent();
                builder.Security
                    .UseIntegratedMode(options =>
                    {
                        options.Lockout.Enabled = true;
                        options.RoleType = typeof(PermissionPolicyRole);
                        options.UserType = typeof(XafXPODynAssem.Module.BusinessObjects.ApplicationUser);
                        options.UserLoginInfoType = typeof(XafXPODynAssem.Module.BusinessObjects.ApplicationUserLoginInfo);
                        options.UseXpoPermissionsCaching();
                        options.Events.OnSecurityStrategyCreated += securityStrategy =>
                        {
                            ((SecurityStrategy)securityStrategy).PermissionsReloadMode = PermissionsReloadMode.NoCache;
                        };
                    })
                    .AddPasswordAuthentication(options =>
                    {
                        options.IsSupportChangePassword = true;
                    });
            });
            var authentication = services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            });
            authentication.AddCookie(options =>
            {
                options.LoginPath = "/LoginPage";
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }
            app.UseHttpsRedirection();
            app.UseRequestLocalization();
            app.UseStaticFiles();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseAntiforgery();
            app.UseXaf();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapXafEndpoints();
                endpoints.MapBlazorHub();
                endpoints.MapHub<SchemaUpdateHub>("/schemaUpdateHub");
                endpoints.MapFallbackToPage("/_Host");
                endpoints.MapControllers();
            });

            // Wire RestartService for graceful shutdown
            var lifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();
            RestartService.Configure(lifetime);

            // Wire schema change orchestrator to SignalR hub + exit code 42 restart
            var hubContext = app.ApplicationServices.GetRequiredService<IHubContext<SchemaUpdateHub>>();
            var orchestrator = XafXPODynAssem.Module.Services.SchemaChangeOrchestrator.Instance;
            orchestrator.SchemaChanged += (version) =>
            {
                var needsRestart = orchestrator.RestartNeeded;

                _ = hubContext.Clients.All.SendAsync("SchemaChanged", version, needsRestart);

                if (needsRestart)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);
                        Console.WriteLine("[RESTART] Force-exiting for restart (exit code 42)...");
                        Environment.Exit(42);
                    });
                }
            };
        }
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build XafXPODynAssem.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add XafXPODynAssem/XafXPODynAssem.Blazor.Server/Startup.cs
git commit -m "feat: wire bootstrap, SignalR, and restart logic in Startup"
```

---

### Task 12: Update Program.cs with Exit Code 42 Loop

**Files:**
- Modify: `XafXPODynAssem/XafXPODynAssem.Blazor.Server/Program.cs`

**Step 1: Update Program.cs**

Add the restart service wiring and exit code 42 protocol:

```csharp
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Blazor.DesignTime;
using DevExpress.ExpressApp.Blazor.Services;
using DevExpress.ExpressApp.Design;
using DevExpress.ExpressApp.Utils;
using System.Reflection;
using XafXPODynAssem.Blazor.Server.Services;

namespace XafXPODynAssem.Blazor.Server
{
    public class Program : IDesignTimeApplicationFactory
    {
        static bool ContainsArgument(string[] args, string argument)
        {
            return args.Any(arg => arg.TrimStart('/').TrimStart('-').ToLower() == argument.ToLower());
        }
        public static int Main(string[] args)
        {
            if (ContainsArgument(args, "help") || ContainsArgument(args, "h"))
            {
                Console.WriteLine("Updates the database when its version does not match the application's version.");
                Console.WriteLine();
                Console.WriteLine($"    {Assembly.GetExecutingAssembly().GetName().Name}.exe --updateDatabase [--forceUpdate --silent]");
                Console.WriteLine();
                Console.WriteLine("--forceUpdate - Marks that the database must be updated whether its version matches the application's version or not.");
                Console.WriteLine("--silent - Marks that database update proceeds automatically and does not require any interaction with the user.");
                Console.WriteLine();
                Console.WriteLine($"Exit codes: 0 - {DBUpdaterStatus.UpdateCompleted}");
                Console.WriteLine($"            1 - {DBUpdaterStatus.UpdateError}");
                Console.WriteLine($"            2 - {DBUpdaterStatus.UpdateNotNeeded}");
            }
            else
            {
                DevExpress.ExpressApp.FrameworkSettings.DefaultSettingsCompatibilityMode = DevExpress.ExpressApp.FrameworkSettingsCompatibilityMode.Latest;
                DevExpress.ExpressApp.Security.SecurityStrategy.AutoAssociationReferencePropertyMode = DevExpress.ExpressApp.Security.ReferenceWithoutAssociationPermissionsMode.AllMembers;

                if (ContainsArgument(args, "updateDatabase"))
                {
                    var dbHost = CreateHostBuilder(args).Build();
                    using (var serviceScope = dbHost.Services.CreateScope())
                    {
                        return serviceScope.ServiceProvider.GetRequiredService<DevExpress.ExpressApp.Utils.IDBUpdater>().Update(ContainsArgument(args, "forceUpdate"), ContainsArgument(args, "silent"));
                    }
                }

                // Run the host. Deploy Schema triggers exit code 42 for restart.
                RestartService.ResetRestartFlag();
                IHost host = CreateHostBuilder(args).Build();
                host.Run();

                if (RestartService.IsRestartRequested)
                {
                    Console.WriteLine("[RESTART] Process exiting for restart (exit code 42)...");
                    return 42;
                }
            }
            return 0;
        }
        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
        XafApplication IDesignTimeApplicationFactory.Create()
        {
            IHostBuilder hostBuilder = CreateHostBuilder(Array.Empty<string>());
            return DesignTimeApplicationFactoryHelper.Create(hostBuilder);
        }
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build XafXPODynAssem.slnx`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add XafXPODynAssem/XafXPODynAssem.Blazor.Server/Program.cs
git commit -m "feat: add exit code 42 restart protocol to Program.cs"
```

---

### Task 13: Create run-server.bat Wrapper Script

**Files:**
- Create: `XafXPODynAssem/run-server.bat` (in the solution root)

**Step 1: Create run-server.bat**

This script runs the Blazor Server app in a loop, restarting on exit code 42.

```batch
@echo off
:loop
echo [run-server] Starting XafXPODynAssem.Blazor.Server...
dotnet run --project XafXPODynAssem\XafXPODynAssem.Blazor.Server
if %ERRORLEVEL% == 42 (
    echo [run-server] Exit code 42 detected. Restarting...
    timeout /t 2 /nobreak >nul
    goto loop
)
echo [run-server] Server exited with code %ERRORLEVEL%.
```

**Step 2: Commit**

```bash
git add XafXPODynAssem/run-server.bat
git commit -m "feat: add run-server.bat wrapper for exit code 42 restart"
```

---

### Task 14: Build and Verify Full Solution

**Step 1: Clean build**

Run: `dotnet build XafXPODynAssem.slnx --no-incremental`
Expected: Build succeeded with 0 errors

**Step 2: If build errors occur**

Fix any issues. Common problems:
- Missing `using` statements
- XPO `BaseObject` constructor mismatch (must pass `Session`)
- `NonPersistent` attribute conflicts

**Step 3: Commit any fixes**

```bash
git add -A
git commit -m "fix: resolve build errors"
```

---

### Task 15: Create CLAUDE.md for the XPO Project

**Files:**
- Create: `CLAUDE.md` (in the repository root `C:\Projects\XafXPODynAssem\`)

**Step 1: Create CLAUDE.md**

```markdown
# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository.

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
- `CustomField` (CustomClass, FieldName, TypeName, IsDefaultField, Description)

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
```

**Step 2: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: add CLAUDE.md project guidance"
```

---

## Summary of What's Excluded (vs EF Core Version)

These features exist in the EF Core version but are **not included** in this plan. They can be added as follow-up tasks:

| Feature | Why Excluded |
|---|---|
| **SchemaSynchronizer** | XPO handles DDL automatically via UpdateSchema |
| **DynamicModelCacheKeyFactory** | XPO has no model cache to invalidate |
| **Web API (OData)** | Can be added later; requires `AddXafWebApi()` wiring |
| **AI Chat (AIChatService, SchemaAIToolsProvider)** | Separate feature; port after core system works |
| **Schema Export/Import** | Separate feature; port after core system works |
| **Graduation (GraduationService, GraduateController)** | Separate feature; port after core system works |
| **SchemaHistory** | Audit trail; add when export/import is ported |
| **SchemaPackage** | Non-persistent DTO; add when export/import is ported |
| **SchemaDiscoveryService** | AI integration; port with AI Chat |
| **Playwright tests** | Can be added once the system is running |
