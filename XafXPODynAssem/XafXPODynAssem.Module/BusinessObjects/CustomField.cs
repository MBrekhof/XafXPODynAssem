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
