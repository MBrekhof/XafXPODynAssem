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
