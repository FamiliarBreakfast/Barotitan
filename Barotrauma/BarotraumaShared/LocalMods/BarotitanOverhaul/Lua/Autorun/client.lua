Hook.Patch(
	"Barotrauma.Level",
	"DrawDebugOverlay",
	{
		"Microsoft.Xna.Framework.Graphics.SpriteBatch",
		"Barotrauma.Camera"
	},
	function(instance, ptable)
		ptable.PreventExecution = true
	end, Hook.HookMethodType.Before)
