if CLIENT and Game.IsSingleplayer then
    --Networking.Receive("fluidSync", function(message)
        --print(message.ReadString()) --decode fluid data and update graphics
    --end)
    
    --fluid data
    --create rectangles based on fluid data
    Hook.Add("think", "DrawHullOverlay", function()
    if not Game.IsMultiplayer or CLIENT then
        local hull = Character.Controlled.CurrentHull
        if not hull then return end

        -- Calculate rectangle size (e.g., 50% width and height of the hull)
        local width = hull.Rect.Width * 0.5
        local height = hull.Rect.Height * 0.5

        -- Position it in the center of the hull
        local x = hull.Rect.X + (hull.Rect.Width - width) / 2
        local y = hull.Rect.Y + (hull.Rect.Height - height) / 2

        -- Draw the rectangle (RGBA: red with 100 alpha = semi-transparent)
        GUI.DrawRectangle(Rect(x, y, width, height), Color(255, 0, 0, 100), true)
    end
end)

end
