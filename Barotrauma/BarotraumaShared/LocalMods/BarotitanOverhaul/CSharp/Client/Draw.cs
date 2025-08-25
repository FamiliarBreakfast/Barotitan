using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Text.RegularExpressions;
 
// This is required so that the .NET runtime doesn't complain about you trying to access internal Types and Members
// [assembly: IgnoreAccessChecksTo("Barotrauma")]
// [assembly: IgnoreAccessChecksTo("BarotraumaCore")]
// [assembly: IgnoreAccessChecksTo("DedicatedServer")]
namespace BaroTITAN {
    static class HullData
    {
        public static Dictionary<int, List<FluidVolume>> HullVolumes = new();
		private static Dictionary<int, string> DecodeComponents = new();

        private static void UpdateOrAddVolume(int HullID, FluidVolume data)
        {
            if (!HullVolumes.TryGetValue(HullID, out var volumes))
            {
                // No entry yet, create a new one
                volumes = new List<FluidVolume>();
                HullVolumes[HullID] = volumes;
            }

            // Look for existing volume with the same name
            var existing = volumes.FindIndex(v => v.Name == data.Name);

            if (existing >= 0)
            {
                // Replace the old volume
                volumes[existing] = data;
            }
            else
            {
                // Add new unique volume
                volumes.Add(data);
            }   
        }

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
			//LuaCsLogger.Log(data);
            if (data != "")
            {
                try
                {
                    Deserialize(data);
                }
                catch (Exception ex)
                {
                    //LuaCsLogger.Log("Shit gone to fuck!");
                    //LuaCsLogger.Log(ex.Message);
                }
            }
        }

        public static void Deserialize(string data)
        {
            string[] hulls = data.Split('@');
            foreach (string hull in hulls)
            {
                var regex = new Regex(
                    @"^\d+:\d+;(?:[A-Za-z ]+:\d+(?:\.\d+)?:\d+(?:\.\d+)?)(?:;[A-Za-z ]+:\d+(?:\.\d+)?:\d+(?:\.\d+)?)*$");
                // if (hull == "" || hull == "0") //idk why either. maybe im just bad at serializing
                // {
                //     continue;
                // }
                if (regex.IsMatch(hull)) {
                    //LuaCsLogger.Log(hull);
                    string[] hunks = hull.Split(';');
                    string[] header = hunks[0].Split(':');
                    //LuaCsLogger.Log("id" + header[0]);
                    int id = Int32.Parse(header[0]);
                    //LuaCsLogger.Log(header[1]);
                    int count = Int32.Parse(header[1]);

                    string name;
                    float gasmoles;
                    float liquidmoles;

                    for (int i = 1; i <= count; i++)
                    {
                        string[] fluid = hunks[i].Split(':');
                        name = fluid[0];
                        gasmoles = Single.Parse(fluid[1]);
                        liquidmoles = Single.Parse(fluid[2]);

                        //LuaCsLogger.Log(name+gasmoles+liquidmoles);

                        FluidVolume Volume = new(name, gasmoles, liquidmoles);
                        UpdateOrAddVolume(id, Volume);
                    }
                } else
                {
                    //LuaCsLogger.Log("Error when parsing data string: "+hull);
                }
            }
        }
    }

    class FluidVolume
    {
        public string Name;
        public Color Color;
        public double LiquidMoles;
        public double GasMoles;
        public double TotalMoles => LiquidMoles + GasMoles;

        public FluidVolume(string name, float liquidmoles, float gasmoles)
        {
            Name = name;
            LiquidMoles = liquidmoles;
            GasMoles = gasmoles;
        }
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
            if (HullData.HullVolumes.ContainsKey(__instance.ID))
            {
                double fill = 0;
                List<FluidVolume> volumes = HullData.HullVolumes[__instance.ID];
                foreach (FluidVolume volume in volumes)
                {
                    fill += volume.LiquidMoles;
                }

                double fillp = __instance.Rect.Width * __instance.Rect.Height / fill;

                Rectangle drawRect = new Rectangle((int)(__instance.Submarine.DrawPosition.X + __instance.Rect.X),
                    (int)(__instance.Submarine.DrawPosition.Y + __instance.Rect.Y), __instance.Rect.Width,
                    __instance.Rect.Height);

                Barotrauma.GUI.DrawRectangle(spriteBatch, new Vector2(drawRect.X, -drawRect.Y+(float)fillp),
                    new Vector2(__instance.Rect.Width, __instance.Rect.Height-(float)fillp), Color.Red, true, (__instance.ID % 255) * 0.000001f,
                    20.0f);
            }
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
