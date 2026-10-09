using Microsoft.Win32;

namespace Lexi.Services;

public static class OverlayMaterialPolicy
{
    public static bool TransparencyEnabled
    {
        get
        {
            if(Environment.GetEnvironmentVariable("LEXI_SOLID_OVERLAYS")=="1") return false;
            if(!OperatingSystem.IsWindows()) return false;
            try { using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("EnableTransparency") is not int enabled || enabled!=0; }
            catch { return false; }
        }
    }
}
