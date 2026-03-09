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
