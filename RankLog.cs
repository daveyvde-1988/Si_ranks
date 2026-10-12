using MelonLoader;

namespace SiPlayerRanks
{
    // Every mod-owned console/file log, including errors, passes this gate.
    internal static class RankLog
    {
        internal static bool Enabled=false;
        internal static void Info(string text) { if (Enabled) MelonLogger.Msg(text); }
        internal static void Warning(string text) { if (Enabled) MelonLogger.Warning(text); }
        internal static void Error(string text) { if (Enabled) MelonLogger.Error(text); }
    }
}
