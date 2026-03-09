using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Xpo;
using DevExpress.Persistent.Base;
using DevExpress.Xpo;
using DevExpress.Xpo.DB;
using DevExpress.Xpo.Metadata;

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

                RegisterTypesInTypesInfo(result.RuntimeTypes);
                XafXPODynAssemModule.Instance?.RefreshRuntimeTypes(result.RuntimeTypes);

                // Create database tables for the new types before restart
                UpdateDatabaseSchema(result.RuntimeTypes);

                var newTypeNames = new HashSet<string>(result.RuntimeTypes.Select(t => t.Name));
                RestartNeeded = true;
                _previousTypeNames = newTypeNames;

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

        internal static void UpdateDatabaseSchema(Type[] runtimeTypes, string connectionString = null)
        {
            try
            {
                var connStr = connectionString ?? XafXPODynAssemModule.RuntimeConnectionString;
                if (string.IsNullOrEmpty(connStr)) return;

                var dict = new ReflectionDictionary();
                foreach (var type in runtimeTypes)
                    dict.GetDataStoreSchema(type);

                using var dataLayer = XpoDefault.GetDataLayer(connStr, dict, AutoCreateOption.DatabaseAndSchema);
                using var session = new Session(dataLayer);
                session.UpdateSchema();
                Tracing.Tracer.LogText($"[SchemaOrchestrator] UpdateSchema completed for {runtimeTypes.Length} type(s)");
            }
            catch (Exception ex)
            {
                Tracing.Tracer.LogError($"[SchemaOrchestrator] UpdateSchema failed: {ex.Message}");
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
