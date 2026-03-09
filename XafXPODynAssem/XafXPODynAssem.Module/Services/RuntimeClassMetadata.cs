namespace XafXPODynAssem.Module.Services
{
    public class RuntimeClassMetadata
    {
        public string ClassName { get; set; }
        public string NavigationGroup { get; set; }
        public string Description { get; set; }
        public bool IsApiExposed { get; set; }
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
