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

            try
            {
                var classes = QueryMetadata(RuntimeConnectionString);
                if (classes.Count == 0) return;

                if (!AssemblyManager.HasLoadedAssembly || AssemblyManager.RuntimeTypes.Length == 0)
                {
                    var result = AssemblyManager.LoadNewAssembly(classes);
                    if (!result.Success)
                    {
                        Tracing.Tracer.LogError($"[EarlyBootstrap] Compilation failed: {string.Join("; ", result.Errors.Take(3))}");
                    }
                    else
                    {
                        Tracing.Tracer.LogText($"[EarlyBootstrap] Compiled {result.RuntimeTypes.Length} runtime type(s): {string.Join(", ", result.RuntimeTypes.Select(t => t.Name))}");

                        // Ensure database tables exist for the compiled types
                        SchemaChangeOrchestrator.UpdateDatabaseSchema(result.RuntimeTypes, RuntimeConnectionString);
                    }
                }
            }
            catch (Exception ex)
            {
                // Non-fatal: database may not exist yet on first run
                Tracing.Tracer.LogText($"[EarlyBootstrap] Skipped: {ex.Message}");
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
        internal static List<RuntimeClassMetadata> QueryMetadata(string connectionString)
        {
            var classes = new List<RuntimeClassMetadata>();

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
            var classMap = new Dictionary<Guid, RuntimeClassMetadata>();
            using (var cmd = new SqlCommand(
                @"SELECT [Oid], [ClassName], [NavigationGroup], [Description], [Status]
                  FROM [CustomClass]
                  WHERE [Status] = 0 AND [GCRecord] IS NULL",
                conn))
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
}
