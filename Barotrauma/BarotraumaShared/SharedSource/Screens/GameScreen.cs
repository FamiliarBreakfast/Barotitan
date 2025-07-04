﻿//#define RUN_PHYSICS_IN_SEPARATE_THREAD

using Microsoft.Xna.Framework;
using System.Threading;
using FarseerPhysics.Dynamics;
using FarseerPhysics;


#if DEBUG && CLIENT
using System;
using Barotrauma.Sounds;
using Microsoft.Xna.Framework.Input;
#endif

namespace Barotrauma
{
    partial class GameScreen : Screen
    {
        private object updateLock = new object();
        private double physicsTime;

#if CLIENT
        private readonly Camera cam;

        public override Camera Cam
        {
            get { return cam; }
        }
#elif SERVER
        public override Camera Cam
        {
            get { return Camera.Instance; }
        }
#endif

        public double GameTime
        {
            get;
            private set;
        }
        
        public GameScreen()
        {
#if CLIENT
            cam = new Camera();
            cam.Translate(new Vector2(-10.0f, 50.0f));
#endif
        }

        public override void Select()
        {
            base.Select();

#if CLIENT
            if (Character.Controlled != null)
            {
                cam.Position = Character.Controlled.WorldPosition;
                cam.UpdateTransform(true);
            }
            else if (Submarine.MainSub != null)
            {
                cam.Position = Submarine.MainSub.WorldPosition;
                cam.UpdateTransform(true);
            }
            GameMain.GameSession?.CrewManager?.ResetCrewListOpenState();
            ChatBox.ResetChatBoxOpenState();
            
#endif

            MapEntity.ClearHighlightedEntities();

#if RUN_PHYSICS_IN_SEPARATE_THREAD
            var physicsThread = new Thread(ExecutePhysics)
            {
                Name = "Physics thread",
                IsBackground = true
            };
            physicsThread.Start();
#endif
        }

