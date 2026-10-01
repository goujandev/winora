namespace Winora;

public static class AppBuild
{
    public static bool IsTilingTest { get; private set; }
    public static bool AllowTilingEffects => !IsDevelopment || IsTilingTest;
    internal static void EnableTilingTest()
    {
        if (!IsDevelopment) throw new InvalidOperationException("Tiling test mode is only available in Winora Dev.");
        IsTilingTest = true;
    }
    public static bool IsDevelopment
    {
        get
        {
#if WINORA_DEV
            return true;
#else
            return false;
#endif
        }
    }
    public static string Name => IsDevelopment ? "Winora Dev" : "Winora";
    public static string DataFolder => IsDevelopment ? "WinoraDev" : "Winora";
    public static string InstanceMutex => IsDevelopment ? @"Local\Winora.Dev.Settings" : @"Local\Winora.Settings";
}
