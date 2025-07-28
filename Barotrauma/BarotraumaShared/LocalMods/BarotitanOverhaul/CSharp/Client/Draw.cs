using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
 
// This is required so that the .NET runtime doesn't complain about you trying to access internal Types and Members
// [assembly: IgnoreAccessChecksTo("Barotrauma")]
// [assembly: IgnoreAccessChecksTo("BarotraumaCore")]
// [assembly: IgnoreAccessChecksTo("DedicatedServer")]
namespace BaroTITAN {
    static class HullData
    {
        public static Dictionary<Hull, List<FluidVolume>> HullVolumes = new();
		private static Dictionary<int, string> DecodeComponents = new();

		private static void UpdateOrAddVolume(int HullID, FluidVolume data) {}

		//private static FluidVolume VolumeFromPackedData(string data) {}

		public static string Base64Decode(string base64EncodedData) 
	    {
	        var base64EncodedBytes = System.Convert.FromBase64String(base64EncodedData);
	        return System.Text.Encoding.UTF8.GetString(base64EncodedBytes);
	    }

		public static void DecodeData(){
			DecodeComponents.Clear();
			foreach (Item item in Item.ItemList) //todo: optimize
	        {
	            if (item.HasTag("FluidNetworkingData"))
	            {
	                Barotrauma.Items.Components.MemoryComponent mem = item.GetComponent<Barotrauma.Items.Components.MemoryComponent>();
	                if (mem.Value != "")
	                {
	                    string[] chunks = mem.Value.Split('~');
	                    DecodeComponents[int.Parse(chunks[0])] = chunks[1];
	                }
	            }
	        }
	
	        string data = "";
	        for (int i = 0; i < DecodeComponents.Count; i++)
	        {
	            data += DecodeComponents[i];
	        }
			LuaCsLogger.Log(data);
			//deconstruct data here and create/update fluid volumes
		}
    }

    class FluidVolume
    {
        public string Name;
        public Color Color;
        public double LiquidMoles;
        public double GasMoles;
        public double TotalMoles => LiquidMoles + GasMoles;
    }
    
    partial class ClientMain : IAssemblyPlugin {
        public Harmony harmony;
        public void Initialize()
        {
            LuaCsLogger.Log("BaroTITAN client assembly loaded.");
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
            harmony.UnpatchSelf();
            harmony = null;
            LuaCsLogger.Log("BaroTITAN unloaded.");
        }
    }

	[HarmonyPatch(typeof(Barotrauma.GameScreen), nameof(Barotrauma.GameScreen.DrawMap))]
	class Update
	{
		static void Postfix(GraphicsDevice graphics, SpriteBatch spriteBatch, double deltaTime) {
			//LuaCsLogger.Log("BaroTITAN unloaded.");
			HullData.DecodeData();
		}
	}

    [HarmonyPatch(typeof(Barotrauma.Hull), nameof(Barotrauma.Hull.Draw))]
    class WaterDraw
    {
        static void Prefix(SpriteBatch spriteBatch, bool editing, bool back, ref Barotrauma.Hull __instance)
        {
            Rectangle drawRect = new Rectangle((int)(__instance.Submarine.DrawPosition.X + __instance.Rect.X), (int)(__instance.Submarine.DrawPosition.Y + __instance.Rect.Y), __instance.Rect.Width, __instance.Rect.Height);

            Barotrauma.GUI.DrawRectangle(spriteBatch, new Vector2(drawRect.X, -drawRect.Y),
                new Vector2(__instance.Rect.Width, __instance.Rect.Height), Color.Red, true, (__instance.ID % 255) * 0.000001f, 20.0f);
            //todo get from HullData.HullVolumes
            //foreach (FluidVolume volume in HullData.HullVolumes[__instance])
            //{
                //do something
            //}
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
