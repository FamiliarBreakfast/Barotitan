if CLIENT or Game.IsSingleplayer then
print("test")

    --Networking.Receive("fluidSync", function(message)
        --print(message.ReadString()) --decode fluid data and update graphics
    --end)
    
    --fluid data
    --create rectangles based on fluid data
    
--     GUI = LuaUserData.CreateStatic('Barotrauma.GUI', true)
--     GUIStyle = LuaUserData.CreateStatic('Barotrauma.GUIStyle', true)
--     Hook.Patch("Barotrauma.Hull", "Draw", function(instance, ptable)
--         spriteBatch = ptable["spriteBatch"]
--         drawPos = instance.Submarine.DrawPosition
-- --      print(drawPos.x)
-- --      print(drawPos.y)
--         GUIStyle.SmallFont.DrawString(spriteBatch, "TESTTESTTEST", drawPos, Color.Red)
--         GUI.DrawRectangle(spriteBatch, Vector2(drawPos.x+instance.Rect.X,drawPos.y+instance.Rect.Y), Vector2(1000, 1000), Color(255, 0, 0, 255), true, 0, 100)
--     end, Hook.HookMethodType.After)
    
end
