using HarmonyLib;
using System.IO;
using System.Reflection;
using KMod;

namespace DrainValve
{
    public class DrainValveMod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            LocString.CreateLocStringKeys(typeof(STRINGS), "DrainValve");
            Localization.RegisterForTranslation(typeof(STRINGS));
            base.OnLoad(harmony);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    [HarmonyPatch(typeof(Localization), "Initialize")]
    public class LocalizationInitializePatch
    {
        public static void Postfix()
        {
            Localization.RegisterForTranslation(typeof(STRINGS));

            var locale = Localization.GetLocale();
            if (locale != null)
            {
                string path = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    "translations",
                    locale.Code + ".po");
                if (File.Exists(path))
                {
                    var strings = Localization.LoadStringsFile(path, false);
                    Localization.OverloadStrings(strings);
                }
            }

            LocString.CreateLocStringKeys(typeof(STRINGS), "DrainValve");
        }
    }
}
