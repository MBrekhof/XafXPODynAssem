using System.Text;
using XafXPODynAssem.Module.BusinessObjects;

namespace XafXPODynAssem.Module.Services
{
    /// <summary>
    /// Generates production-quality XPO C# source code for graduating a runtime entity
    /// to compiled code.
    /// </summary>
    public static class GraduationService
    {
        /// <summary>
        /// Graduate a CustomClass: generate production source, set GraduatedSource, set Status to Compiled.
        /// Returns the generated source string.
        /// </summary>
        public static string Graduate(CustomClass cc)
        {
            var source = GenerateSource(cc);
            cc.GraduatedSource = source;
            cc.Status = CustomClassStatus.Compiled;
            return source;
        }

        private static string GenerateSource(CustomClass cc)
        {
            var sb = new StringBuilder();

            sb.AppendLine("// ============================================================");
            sb.AppendLine($"// Graduated Entity: {cc.ClassName}");
            sb.AppendLine($"// Generated from runtime entity on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("// ============================================================");
            sb.AppendLine();

            // Section 1: Entity class
            sb.AppendLine("// --- Entity Class ---");
            sb.AppendLine("// Place this file in your BusinessObjects folder.");
            sb.AppendLine();
            sb.Append(GenerateEntityClass(cc));
            sb.AppendLine();

            // Section 2: XPO note
            sb.AppendLine("// --- XPO Note ---");
            sb.AppendLine($"// The table \"{cc.ClassName}\" already exists in the database.");
            sb.AppendLine("// XPO will use the existing table automatically when this class is loaded.");
            sb.AppendLine("// No migration step is needed.");

            // Section 3: Web API note (if applicable)
            if (cc.IsApiExposed)
            {
                sb.AppendLine();
                sb.AppendLine("// --- Web API Note ---");
                sb.AppendLine("// This entity was API-exposed at runtime.");
                sb.AppendLine($"// Add options.BusinessObject<{cc.ClassName}>() to AddWebApi in Startup.cs.");
            }

            return sb.ToString();
        }

        private static string GenerateEntityClass(CustomClass cc)
        {
            var sb = new StringBuilder();

            sb.AppendLine("using System;");
            sb.AppendLine("using System.ComponentModel;");
            sb.AppendLine("using DevExpress.ExpressApp;");
            sb.AppendLine("using DevExpress.Persistent.Base;");
            sb.AppendLine("using DevExpress.Persistent.BaseImpl;");
            sb.AppendLine("using DevExpress.Xpo;");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(cc.Description))
            {
                sb.AppendLine("/// <summary>");
                sb.AppendLine($"/// {EscapeXmlComment(cc.Description)}");
                sb.AppendLine("/// </summary>");
            }

            if (!cc.GenerateAsPartial)
            {
                sb.AppendLine("[DefaultClassOptions]");
                if (!string.IsNullOrWhiteSpace(cc.NavigationGroup))
                    sb.AppendLine($"[NavigationItem(\"{EscapeString(cc.NavigationGroup)}\")]");

                var defaultField = FindDefaultProperty(cc);
                if (defaultField != null)
                    sb.AppendLine($"[DefaultProperty(\"{defaultField.FieldName}\")]");
            }

            var partial = cc.GenerateAsPartial ? "partial " : "";
            sb.AppendLine($"public {partial}class {cc.ClassName} : BaseObject");
            sb.AppendLine("{");

            // XPO Session constructor
            sb.AppendLine($"    public {cc.ClassName}(Session session) : base(session) {{ }}");
            sb.AppendLine();

            // Generate properties
            var fields = cc.Fields
                .Cast<CustomField>()
                .Where(f => !string.IsNullOrWhiteSpace(f.FieldName))
                .OrderBy(f => f.SortOrder)
                .ThenBy(f => f.FieldName);

            foreach (var field in fields)
            {
                if (!string.IsNullOrWhiteSpace(field.Description))
                {
                    sb.AppendLine($"    /// <summary>{EscapeXmlComment(field.Description)}</summary>");
                }

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
            sb.AppendLine($"    {backingFieldType} {backingFieldName};");

            // Attributes
            EmitFieldAttributes(sb, field);

            if (field.TypeName == "System.String" && field.StringMaxLength.HasValue)
                sb.AppendLine($"    [Size({field.StringMaxLength.Value})]");
            else if (field.TypeName == "System.String")
                sb.AppendLine("    [Size(SizeAttribute.DefaultStringMappingFieldSize)]");

            if (field.TypeName == "System.Byte[]")
                sb.AppendLine("    [Size(SizeAttribute.Unlimited)]");

            // Property with SetPropertyValue
            sb.AppendLine($"    public {backingFieldType} {field.FieldName}");
            sb.AppendLine("    {");
            sb.AppendLine($"        get => {backingFieldName};");
            sb.AppendLine($"        set => SetPropertyValue(nameof({field.FieldName}), ref {backingFieldName}, value);");
            sb.AppendLine("    }");
        }

        private static void EmitReferenceProperty(StringBuilder sb, CustomField field)
        {
            var refTypeName = field.ReferencedClassName;
            var backingFieldName = ToCamelCase(field.FieldName);

            // Backing field
            sb.AppendLine($"    {refTypeName} {backingFieldName};");

            // Attributes
            EmitFieldAttributes(sb, field);

            // Property with SetPropertyValue
            sb.AppendLine($"    public {refTypeName} {field.FieldName}");
            sb.AppendLine("    {");
            sb.AppendLine($"        get => {backingFieldName};");
            sb.AppendLine($"        set => SetPropertyValue(nameof({field.FieldName}), ref {backingFieldName}, value);");
            sb.AppendLine("    }");
        }

        private static void EmitFieldAttributes(StringBuilder sb, CustomField field)
        {
            if (field.IsImmediatePostData)
                sb.AppendLine("    [ImmediatePostData]");
            if (!field.IsVisibleInListView)
                sb.AppendLine("    [VisibleInListView(false)]");
            if (!field.IsVisibleInDetailView)
                sb.AppendLine("    [VisibleInDetailView(false)]");
            if (!field.IsEditable)
                sb.AppendLine("    [DevExpress.ExpressApp.Editors.Editable(false)]");
            if (!string.IsNullOrWhiteSpace(field.ToolTip))
                sb.AppendLine($"    [ToolTip(\"{EscapeString(field.ToolTip)}\")]");
            if (!string.IsNullOrWhiteSpace(field.DisplayName))
                sb.AppendLine($"    [DisplayName(\"{EscapeString(field.DisplayName)}\")]");
        }

        private static CustomField FindDefaultProperty(CustomClass cc)
        {
            var fields = cc.Fields.Cast<CustomField>().ToList();

            var defaultField = fields.FirstOrDefault(f => f.IsDefaultField);
            if (defaultField != null) return defaultField;

            defaultField = fields
                .Where(f => f.TypeName == "System.String" && !string.IsNullOrWhiteSpace(f.FieldName))
                .OrderBy(f => f.SortOrder)
                .FirstOrDefault();
            if (defaultField != null) return defaultField;

            return fields
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

        private static string EscapeXmlComment(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
