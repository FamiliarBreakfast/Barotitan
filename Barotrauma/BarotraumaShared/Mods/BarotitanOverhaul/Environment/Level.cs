using System;
using System.Runtime;
using System.Reflection;
using HarmonyLib;
using Barotrauma;

public class Example
{
	public static void Main()
	{
		var harmony = new Harmony("com.anikyte.barotitan.level");
		harmony.PatchAll();
	}
	public static void Unpatch()
	{
		harmony.UnpatchAll("com.anikyte.barotitan.level");
	}
}

[HarmonyPatch]
class Patch
{
	public static MethodBase TargetMethods()
	{
		var type = AccessTools.TypeByName("ChatMessage");
		return AccessTools.Method(type, "GetTimeStamp");
	}
	static void Postfix(ref string __result)
	{
		__result = $"[MODDY] ";
	}
}