        public override void Deselect()
        {
            base.Deselect();
#if CLIENT
            var config = GameSettings.CurrentConfig;
            config.CrewMenuOpen = CrewManager.PreferCrewMenuOpen;
            config.ChatOpen = ChatBox.PreferChatBoxOpen;
            GameSettings.SetCurrentConfig(config);
            GameSettings.SaveCurrentConfig();
            GameMain.SoundManager.SetCategoryMuffle(Sounds.SoundManager.SoundCategoryDefault, false);
            GUI.ClearMessages();
#if !DEBUG
            if (GameMain.GameSession?.GameMode is TestGameMode)
            {
                DebugConsole.DeactivateCheats();
            }
#endif
#endif
        }
        /// <summary>
        /// Allows the game to run logic such as updating the world,
        /// checking for collisions, gathering input, and playing audio.
        /// </summary>
        public override void Update(double deltaTime)
        {
#if RUN_PHYSICS_IN_SEPARATE_THREAD
            physicsTime += deltaTime;
            lock (updateLock)
            { 
#endif


#if DEBUG && CLIENT
            if (GameMain.GameSession != null && !DebugConsole.IsOpen && GUI.KeyboardDispatcher.Subscriber == null)
            {
                if (GameMain.GameSession.Level != null && GameMain.GameSession.Submarine != null)
                {
                    Submarine closestSub = Submarine.FindClosest(cam.WorldViewCenter) ?? GameMain.GameSession.Submarine;

                    Vector2 targetMovement = Vector2.Zero;
                    if (PlayerInput.KeyDown(Keys.I)) { targetMovement.Y += 1.0f; }
                    if (PlayerInput.KeyDown(Keys.K)) { targetMovement.Y -= 1.0f; }
                    if (PlayerInput.KeyDown(Keys.J)) { targetMovement.X -= 1.0f; }
                    if (PlayerInput.KeyDown(Keys.L)) { targetMovement.X += 1.0f; }

                    if (targetMovement != Vector2.Zero)
                    {
                        closestSub.ApplyForce(targetMovement * closestSub.SubBody.Body.Mass * 100.0f);
                    }
                }
            }
#endif

#if CLIENT
            GameMain.LightManager?.Update((float)deltaTime);
#endif

            GameTime += deltaTime;

            foreach (PhysicsBody body in PhysicsBody.List)
            {
                //update character (colliders) regardless if they're enabled or not, so that the draw position is updated
                //necessary to sync the character's position even if the character is ragdolled and the collider is disabled
                if ((body.Enabled || body.UserData is Character) && 
                    body.BodyType != BodyType.Static) 
                { 
                    body.Update(); 
                }               
            }
            MapEntity.ClearHighlightedEntities();

#if CLIENT
            var sw = new System.Diagnostics.Stopwatch();
            sw.Start();
#endif

            GameMain.GameSession?.Update((float)deltaTime);

#if CLIENT
            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Update:GameSession", sw.ElapsedTicks);
            sw.Restart();

            GameMain.ParticleManager.Update((float)deltaTime); 
            
            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Update:Particles", sw.ElapsedTicks);
            sw.Restart();  

            if (Level.Loaded != null) Level.Loaded.Update((float)deltaTime, cam);

            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Update:Level", sw.ElapsedTicks);

            if (Character.Controlled is { } controlled)
            {
                if (controlled.SelectedItem != null && controlled.CanInteractWith(controlled.SelectedItem))
                {
                    controlled.SelectedItem.UpdateHUD(cam, controlled, (float)deltaTime);
                }
                if (controlled.Inventory != null)
                {
                    foreach (Item item in controlled.Inventory.AllItems)
                    {
                        if (controlled.HasEquippedItem(item))
                        {
                            item.UpdateHUD(cam, controlled, (float)deltaTime);
                        }
                    }
                }
            }

            sw.Restart();

            Character.UpdateAll((float)deltaTime, cam);
#elif SERVER
            if (Level.Loaded != null) Level.Loaded.Update((float)deltaTime, Camera.Instance);
            Character.UpdateAll((float)deltaTime, Camera.Instance);
#endif


#if CLIENT
            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Update:Character", sw.ElapsedTicks);
            sw.Restart(); 
#endif

            StatusEffect.UpdateAll((float)deltaTime);

#if CLIENT
            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Update:StatusEffects", sw.ElapsedTicks);
            sw.Restart(); 

            if (Character.Controlled != null && 
                Lights.LightManager.ViewTarget != null)
            {
                Vector2 targetPos = Lights.LightManager.ViewTarget.WorldPosition;
                if (Lights.LightManager.ViewTarget == Character.Controlled)
                {
                    //take the NetworkPositionErrorOffset into account, meaning the camera is positioned
                    //where we've smoothed out the draw position of the character after a positional correction,
                    //instead of where the character's collider actually is
                    targetPos += ConvertUnits.ToDisplayUnits(Character.Controlled.AnimController.Collider.NetworkPositionErrorOffset);
                    if (CharacterHealth.OpenHealthWindow != null || CrewManager.IsCommandInterfaceOpen || ConversationAction.IsDialogOpen)
                    {
                        Vector2 screenTargetPos = new Vector2(GameMain.GraphicsWidth, GameMain.GraphicsHeight) * 0.5f;
                        if (CharacterHealth.OpenHealthWindow != null)
                        {
                            screenTargetPos.X = GameMain.GraphicsWidth * (CharacterHealth.OpenHealthWindow.Alignment == Alignment.Left ? 0.6f : 0.4f);
                        }
                        else if (ConversationAction.IsDialogOpen)
                        {
                            screenTargetPos.Y = GameMain.GraphicsHeight * 0.4f;
                        }
                        Vector2 screenOffset = screenTargetPos - new Vector2(GameMain.GraphicsWidth / 2, GameMain.GraphicsHeight / 2);
                        screenOffset.Y = -screenOffset.Y;
                        targetPos -= screenOffset / cam.Zoom;
                    }
                }
                cam.TargetPos = targetPos;
            }

            cam.MoveCamera((float)deltaTime, allowZoom: GUI.MouseOn == null && !Inventory.IsMouseOnInventory);

            Character.Controlled?.UpdateLocalCursor(cam);
#endif

            foreach (Submarine sub in Submarine.Loaded)
            {
                sub.SetPrevTransform(sub.Position);
            }

            foreach (PhysicsBody body in PhysicsBody.List)
            {
                if (body.Enabled && body.BodyType != FarseerPhysics.BodyType.Static) 
                { 
                    body.SetPrevTransform(body.SimPosition, body.Rotation); 
                }
            }

#if CLIENT
            MapEntity.UpdateAll((float)deltaTime, cam);
#elif SERVER
            MapEntity.UpdateAll((float)deltaTime, Camera.Instance);
#endif

#if CLIENT
            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Update:MapEntity", sw.ElapsedTicks);
            sw.Restart(); 
#endif
            Character.UpdateAnimAll((float)deltaTime);

#if CLIENT
            Ragdoll.UpdateAll((float)deltaTime, cam);
#elif SERVER
            Ragdoll.UpdateAll((float)deltaTime, Camera.Instance);
#endif

#if CLIENT
            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Update:Ragdolls", sw.ElapsedTicks);
            sw.Restart(); 
#endif

            foreach (Submarine sub in Submarine.Loaded)
            {
                sub.Update((float)deltaTime);
            }

#if CLIENT
            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Update:Submarine", sw.ElapsedTicks);
            sw.Restart();
#endif

#if !RUN_PHYSICS_IN_SEPARATE_THREAD
            try
            {
                GameMain.World.Step((float)Timing.Step);
            }
            catch (WorldLockedException e)
            {
                string errorMsg = "Attempted to modify the state of the physics simulation while a time step was running.";
                DebugConsole.ThrowError(errorMsg, e);
                GameAnalyticsManager.AddErrorEventOnce("GameScreen.Update:WorldLockedException" + e.Message, GameAnalyticsManager.ErrorSeverity.Critical, errorMsg);
            }
#endif


#if CLIENT
            sw.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Update:Physics", sw.ElapsedTicks);
#endif
            UpdateProjSpecific(deltaTime);

#if RUN_PHYSICS_IN_SEPARATE_THREAD
            }
#endif
        }

        partial void UpdateProjSpecific(double deltaTime);

        private void ExecutePhysics()
        {
            while (true)
            {
                while (physicsTime >= Timing.Step)
                {
                    lock (updateLock)
                    {
                        GameMain.World.Step((float)Timing.Step);
                        physicsTime -= Timing.Step;
                    }
                }
            }
        }
    }
}
