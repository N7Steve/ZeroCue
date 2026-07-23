using System;
using Nefarius.ViGEm.Client;

namespace ZeroCue.DataProbe.Services
{
    public static class ViGEmBusDetector
    {
        public static bool IsAvailable()
        {
            try
            {
                using var client = new ViGEmClient();
                ZeroCueLog.Communication("[VIGEM] Startup prerequisite check succeeded: ViGEmBus is available.");
                return true;
            }
            catch (Exception ex)
            {
                ZeroCueLog.Communication(
                    $"[VIGEM] Startup prerequisite check failed: {ex.GetType().FullName}: {ex.Message}");
                return false;
            }
        }
    }
}
