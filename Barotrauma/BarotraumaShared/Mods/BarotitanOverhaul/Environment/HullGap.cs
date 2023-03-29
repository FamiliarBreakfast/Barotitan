using System;
using System.Runtime;
using System.Reflection;
using HarmonyLib;
using Barotrauma;

public class Example
{
	public static void Main()
	{
		var harmony = new Harmony("com.anikyte.barotitan.hulls");
		harmony.PatchAll();
	}
	public static void Unpatch()
	{
		harmony.UnpatchAll("com.anikyte.barotitan.hulls");
	}
}

[HarmonyPatch]
class Patch
{
	public static MethodBase TargetMethod()
	{
		Type[] args = { AccessTools.TypeByName("Rectangle"),
		                AccessTools.TypeByName("Submarine"),
		                ushort }
		return AccessTools.Method(AccessTools.TypeByName("Hull"), "Hull", args);
	}
	static void Postfix(ref string __result)
	{
		__result = $"[MODDY] ";
	}
}
