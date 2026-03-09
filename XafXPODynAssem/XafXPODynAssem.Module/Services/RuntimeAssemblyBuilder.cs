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
            var fields = cc.AllFields
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
            var defaultField = cc.AllFields.FirstOrDefault(f => f.IsDefaultField);
            if (defaultField != null) return defaultField;

            defaultField = cc.AllFields
                .Where(f => f.TypeName == "System.String" && !string.IsNullOrWhiteSpace(f.FieldName))
                .OrderBy(f => f.SortOrder)
                .FirstOrDefault();
            if (defaultField != null) return defaultField;

            return cc.AllFields
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
            return char.ToLowerInvariant(name[0]) + name[1..];
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
