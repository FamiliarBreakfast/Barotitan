using System;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
 
// This is required so that the .NET runtime doesn't complain about you trying to access internal Types and Members
// [assembly: IgnoreAccessChecksTo("Barotrauma")]
// [assembly: IgnoreAccessChecksTo("BarotraumaCore")]
// [assembly: IgnoreAccessChecksTo("DedicatedServer")]
namespace BaroTITAN {
    partial class TestDraw : IAssemblyPlugin {
        public Harmony harmony;
        public void Initialize()
        {
            // When your plugin is loading, use this instead of the constructor
            // Put any code here that does not rely on other plugins.
            LuaCsLogger.Log("ExampleMod loaded!");
            Console.WriteLine("urine");
            harmony = new Harmony("barotitan.client");

            // harmony.Patch(
            //     original: typeof(InteractionLabelManager).GetMethod("DrawLabelForItem", AccessTools.all),
            //     prefix: new HarmonyMethod(typeof(ConditionBarsForInteractionLabels).GetMethod("DrawLabelForItem"))
            // );
            harmony.PatchAll();
        }
 
        public void OnLoadCompleted()
        {
            // After all plugins have loaded
            // Put code that interacts with other plugins here.
        }
 
        public void PreInitPatching()
        {
            // Not yet supported: Called during the Barotrauma startup phase before vanilla content is loaded.
        }
 
        public void Dispose()
        {
            // Cleanup your plugin!
            harmony.UnpatchSelf();
            harmony = null;
            LuaCsLogger.Log("ExampleMod disposed!");
        }
    }

    [HarmonyPatch(typeof(Barotrauma.Hull), nameof(Barotrauma.Hull.Draw))]
    class WaterDraw
    {
        static void Prefix(SpriteBatch spriteBatch, bool editing, bool back, ref Barotrauma.Hull __instance)
        {
            // LuaCsLogger.Log("wgfer");
            // Console.WriteLine("utgwqrrine");
            // Barotrauma.GUIStyle.SmallFont.DrawString(spriteBatch, "TESTTESTTEST", __instance.Submarine.DrawPosition,
            //     Color.Red);
            // Barotrauma.GUI.DrawFilledRectangle(spriteBatch, __instance.Submarine.DrawPosition, new Vector2(10000, 10000), Color.Red, 0);
            Rectangle drawRect = new Rectangle((int)(__instance.Submarine.DrawPosition.X + __instance.Rect.X), (int)(__instance.Submarine.DrawPosition.Y + __instance.Rect.Y), __instance.Rect.Width, __instance.Rect.Height);
            //Barotrauma.GUI.DrawRectangle(spriteBatch, new Rectangle(-1000, -1000, 1000, 1000), Color.Red, true, 0.0f); //works!!!!!
            Barotrauma.GUI.DrawRectangle(spriteBatch, new Vector2(drawRect.X, -drawRect.Y),
                new Vector2(__instance.Rect.Width, __instance.Rect.Height), Color.Red, true, (__instance.ID % 255) * 0.000001f, 20.0f);
        }
    }

    [HarmonyPatch(typeof(Barotrauma.Hull), nameof(Barotrauma.Hull.IsVisible))]
    class MakeVisible
    {
        static bool Prefix(Rectangle worldView, ref bool __result, ref Barotrauma.Hull __instance)
        {
            //__result = base.IsVisible(worldView); //todo fix check and optimize
            __result = true;
            return false;
        }
    }
}
