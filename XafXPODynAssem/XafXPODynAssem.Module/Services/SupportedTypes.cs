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
