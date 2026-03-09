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

        [Association("CustomClass-Fields"), DevExpress.Xpo.Aggregated]
        public new XPCollection<CustomField> Fields => GetCollection<CustomField>(nameof(Fields));

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
