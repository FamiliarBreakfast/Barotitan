﻿using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Barotrauma
{
    partial class HumanAIController : AIController
    {
        public static bool DebugAI;
        public static bool DisableCrewAI;

        private readonly AIObjectiveManager objectiveManager;
        
        public float SortTimer { get; set; }
        private float crouchRaycastTimer;
        private float reactTimer;
        private float unreachableClearTimer;
        private bool shouldCrouch;
        /// <summary>
        /// Resets each frame
        /// </summary>
        public bool AutoFaceMovement = true;

        const float reactionTime = 0.3f;
        const float crouchRaycastInterval = 1;
        const float sortObjectiveInterval = 1;
        const float clearUnreachableInterval = 30;

        private float flipTimer;
        private const float FlipInterval = 0.5f;

        public const float HULL_SAFETY_THRESHOLD = 40;
        public const float HULL_LOW_OXYGEN_PERCENTAGE = 30;

        private static readonly float characterWaitOnSwitch = 5;

        public readonly HashSet<Hull> UnreachableHulls = new HashSet<Hull>();
        public readonly HashSet<Hull> UnsafeHulls = new HashSet<Hull>();
        public readonly List<Item> IgnoredItems = new List<Item>();

        private readonly HashSet<Hull> dirtyHullSafetyCalculations = new HashSet<Hull>();

        private float respondToAttackTimer;
        private const float RespondToAttackInterval = 1.0f;
        private bool wasConscious;

        private bool freezeAI;

        private readonly float maxSteeringBuffer = 5000;
        private readonly float minSteeringBuffer = 500;
        private readonly float steeringBufferIncreaseSpeed = 100;
        private float steeringBuffer;

        private readonly float obstacleRaycastIntervalShort = 1, obstacleRaycastIntervalLong = 5;
        private float obstacleRaycastTimer;
        private bool isBlocked;

        private readonly float enemyCheckInterval = 0.2f;
        private readonly float enemySpotDistanceOutside = 800;
        private readonly float enemySpotDistanceInside = 1000;
        private float enemyCheckTimer;

        private readonly float reportProblemsInterval = 1.0f;
        private float reportProblemsTimer;

        /// <summary>
        /// Affects how far the character can hear sounds created by AI targets with the tag ProvocativeToHumanAI. 
        /// Used as a multiplier on the sound range of the target, e.g. a value of 0.5 would mean a target with a sound range of 1000 would need to be within 500 units for this character to hear it.
        /// Only affects the "fight intruders" objective, which makes the character go and inspect noises.
        /// </summary>
        public float Hearing { get; set; } = 1.0f;

        /// <summary>
        /// How far other characters can hear reports done by this character (e.g. reports for fires, intruders). Defaults to infinity.
        /// </summary>
        public float ReportRange { get; set; } = float.PositiveInfinity;
        
        /// <summary>
        /// How far the character can seek new weapons from.
        /// </summary>
        public float FindWeaponsRange { get; set; } = float.PositiveInfinity;

        private float _aimSpeed = 1;
        public float AimSpeed
        {
            get { return _aimSpeed; }
            set { _aimSpeed = Math.Max(value, 0.01f); }
        }

        private float _aimAccuracy = 1;
        public float AimAccuracy
        {
            get { return _aimAccuracy; }
            set { _aimAccuracy = Math.Clamp(value, 0f, 1f); }
        }

        /// <summary>
        /// List of previous attacks done to this character
        /// </summary>
        private readonly Dictionary<Character, AttackResult> previousAttackResults = new Dictionary<Character, AttackResult>();
        private readonly Dictionary<Character, float> previousHealAmounts = new Dictionary<Character, float>();

        private readonly SteeringManager outsideSteering, insideSteering;

        /// <summary>
        /// Waypoints that are not linked to a sub (e.g. main path).
        /// </summary>
        public bool UseOutsideWaypoints { get; private set; }

        public IndoorsSteeringManager PathSteering => insideSteering as IndoorsSteeringManager;
        public HumanoidAnimController AnimController => Character.AnimController as HumanoidAnimController;

        public AIObjectiveManager ObjectiveManager => objectiveManager;

        public float CurrentHullSafety { get; private set; } = 100;

        private readonly Dictionary<Character, float> structureDamageAccumulator = new Dictionary<Character, float>();
        private readonly Dictionary<Hull, HullSafety> knownHulls = new Dictionary<Hull, HullSafety>();
        private class HullSafety
        {
            public float safety;
            public float timer;

            public bool IsStale => timer <= 0;

            public HullSafety(float safety)
            {
                Reset(safety);
            }

            public void Reset(float safety)
            {
                this.safety = safety;
                // How long before the hull safety is considered stale
                timer = 0.5f;
            }

            /// <summary>
            /// Returns true when the safety is stale
            /// </summary>
            public bool Update(float deltaTime)
            {
                timer = Math.Max(timer - deltaTime, 0);
                return IsStale;
            }
        }

        public MentalStateManager MentalStateManager { get; private set; }

        public void InitMentalStateManager()
        {
            if (MentalStateManager == null)
            {
                MentalStateManager = new MentalStateManager(Character, this);
            }
            MentalStateManager.Active = true;
        }

        public override bool IsMentallyUnstable => 
            MentalStateManager is
            {
                CurrentMentalType: 
                    MentalStateManager.MentalType.Afraid or 
                    MentalStateManager.MentalType.Desperate or
                    MentalStateManager.MentalType.Berserk
            };

        public ShipCommandManager ShipCommandManager { get; private set; }

        public void InitShipCommandManager()
        {
            if (ShipCommandManager == null)
            {
                ShipCommandManager = new ShipCommandManager(Character);
            }
            ShipCommandManager.Active = true;
        }

        public HumanAIController(Character c) : base(c)
        {
            insideSteering = new IndoorsSteeringManager(this, true, false);
            outsideSteering = new SteeringManager(this);
            objectiveManager = new AIObjectiveManager(c);
            reactTimer = GetReactionTime();
            SortTimer = Rand.Range(0f, sortObjectiveInterval);
            reportProblemsTimer = Rand.Range(0f, reportProblemsInterval);
        }

        public override void Update(float deltaTime)
        {
            if (DisableCrewAI || Character.Removed) { return; }

            bool isIncapacitated = Character.IsIncapacitated;
            if (freezeAI && !isIncapacitated)
            {
                freezeAI = false;
            }
            if (isIncapacitated) { return; }

            wasConscious = true;

            respondToAttackTimer -= deltaTime;
            if (respondToAttackTimer <= 0.0f)
            {
                foreach (var previousAttackResult in previousAttackResults)
                {
                    RespondToAttack(previousAttackResult.Key, previousAttackResult.Value);
                    if (previousHealAmounts.ContainsKey(previousAttackResult.Key))
                    {
                        //gradually forget past heals
                        previousHealAmounts[previousAttackResult.Key] = Math.Min(previousHealAmounts[previousAttackResult.Key] - 5.0f, 100.0f);
                        if (previousHealAmounts[previousAttackResult.Key] <= 0.0f)
                        {
                            previousHealAmounts.Remove(previousAttackResult.Key);
                        }
                    }
                }
                previousAttackResults.Clear();
                respondToAttackTimer = RespondToAttackInterval;
            }

            base.Update(deltaTime);

            foreach (var values in knownHulls)
            {
                HullSafety hullSafety = values.Value;
                hullSafety.Update(deltaTime);
            }

            if (unreachableClearTimer > 0)
            {
                unreachableClearTimer -= deltaTime;
            }
            else
            {
                unreachableClearTimer = clearUnreachableInterval;
                UnreachableHulls.Clear();
                IgnoredItems.Clear();
            }

            // Note: returns false when useTargetSub is 'true' and the target is outside (targetSub is 'null')
            bool IsCloseEnoughToTarget(float threshold, bool targetSub = true)
            {
                Entity target = SelectedAiTarget?.Entity;
                if (target == null)
                {
                    return false;
                }
                if (targetSub)
                {
                    if (target.Submarine is Submarine sub)
                    {
                        target = sub;
                        threshold += Math.Max(sub.Borders.Size.X, sub.Borders.Size.Y) / 2;
                    }
                    else
                    {
                        return false;
                    }
                }
                return Vector2.DistanceSquared(Character.WorldPosition, target.WorldPosition) < MathUtils.Pow2(threshold);
            }

            bool isOutside = Character.Submarine == null;
            if (isOutside)
            {
                obstacleRaycastTimer -= deltaTime;
                if (obstacleRaycastTimer <= 0)
                {
                    bool hasValidPath = HasValidPath();
                    isBlocked = false;
                    UseOutsideWaypoints = false;
                    obstacleRaycastTimer = obstacleRaycastIntervalLong;
                    ISpatialEntity spatialTarget = SelectedAiTarget?.Entity ?? ObjectiveManager.GetLastActiveObjective<AIObjectiveGoTo>()?.Target;
                    if (spatialTarget != null && (spatialTarget.Submarine == null || !IsCloseEnoughToTarget(2000, targetSub: false)))
                    {
                        // If the target is behind a level wall, switch to the pathing to get around the obstacles.
                        IEnumerable<FarseerPhysics.Dynamics.Body> ignoredBodies = null;
                        Vector2 rayEnd = spatialTarget.SimPosition;
                        Submarine targetSub = spatialTarget.Submarine;
                        if (targetSub != null)
                        {
                            rayEnd += targetSub.SimPosition;
                            ignoredBodies = targetSub.PhysicsBody.FarseerBody.ToEnumerable();
                        }
                        var obstacle = Submarine.PickBody(SimPosition, rayEnd, ignoredBodies, collisionCategory: Physics.CollisionLevel | Physics.CollisionWall);
                        isBlocked = obstacle != null;
                        // Don't use outside waypoints when blocked by a sub, because we should use the waypoints linked to the sub instead.
                        UseOutsideWaypoints = isBlocked && (obstacle.UserData is not Submarine sub || sub.Info.IsRuin);
                        bool resetPath = false;
                        if (UseOutsideWaypoints)
                        {
                            bool isUsingInsideWaypoints = hasValidPath && HasValidPath(nodePredicate: n => n.Submarine != null || n.Ruin != null);
                            if (isUsingInsideWaypoints)
                            {
                                resetPath = true;
                            }
                        }
                        else
                        {
                            bool isUsingOutsideWaypoints = hasValidPath && HasValidPath(nodePredicate: n => n.Submarine == null && n.Ruin == null);
                            if (isUsingOutsideWaypoints)
                            {
                                resetPath = true;
                            }
                        }
                        if (resetPath)
                        {
                            PathSteering.ResetPath();
                        }
                    }
                    else if (hasValidPath)
                    {
                        obstacleRaycastTimer = obstacleRaycastIntervalShort;
                        // Swimming outside and using the path finder -> check that the path is not blocked with anything (the path finder doesn't know about other subs).
                        if (Submarine.MainSub != null)
                        {
                            foreach (var connectedSub in Submarine.MainSub.GetConnectedSubs())
                            {
                                if (connectedSub == Submarine.MainSub) { continue; }
                                Vector2 rayStart = SimPosition - connectedSub.SimPosition;
                                Vector2 dir = PathSteering.CurrentPath.CurrentNode.WorldPosition - WorldPosition;
                                Vector2 rayEnd = rayStart + dir.ClampLength(Character.AnimController.Collider.GetLocalFront().Length() * 5);
                                if (Submarine.CheckVisibility(rayStart, rayEnd, ignoreSubs: true) != null)
                                {
                                    PathSteering.CurrentPath.Unreachable = true;
                                    break;
                                }
                            }
                        }                        
                    }
                }
            }
            else
            {
                UseOutsideWaypoints = false;
                isBlocked = false;
            }
            
            if (isOutside || Character.IsOnPlayerTeam && !Character.IsEscorted && !Character.IsOnFriendlyTeam(Character.Submarine.TeamID))
            {
                // Spot enemies while staying outside or inside an enemy ship.
                // does not apply for escorted characters, such as prisoners or terrorists who have their own behavior
                enemyCheckTimer -= deltaTime;
                if (enemyCheckTimer < 0)
                {
                    SpotEnemies();
                    enemyCheckTimer = enemyCheckInterval * Rand.Range(0.75f, 1.25f);
                }
            }
            bool useInsideSteering = !isOutside || isBlocked || HasValidPath() || IsCloseEnoughToTarget(steeringBuffer);
            if (useInsideSteering)
            {
                if (steeringManager != insideSteering)
                {
                    insideSteering.Reset();
                    PathSteering.ResetPath();
                    steeringManager = insideSteering;
                }
                if (IsCloseEnoughToTarget(maxSteeringBuffer))
                {
                    steeringBuffer += steeringBufferIncreaseSpeed * deltaTime;
                }
                else
                {
                    steeringBuffer = minSteeringBuffer;
                }
            }
            else
            {
                if (steeringManager != outsideSteering)
                {
                    outsideSteering.Reset();
                    steeringManager = outsideSteering;
                }
                steeringBuffer = minSteeringBuffer;
            }
            steeringBuffer = Math.Clamp(steeringBuffer, minSteeringBuffer, maxSteeringBuffer);

            AnimController.Crouching = shouldCrouch;
            CheckCrouching(deltaTime);
            Character.ClearInputs();
            
            if (SortTimer > 0.0f)
            {
                SortTimer -= deltaTime;
            }
            else
            {
                objectiveManager.SortObjectives();
                SortTimer = sortObjectiveInterval;
            }
            objectiveManager.UpdateObjectives(deltaTime);

            UpdateDragged(deltaTime);

            if (reportProblemsTimer > 0)
            {
                reportProblemsTimer -= deltaTime;
            }
            if (reactTimer > 0.0f)
            {
                reactTimer -= deltaTime;
                if (findItemState != FindItemState.None)
                {
                    // Update every frame only when seeking items
                    UnequipUnnecessaryItems();
                }
            }
            else
            {
                Character.UpdateTeam();
                if (Character.CurrentHull != null)
                {
                    if (Character.IsOnPlayerTeam)
                    {
                        foreach (Hull h in VisibleHulls)
                        {
                            PropagateHullSafety(Character, h);
                            dirtyHullSafetyCalculations.Remove(h);
                        }
                    }
                    else
                    {
                        foreach (Hull h in VisibleHulls)
                        {
                            RefreshHullSafety(h);
                            dirtyHullSafetyCalculations.Remove(h);
                        }
                    }
                    foreach (Hull h in dirtyHullSafetyCalculations)
                    {
                        RefreshHullSafety(h);
                    }
                }
                dirtyHullSafetyCalculations.Clear();
                if (reportProblemsTimer <= 0.0f)
                {
                    if (Character.Submarine != null && (Character.Submarine.TeamID == Character.TeamID || Character.Submarine.TeamID == Character.OriginalTeamID || Character.IsEscorted) && !Character.Submarine.Info.IsWreck)
                    {
                        ReportProblems();
                    }
                    else
                    {
                        // Allows bots to heal targets autonomously while swimming outside of the sub.
                        if (AIObjectiveRescueAll.IsValidTarget(Character, Character, out _))
                        {
                            AddTargets<AIObjectiveRescueAll, Character>(Character, Character);
                        }
                    }
                    reportProblemsTimer = reportProblemsInterval;
                }
                SpeakAboutIssues();
                UnequipUnnecessaryItems();
                reactTimer = GetReactionTime();
            }

            if (objectiveManager.CurrentObjective == null) { return; }

            objectiveManager.DoCurrentObjective(deltaTime);
            var currentObjective = objectiveManager.CurrentObjective;
            bool run = !currentObjective.ForceWalk && (currentObjective.ForceRun || objectiveManager.GetCurrentPriority() > AIObjectiveManager.RunPriority);
            if (currentObjective is AIObjectiveGoTo goTo)
            {
                run = goTo.ShouldRun(run);
            }

            //if someone is grabbing the bot and the bot isn't trying to run anywhere, let them keep dragging and "control" the bot
            if (Character.SelectedBy == null || run)
            {
                steeringManager.Update(Character.AnimController.GetCurrentSpeed(run && Character.CanRun));
            }

            bool ignorePlatforms = Character.AnimController.TargetMovement.Y < -0.5f && (-Character.AnimController.TargetMovement.Y > Math.Abs(Character.AnimController.TargetMovement.X));
            if (steeringManager == insideSteering)
            {
                var currPath = PathSteering.CurrentPath;
                if (currPath != null && currPath.CurrentNode != null)
                {
                    if (currPath.CurrentNode.SimPosition.Y < Character.AnimController.GetColliderBottom().Y)
                    {
                        // Don't allow to jump from too high.
                        float allowedJumpHeight = Character.AnimController.ImpactTolerance / 2;
                        float height = Math.Abs(currPath.CurrentNode.SimPosition.Y - Character.SimPosition.Y);
                        ignorePlatforms = height < allowedJumpHeight;
                    }
                }
                if (Character.IsClimbing && PathSteering.IsNextLadderSameAsCurrent)
                {
                    Character.AnimController.TargetMovement = new Vector2(0.0f, Math.Sign(Character.AnimController.TargetMovement.Y));
                }
            }
            Character.AnimController.IgnorePlatforms = ignorePlatforms;

            Vector2 targetMovement = AnimController.TargetMovement;
            if (!Character.AnimController.InWater)
            {
                targetMovement = new Vector2(Character.AnimController.TargetMovement.X, MathHelper.Clamp(Character.AnimController.TargetMovement.Y, -1.0f, 1.0f));
            }
            Character.AnimController.TargetMovement = Character.ApplyMovementLimits(targetMovement, AnimController.GetCurrentSpeed(run));

            flipTimer -= deltaTime;
            if (flipTimer <= 0.0f)
            {
                Direction newDir = Character.AnimController.TargetDir;
                if (Character.IsKeyDown(InputType.Aim))
                {
                    var cursorDiffX = Character.CursorPosition.X - Character.Position.X;
                    if (cursorDiffX > 10.0f)
                    {
                        newDir = Direction.Right;
                    }
                    else if (cursorDiffX < -10.0f)
                    {
                        newDir = Direction.Left;
                    }
                    if (Character.SelectedItem != null)
                    {
                        Character.SelectedItem.SecondaryUse(deltaTime, Character);
                    }
                }
                else if (AutoFaceMovement && Math.Abs(Character.AnimController.TargetMovement.X) > 0.1f && !Character.AnimController.InWater)
                {
                    newDir = Character.AnimController.TargetMovement.X > 0.0f ? Direction.Right : Direction.Left;
                }
                if (newDir != Character.AnimController.TargetDir)
                {
                    Character.AnimController.TargetDir = newDir;
                    flipTimer = FlipInterval;
                }
            }
            AutoFaceMovement = true;

            MentalStateManager?.Update(deltaTime);
            ShipCommandManager?.Update(deltaTime);
        }

        private void SpotEnemies()
        {
            //already in combat, no need to check
            if (objectiveManager.IsCurrentObjective<AIObjectiveCombat>()) { return; }
            if (objectiveManager.HasActiveObjective<AIObjectiveCombat>()) { return; }
            
            float closestDistance = 0;
            Character closestEnemy = null;
            bool shouldActOffensively = ObjectiveManager.HasObjectiveOrOrder<AIObjectiveFightIntruders>();
            foreach (Character c in Character.CharacterList)
            {
                if (c.Submarine != Character.Submarine) { continue; }
                if (c.Removed || c.IsDead || c.IsIncapacitated || c.InDetectable) { continue; }
                if (IsFriendly(c)) { continue; }
                Vector2 toTarget = c.WorldPosition - WorldPosition;
                float dist = toTarget.LengthSquared();
                float maxDistance = Character.Submarine == null ? enemySpotDistanceOutside : enemySpotDistanceInside;
                if (dist > maxDistance * maxDistance) { continue; }
                if (EnemyAIController.IsLatchedToSomeoneElse(c, Character)) { continue; }
                var head = Character.AnimController.GetLimb(LimbType.Head);
                if (head == null) { continue; }
                float rotation = head.body.TransformedRotation;
                Vector2 forward = VectorExtensions.Forward(rotation);
                float angle = MathHelper.ToDegrees(VectorExtensions.Angle(toTarget, forward));
                if (angle > 70) { continue; }
                if (!Character.CanSeeTarget(c)) { continue; }
                if (dist < closestDistance || closestEnemy == null)
                {
                    closestEnemy = c;
                    closestDistance = dist;
                }
            }
            if (closestEnemy != null)
            {
                AddCombatObjective(shouldActOffensively ? AIObjectiveCombat.CombatMode.Offensive : AIObjectiveCombat.CombatMode.Defensive, closestEnemy);
            }
        }

        private void UnequipUnnecessaryItems()
        {
            if (Character.LockHands) { return; }
            if (ObjectiveManager.CurrentObjective == null) { return; }
            if (Character.CurrentHull == null) { return; }
            bool shouldActOnSuffocation = Character.IsLowInOxygen && !Character.AnimController.HeadInWater && HasDivingSuit(Character, requireOxygenTank: false) && !HasItem(Character, Tags.OxygenSource, out _, conditionPercentage: 1);
            bool isCarrying = ObjectiveManager.HasActiveObjective<AIObjectiveContainItem>() || ObjectiveManager.HasActiveObjective<AIObjectiveMoveItem>();

            bool NeedsDivingGearOnPath(AIObjectiveGoTo gotoObjective)
            {
                bool insideSteering = SteeringManager == PathSteering && PathSteering.CurrentPath != null && !PathSteering.IsPathDirty;
                Hull targetHull = gotoObjective.GetTargetHull();
                return (gotoObjective.Target != null && targetHull == null && !Character.IsImmuneToPressure) ||
                    NeedsDivingGear(targetHull, out _) ||
                    (insideSteering && ((PathSteering.CurrentPath.HasOutdoorsNodes && !Character.IsImmuneToPressure) || PathSteering.CurrentPath.Nodes.Any(n => NeedsDivingGear(n.CurrentHull, out _))));
            }

            if (isCarrying)
            {
                if (findItemState != FindItemState.OtherItem)
                {
                    var moveItemObjective = ObjectiveManager.GetLastActiveObjective<AIObjectiveMoveItem>();
                    if (moveItemObjective is { TargetItem: not null } && moveItemObjective.TargetItem.HasTag(Tags.HeavyDivingGear) &&
                        ObjectiveManager.GetActiveObjective() is AIObjectiveGoTo gotoObjective && NeedsDivingGearOnPath(gotoObjective))
                    {
                        // Don't try to put the diving suit in a locker if the suit would be needed in any hull in the path to the locker.
                        gotoObjective.Abandon = true;
                    }
                }
                if (!shouldActOnSuffocation)
                {
                    return;
                }
            }

            // Diving gear
            if (shouldActOnSuffocation || findItemState != FindItemState.OtherItem)
            {
                bool needsGear = NeedsDivingGear(Character.CurrentHull, out _);
                if (!needsGear || shouldActOnSuffocation)
                {
                    bool isCurrentObjectiveFindSafety = ObjectiveManager.IsCurrentObjective<AIObjectiveFindSafety>();
                    bool shouldKeepTheGearOn =
                        isCurrentObjectiveFindSafety ||
                        Character.AnimController.InWater ||
                        Character.AnimController.HeadInWater ||
                        Character.IsClimbing ||
                        Character.Submarine == null ||
                        Character.Submarine.Info.HasTag(SubmarineTag.Shuttle) ||
                        (!Character.IsOnFriendlyTeam(Character.TeamID, Character.Submarine.TeamID) && !Character.IsEscorted) ||
                        ObjectiveManager.CurrentOrders.Any(o => o.Objective.KeepDivingGearOnAlsoWhenInactive) ||
                        ObjectiveManager.CurrentObjective.GetSubObjectivesRecursive(true).Any(o => o.KeepDivingGearOn) ||
                        Character.CurrentHull.OxygenPercentage < HULL_LOW_OXYGEN_PERCENTAGE + 10 ||
                        Character.CurrentHull.IsWetRoom;
                    bool IsOrderedToWait() => Character.IsOnPlayerTeam && ObjectiveManager.CurrentOrder is AIObjectiveGoTo { IsWaitOrder: true };
                    bool removeDivingSuit = !shouldKeepTheGearOn && !IsOrderedToWait();
                    if (shouldActOnSuffocation && Character.CurrentHull.Oxygen > 0 && (!isCurrentObjectiveFindSafety || Character.OxygenAvailable < 1))
                    {
                        shouldKeepTheGearOn = false;
                        // Remove the suit before we pass out
                        removeDivingSuit = true;
                    }
                    bool takeMaskOff = !shouldKeepTheGearOn;
                    if (!shouldKeepTheGearOn && !shouldActOnSuffocation)
                    {
                        if (ObjectiveManager.IsCurrentObjective<AIObjectiveIdle>())
                        {
                            removeDivingSuit = true;
                            takeMaskOff = true;
                        }
                        else
                        {
                            bool removeSuit = false;
                            bool removeMask = false;
                            foreach (var objective in ObjectiveManager.CurrentObjective.GetSubObjectivesRecursive(includingSelf: true))
                            {
                                if (objective is AIObjectiveGoTo gotoObjective)
                                {
                                    if (NeedsDivingGearOnPath(gotoObjective))
                                    {
                                        removeDivingSuit = false;
                                        takeMaskOff = false;
                                        break;
                                    }
                                    else if (gotoObjective.Mimic)
                                    {
                                        bool targetHasDivingGear = HasDivingGear(gotoObjective.Target as Character, requireOxygenTank: false);
                                        if (!removeSuit)
                                        {
                                            removeDivingSuit = !targetHasDivingGear;
                                            if (removeDivingSuit)
                                            {
                                                removeSuit = true;
                                            }
                                        }
                                        if (!removeMask)
                                        {
                                            takeMaskOff = !targetHasDivingGear;
                                            if (takeMaskOff)
                                            {
                                                removeMask = true;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    if (removeDivingSuit)
                    {
                        var divingSuit = Character.Inventory.FindEquippedItemByTag(Tags.HeavyDivingGear);
                        if (divingSuit != null && !divingSuit.HasTag(Tags.DivingGearWearableIndoors) && divingSuit.IsInteractable(Character))
                        {
                            if (shouldActOnSuffocation || Character.Submarine?.TeamID != Character.TeamID || ObjectiveManager.GetCurrentPriority() >= AIObjectiveManager.RunPriority)
                            {
                                divingSuit.Drop(Character);
                                HandleRelocation(divingSuit);
                                ReequipUnequipped();
                            }
                            else if (findItemState == FindItemState.None || findItemState == FindItemState.DivingSuit)
                            {
                                findItemState = FindItemState.DivingSuit;
                                if (FindSuitableContainer(divingSuit, out Item targetContainer))
                                {
                                    findItemState = FindItemState.None;
                                    itemIndex = 0;
                                    if (targetContainer != null)
                                    {
                                        var moveItemObjective = new AIObjectiveMoveItem(Character, divingSuit, ObjectiveManager, targetContainer: targetContainer.GetComponent<ItemContainer>())
                                        {
                                            DropIfFails = false
                                        };
                                        moveItemObjective.Abandoned += () =>
                                        {
                                            ReequipUnequipped();
                                            IgnoredItems.Add(targetContainer);
                                        };
                                        moveItemObjective.Completed += () => ReequipUnequipped();
                                        ObjectiveManager.CurrentObjective.AddSubObjective(moveItemObjective, addFirst: true);
                                        return;
                                    }
                                    else
                                    {
                                        divingSuit.Drop(Character);
                                        HandleRelocation(divingSuit);
                                        ReequipUnequipped();
                                    }
                                }
                            }
                        }
                    }
                    if (takeMaskOff)
                    {                  
                        var mask = Character.Inventory.FindEquippedItemByTag(Tags.LightDivingGear);
                        if (mask != null)
                        {
                            if (!mask.AllowedSlots.Contains(InvSlotType.Any) || !Character.Inventory.TryPutItem(mask, Character, new List<InvSlotType>() { InvSlotType.Any }))
                            {
                                if (Character.Submarine?.TeamID != Character.TeamID || ObjectiveManager.GetCurrentPriority() >= AIObjectiveManager.RunPriority)
                                {
                                    mask.Drop(Character);
                                    HandleRelocation(mask);
                                    ReequipUnequipped();
                                }
                                else if (findItemState is FindItemState.None or FindItemState.DivingMask)
                                {
                                    findItemState = FindItemState.DivingMask;
                                    if (FindSuitableContainer(mask, out Item targetContainer))
                                    {
                                        findItemState = FindItemState.None;
                                        itemIndex = 0;
                                        if (targetContainer != null)
                                        {
                                            var moveItemObjective = new AIObjectiveMoveItem(Character, mask, ObjectiveManager, targetContainer: targetContainer.GetComponent<ItemContainer>());
                                            moveItemObjective.Abandoned += () =>
                                            {
                                                ReequipUnequipped();
                                                IgnoredItems.Add(targetContainer);
                                            };
                                            moveItemObjective.Completed += ReequipUnequipped;
                                            ObjectiveManager.CurrentObjective.AddSubObjective(moveItemObjective, addFirst: true);
                                            return;
                                        }
                                        else
                                        {
                                            mask.Drop(Character);
                                            HandleRelocation(mask);
                                            ReequipUnequipped();
                                        }
                                    }
                                }
                            }
                            else
                            {
                                ReequipUnequipped();
                            }
                        }                        
                    }
                }
            }
            // Other items
            if (isCarrying) { return; }
            if (!ObjectiveManager.CurrentObjective.AllowAutomaticItemUnequipping || !ObjectiveManager.GetActiveObjective().AllowAutomaticItemUnequipping) { return; }

            if (Character.Submarine?.TeamID == Character.TeamID && findItemState is FindItemState.None or FindItemState.OtherItem)
            {
                // Only unequip other items inside a friendly sub.
                foreach (Item item in Character.HeldItems)
                {
                    if (item == null || !item.IsInteractable(Character)) { continue; }
                    if (!item.UnequipAutomatically) { continue; }
                    //NPC set to operate the item they're holding, don't put it away
                    if (ObjectiveManager.CurrentObjective is AIObjectiveOperateItem operateItem && 
                        (operateItem.OperateTarget == item || operateItem.Component?.Item == item)) 
                    { 
                        continue; 
                    }
                    if (Character.TryPutItemInAnySlot(item)) { continue; }
                    if (Character.TryPutItemInBag(item)) { continue; }
                    if (item.HasTag(Tags.Weapon))
                    {
                        // Don't store weapons in containers, because it could be that we are holding a weapon that cannot be placed on back (if we have a toolbelt) nor in any slot, such as an HMG.
                        // Could check that we only ignore weapons when we've had an order to find a weapon, but it could also be that we picked the weapon for self-defence, on ad-hoc basis.
                        // And I don't think it would make sense to move those weapons in containers either.
                        continue;
                    }
                    findItemState = FindItemState.OtherItem;
                    if (FindSuitableContainer(item, out Item targetContainer))
                    {
                        findItemState = FindItemState.None;
                        itemIndex = 0;
                        if (targetContainer != null)
                        {
                            var moveItemObjective = new AIObjectiveMoveItem(Character, item, ObjectiveManager, targetContainer: targetContainer.GetComponent<ItemContainer>());
                            moveItemObjective.Abandoned += () =>
                            {
                                ReequipUnequipped();
                                IgnoredItems.Add(targetContainer);
                            };
                            ObjectiveManager.CurrentObjective.AddSubObjective(moveItemObjective, addFirst: true);
                            return;
                        }
                        else
                        {
                            item.Drop(Character);
                            HandleRelocation(item);
                        }
                    }
                }
            }
        }

        private readonly HashSet<Item> itemsToRelocate = new HashSet<Item>();

        public void HandleRelocation(Item item)
        {
            if (item.SpawnedInCurrentOutpost) { return; }
            if (item.Submarine == null || Submarine.MainSub == null) { return; }
            // Only affects bots in the player team
            if (!Character.IsOnPlayerTeam) { return; }
            // Don't relocate if the item is on a sub of the same team
            if (item.Submarine.TeamID == Character.TeamID) { return; }
            if (itemsToRelocate.Contains(item)) { return; }
            itemsToRelocate.Add(item);
            if (item.Submarine.ConnectedDockingPorts.TryGetValue(Submarine.MainSub, out DockingPort myPort))
            {
                myPort.OnUnDocked += Relocate;
            }
            var campaign = GameMain.GameSession.Campaign;
            if (campaign != null)
            {
                // In the campaign mode, undocking happens after leaving the outpost, so we can't use that.
                campaign.BeforeLevelLoading += Relocate;
                campaign.OnSaveAndQuit += Relocate;
                campaign.ItemsRelocatedToMainSub = true;
            }
#if CLIENT
            HintManager.OnItemMarkedForRelocation();
#endif
            void Relocate()
            {
                if (item == null || item.Removed) { return; }
                if (!itemsToRelocate.Contains(item)) { return; }
                var mainSub = Submarine.MainSub;
                if (mainSub == null) { return; }
                Entity owner = item.GetRootInventoryOwner();
                if (owner != null)
                {
                    if (owner is Character c)
                    {
                        if (c.TeamID == CharacterTeamType.Team1 || c.TeamID == CharacterTeamType.Team2)
                        {
                            // Taken by a player/bot (if npc or monster would take the item, we'd probably still want it to spawn back to the main sub.
                            return;
                        }
                    }
                    else if (owner.Submarine == mainSub)
                    {
                        // Placed inside an inventory that's already in the main sub.
                        return;
                    }
                }
                // Laying on the ground inside the main sub.
                if (item.Submarine == mainSub)
                {
                    return;
                }
                if (owner != null && owner != item)
                {
                    item.Drop(null);
                }
                item.Submarine = mainSub;
                Item newContainer = mainSub.FindContainerFor(item, onlyPrimary: false);
                if (newContainer == null || !newContainer.OwnInventory.TryPutItem(item, user: null))
                {
                    WayPoint wp = WayPoint.GetRandom(SpawnType.Cargo, null, mainSub) ?? WayPoint.GetRandom(SpawnType.Path, null, mainSub);
                    if (wp != null)
                    {
                        item.SetTransform(wp.SimPosition, 0.0f, findNewHull: false, setPrevTransform: false);
                    }
                    else
                    {
                        DebugConsole.AddWarning($"Failed to relocate item {item.Prefab.Identifier} ({item.ID}), because no cargo spawn point could be found!");
                    }
                }
                itemsToRelocate.Remove(item);
                DebugConsole.Log($"Relocated item {item.Prefab.Identifier} ({item.ID}) back to the main sub.");
            }
        }

        private enum FindItemState
        {
            None,
            DivingSuit,
            DivingMask,
            OtherItem
        }
        private FindItemState findItemState;
        private int itemIndex;

        public bool FindSuitableContainer(Item containableItem, out Item suitableContainer) => FindSuitableContainer(Character, containableItem, IgnoredItems, ref itemIndex, out suitableContainer);

        public static bool FindSuitableContainer(Character character, Item containableItem, List<Item> ignoredItems, ref int itemIndex, out Item suitableContainer)
        {
            suitableContainer = null;
            if (character.FindItem(ref itemIndex, out Item targetContainer, ignoredItems: ignoredItems, positionalReference: containableItem, customPriorityFunction: i =>
            {
                if (!i.HasAccess(character)) { return 0; }
                var container = i.GetComponent<ItemContainer>();
                if (container == null) { return 0; }
                if (!container.Inventory.CanBePut(containableItem)) { return 0; }
                var rootContainer = container.Item.RootContainer ?? container.Item;
                if (rootContainer.GetComponent<Fabricator>() != null || rootContainer.GetComponent<Deconstructor>() != null) { return 0; }
                if (container.ShouldBeContained(containableItem, out bool isRestrictionsDefined))
                {
                    if (isRestrictionsDefined)
                    {
                        return 10;
                    }
                    else
                    {
                        if (containableItem.IsContainerPreferred(container, out bool isPreferencesDefined, out bool isSecondary))
                        {
                            return isPreferencesDefined ? isSecondary ? 2 : 5 : 1;
                        }
                        else
                        {
                            if (isPreferencesDefined)
                            {
                                // Use any valid locker as a fall back container.
                                return container.Item.HasTag(Tags.FallbackLocker) ? 0.5f : 0;
                            }
                            return 1;
                        }
                    }
                }
                else
                {
                    return 0;
                }
            }))
            {
                if (targetContainer != null &&
                    character.AIController is HumanAIController humanAI && 
                    humanAI.PathSteering.PathFinder.FindPath(character.SimPosition, targetContainer.SimPosition, character.Submarine, errorMsgStr: $"FindSuitableContainer ({character.DisplayName})", nodeFilter: node => node.Waypoint.CurrentHull != null).Unreachable)
                {
                    ignoredItems.Add(targetContainer);
                    itemIndex = 0;
                    return false;
                }
                else
                {
                    suitableContainer = targetContainer;
                    return true;
                }
            }
            return false;
        }

        private float draggedTimer;
        private float refuseDraggingTimer;
        /// <summary>
        /// The bot breaks free if being dragged by a human player from another team for longer than this
        /// </summary>
        private const float RefuseDraggingThresholdHigh = 10.0f;
        /// <summary>
        /// If the RefuseDraggingDuration is active (the bot recently broke free of being dragged), the bot breaks free much faster
        /// </summary>
        private const float RefuseDraggingThresholdLow = 0.5f;
        private const float RefuseDraggingDuration = 30.0f;

        private void UpdateDragged(float deltaTime)
        {
            if (Character.HumanPrefab is { AllowDraggingIndefinitely: true }) { return; }
            if (Character.IsEscorted) { return; }
            if (Character.LockHands) { return; }

            //don't allow player characters who aren't in the same team to drag us for more than x seconds
            if (Character.SelectedBy == null || 
                !Character.SelectedBy.IsPlayer || 
                Character.SelectedBy.TeamID == Character.TeamID) 
            {
                refuseDraggingTimer -= deltaTime;
                return; 
            }

            draggedTimer += deltaTime;
            if (draggedTimer > RefuseDraggingThresholdHigh || 
                (refuseDraggingTimer > 0.0f && draggedTimer > RefuseDraggingThresholdLow))
            {
                draggedTimer = 0.0f;
                refuseDraggingTimer = RefuseDraggingDuration;
                Character.SelectedBy.DeselectCharacter(); 
                Character.Speak(TextManager.Get("dialogrefusedragging").Value, delay: 0.5f, identifier: "refusedragging".ToIdentifier(), minDurationBetweenSimilar: 5.0f);
            }
        }

        protected void ReportProblems()
        {
            Order newOrder = null;
            Hull targetHull = null;

            // for now, escorted characters use the report system to get targets but do not speak. escort-character specific dialogue could be implemented
            bool speak = Character.SpeechImpediment < 100 && !Character.IsEscorted;
            if (Character.CurrentHull != null)
            {
                bool isFighting = ObjectiveManager.HasActiveObjective<AIObjectiveCombat>();
                bool isFleeing = ObjectiveManager.HasActiveObjective<AIObjectiveFindSafety>();
                foreach (var hull in VisibleHulls)
                {
                    foreach (Character target in Character.CharacterList)
                    {
                        if (target.CurrentHull != hull || !target.Enabled || target.InDetectable) { continue; }
                        if (AIObjectiveFightIntruders.IsValidTarget(target, Character, targetCharactersInOtherSubs: false))
                        {
                            if (AddTargets<AIObjectiveFightIntruders, Character>(Character, target) && newOrder == null)
                            {
                                var orderPrefab = OrderPrefab.Prefabs["reportintruders"];
                                newOrder = new Order(orderPrefab, hull, targetItem: null, orderGiver: Character);
                                targetHull = hull;
                                if (target.IsEscorted)
                                {
                                    if (!Character.IsPrisoner && target.IsPrisoner)
                                    {
                                        LocalizedString msg = TextManager.GetWithVariables("orderdialog.prisonerescaped", ("[roomname]", targetHull.DisplayName, FormatCapitals.No));
                                        Character.Speak(msg.Value, ChatMessageType.Order);
                                        speak = false;
                                    }
                                    else if (!IsMentallyUnstable && target.AIController.IsMentallyUnstable)
                                    {
                                        LocalizedString msg = TextManager.GetWithVariables("orderdialog.mentalcase", ("[roomname]", targetHull.DisplayName, FormatCapitals.No));
                                        Character.Speak(msg.Value, ChatMessageType.Order);
                                        speak = false;
                                    }
                                }
                            }
                            if (Character.CombatAction == null && !isFighting)
                            {
                                // Immediately react to enemies when they are spotted. AIObjectiveFightIntruders and AIObjectiveFindSafety would make the bot react to the threats,
                                // but the reaction is delayed (and doesn't necessarily target this enemy), and in many cases the reaction would come only when the enemy attacks and triggers AIObjectiveCombat.
                                AddCombatObjective(ObjectiveManager.HasObjectiveOrOrder<AIObjectiveFightIntruders>() ? AIObjectiveCombat.CombatMode.Offensive : AIObjectiveCombat.CombatMode.Defensive, target); 
                            }
                        }
                    }
                    if (AIObjectiveExtinguishFires.IsValidTarget(hull, Character))
                    {
                        if (AddTargets<AIObjectiveExtinguishFires, Hull>(Character, hull) && newOrder == null)
                        {
                            var orderPrefab = OrderPrefab.Prefabs["reportfire"];
                            newOrder = new Order(orderPrefab, hull, targetItem: null, orderGiver: Character);
                            targetHull = hull;
                        }
                    }
                    if (IsBallastFloraNoticeable(Character, hull) && newOrder == null)
                    {
                        var orderPrefab = OrderPrefab.Prefabs["reportballastflora"];
                        newOrder = new Order(orderPrefab, hull, targetItem: null, orderGiver: Character);
                        targetHull = hull;
                    }
                    if (!isFighting)
                    {
                        foreach (var gap in hull.ConnectedGaps)
                        {
                            if (AIObjectiveFixLeaks.IsValidTarget(gap, Character))
                            {
                                if (AddTargets<AIObjectiveFixLeaks, Gap>(Character, gap) && newOrder == null && !gap.IsRoomToRoom)
                                {
                                    var orderPrefab = OrderPrefab.Prefabs["reportbreach"];
                                    newOrder = new Order(orderPrefab, hull, targetItem: null, orderGiver: Character);
                                    targetHull = hull;
                                }
                            }
                        }
                        if (!isFleeing)
                        {
                            foreach (Character target in Character.CharacterList)
                            {
                                if (target.CurrentHull != hull) { continue; }
                                if (AIObjectiveRescueAll.IsValidTarget(target, Character, out _))
                                {
                                    if (AddTargets<AIObjectiveRescueAll, Character>(Character, target) && newOrder == null && (!Character.IsMedic || Character == target) && !ObjectiveManager.HasActiveObjective<AIObjectiveRescue>())
                                    {
                                        var orderPrefab = OrderPrefab.Prefabs["requestfirstaid"];
                                        newOrder = new Order(orderPrefab, hull, targetItem: null, orderGiver: Character);
                                        targetHull = hull;
                                    }
                                }
                            }                            
                            foreach (Item item in Item.RepairableItems)
                            {
                                if (item.CurrentHull != hull) { continue; }
                                if (AIObjectiveRepairItems.IsValidTarget(item, Character))
                                {
                                    if (!item.Repairables.Any(r => r.IsBelowRepairIconThreshold)) { continue; }
                                    if (AddTargets<AIObjectiveRepairItems, Item>(Character, item) && newOrder == null && !ObjectiveManager.HasActiveObjective<AIObjectiveRepairItem>())
                                    {
                                        var orderPrefab = OrderPrefab.Prefabs["reportbrokendevices"];
                                        newOrder = new Order(orderPrefab, hull, item.Repairables?.FirstOrDefault(), orderGiver: Character);
                                        targetHull = hull;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            if (newOrder != null && speak)
            {
                string msg = newOrder.GetChatMessage(string.Empty, targetHull?.DisplayName?.Value ?? string.Empty, givingOrderToSelf: false);
                if (Character.TeamID == CharacterTeamType.FriendlyNPC)
                {
                    Character.Speak(msg, ChatMessageType.Default, identifier: $"{newOrder.Prefab.Identifier}{targetHull?.RoomName ?? "null"}".ToIdentifier(), minDurationBetweenSimilar: 60f);
                }
                else if (Character.IsOnPlayerTeam && GameMain.GameSession?.CrewManager != null && GameMain.GameSession.CrewManager.AddOrder(newOrder, newOrder.FadeOutTime))
                {
                    Character.Speak(msg, messageType: ChatMessageType.Order);
#if SERVER
                    GameMain.Server.SendOrderChatMessage(new OrderChatMessage(newOrder
                        .WithManualPriority(CharacterInfo.HighestManualOrderPriority)
                        .WithTargetEntity(targetHull)
                        .WithOrderGiver(Character), msg, targetCharacter: null, sender: Character));
#endif
                }
            }
        }

        public static bool IsBallastFloraNoticeable(Character character, Hull hull)
        {
            foreach (var ballastFlora in MapCreatures.Behavior.BallastFloraBehavior.EntityList)
            {
                if (ballastFlora.Parent?.Submarine != character.Submarine) { continue; }
                if (!ballastFlora.HasBrokenThrough) { continue; }
                // Don't react to the first two branches, because they are usually in the very edges of the room.
                if (ballastFlora.Branches.Count(b => !b.Removed && b.Health > 0 && b.CurrentHull == hull) > 2)
                {
                    return true;
                }
            }
            return false;
        }

        public static void ReportProblem(Character reporter, Order order, Hull targetHull = null)
        {
            if (reporter == null || order == null) { return; }
            if (targetHull == null)
            {
                foreach (var hull in reporter.GetVisibleHulls())
                {
                    Report(hull);
                }
            }
            else
            {
                Report(targetHull);
            }
            
            void Report(Hull hull)
            {
                PropagateHullSafety(reporter, hull);
                RefreshTargets(reporter, order, hull);
            }
        }

        private void SpeakAboutIssues()
        {
            if (!Character.IsOnPlayerTeam) { return; }
            if (Character.SpeechImpediment >= 100) { return; }
            float minDelay = 0.5f, maxDelay = 2f;
            if (Character.Oxygen < CharacterHealth.InsufficientOxygenThreshold)
            {
                string msgId = "DialogLowOxygen";
                Character.Speak(TextManager.Get(msgId).Value, delay: Rand.Range(minDelay, maxDelay), identifier: msgId.ToIdentifier(), minDurationBetweenSimilar: 30.0f);
            }
            if (Character.Bleeding > AfflictionPrefab.Bleeding.TreatmentThreshold && !Character.IsMedic)
            {
                string msgId = "DialogBleeding";
                Character.Speak(TextManager.Get(msgId).Value, delay: Rand.Range(minDelay, maxDelay), identifier: msgId.ToIdentifier(), minDurationBetweenSimilar: 30.0f);
            }
            if ((Character.CurrentHull == null || Character.CurrentHull.LethalPressure > 0) && !Character.IsProtectedFromPressure)
            {
                if (Character.PressureProtection > 0)
                {
                    string msgId = "DialogInsufficientPressureProtection";
                    Character.Speak(TextManager.Get(msgId).Value, delay: Rand.Range(minDelay, maxDelay), identifier: msgId.ToIdentifier(), minDurationBetweenSimilar: 30.0f);
                }
                else if (Character.CurrentHull?.DisplayName != null)
                {
                    string msgId = "DialogPressure";
                    Character.Speak(TextManager.GetWithVariable(msgId, "[roomname]", Character.CurrentHull.DisplayName, FormatCapitals.Yes).Value, delay: Rand.Range(minDelay, maxDelay), identifier: msgId.ToIdentifier(), minDurationBetweenSimilar: 30.0f);
                }
            }
        }

        public override void OnHealed(Character healer, float healAmount)
        {
            if (healer == null || healAmount <= 0.0f) { return; }
            if (previousHealAmounts.ContainsKey(healer))
            {
                previousHealAmounts[healer] += healAmount;
            }
            else
            {
                previousHealAmounts.Add(healer, healAmount);
            }
        }

        public override void OnAttacked(Character attacker, AttackResult attackResult)
        {
            // The attack incapacitated/killed the character: respond immediately to trigger nearby characters because the update loop no longer runs
            if (wasConscious && (Character.IsIncapacitated || Character.Stun > 0.0f))
            {
                RespondToAttack(attacker, attackResult);
                wasConscious = false;
                return;
            }
            if (Character.IsDead) { return; }
            if (attacker == null || Character.IsPlayer)
            {
                // The player characters need to "respond" to the attack always, because the update loop doesn't run for them.
                // Otherwise other NPCs totally ignore when player characters are attacked.
                RespondToAttack(attacker, attackResult);
                return;
            }
            if (previousAttackResults.ContainsKey(attacker))
            {
                if (attackResult.Afflictions != null)
                {
                    foreach (Affliction newAffliction in attackResult.Afflictions)
                    {
                        var matchingAffliction = previousAttackResults[attacker].Afflictions.Find(a => a.Prefab == newAffliction.Prefab && a.Source == newAffliction.Source);
                        if (matchingAffliction == null)
                        {
                            previousAttackResults[attacker].Afflictions.Add(newAffliction);
                        }
                        else
                        {
                            matchingAffliction.Strength += newAffliction.Strength;
                        }
                    }
                }
                previousAttackResults[attacker] = new AttackResult(previousAttackResults[attacker].Afflictions, previousAttackResults[attacker].HitLimb);
            }
            else
            {
                previousAttackResults.Add(attacker, attackResult);
            }
        }

        private void RespondToAttack(Character attacker, AttackResult attackResult)
        {
            float healAmount = 0.0f;
            if (attacker != null)
            {
                previousHealAmounts.TryGetValue(attacker, out healAmount);
            }
            // excluding poisons etc
            float realDamage = attackResult.Damage - healAmount;
            // including poisons etc
            float totalDamage = realDamage;
            if (attackResult.Afflictions != null)
            {
                foreach (Affliction affliction in attackResult.Afflictions)
                {
                    totalDamage -= affliction.Prefab.KarmaChangeOnApplied * affliction.Strength;
                }
            }
            if (totalDamage <= 0.01f) { return; }
            if (Character.IsBot)
            {
                if (!freezeAI && !Character.IsDead && Character.IsIncapacitated)
                {
                    // Removes the combat objective and resets all objectives.
                    objectiveManager.CreateAutonomousObjectives();
                    objectiveManager.SortObjectives();
                    freezeAI = true;
                }
            }
            if (attacker == null || attacker.IsUnconscious || attacker.Removed)
            {
                // Don't react to the damage if there's no attacker.
                // We might consider launching the retreat combat objective in some cases, so that the bot does not just stand somewhere getting damaged and dying.
                // But fires and enemies should already be handled by the FindSafetyObjective.
                return;
                // Ignore damage from falling etc that we shouldn't react to.
                //if (Character.LastDamageSource == null) { return; }
                //AddCombatObjective(AIObjectiveCombat.CombatMode.Retreat, Rand.Range(0.5f, 1f, Rand.RandSync.Unsynced));
            }

            bool sameTeam =
                attacker.TeamID == Character.TeamID ||
                // consider escorted characters to be in the same team (otherwise accidental damage or side-effects from healing trigger them too easily)
                (attacker.TeamID == CharacterTeamType.Team1 && Character.IsEscorted);

            if (realDamage <= 0 && (attacker.IsBot || sameTeam))
            {
                // Don't react to damage that is entirely based on karma penalties (medics, poisons etc), unless applier is player
                return;
            }
            if (attacker.Submarine == null && Character.Submarine != null)
            {
                // Don't react to attackers that are outside of the sub (e.g. AoE attacks)
                return;
            }
            bool isAttackerInfected = false;
            bool isAttackerFightingEnemy = false;
            float minorDamageThreshold = 5;
            float majorDamageThreshold = 20;
            if (sameTeam && !attacker.IsInstigator)
            {
                minorDamageThreshold = 10;
                majorDamageThreshold = 40;
            }
            bool eitherIsMentallyUnstable = IsMentallyUnstable || attacker.AIController is { IsMentallyUnstable: true };
            if (IsFriendly(attacker))
            {
                if (attacker.AnimController.Anim == Barotrauma.AnimController.Animation.CPR && attacker.SelectedCharacter == Character)
                {
                    // Don't attack characters that damage you while doing cpr, because let's assume that they are helping you.
                    // Should not cancel any existing ai objectives (so that if the character attacked you and then helped, we still would want to retaliate).
                    return;
                }
                float cumulativeDamage = realDamage + Character.GetDamageDoneByAttacker(attacker);
                bool isAccidental = attacker.IsBot && !eitherIsMentallyUnstable && attacker.CombatAction == null;
                if (isAccidental)
                {
                    if (attacker.TeamID != Character.TeamID || (!Character.IsSecurity && cumulativeDamage > minorDamageThreshold))
                    {
                        AddCombatObjective(AIObjectiveCombat.CombatMode.Retreat, attacker);
                    }
                }
                else
                {
                    isAttackerInfected = attacker.CharacterHealth.GetAfflictionStrengthByType(AfflictionPrefab.AlienInfectedType) > 0;
                    // Inform other NPCs
                    if (isAttackerInfected || cumulativeDamage > minorDamageThreshold || totalDamage > minorDamageThreshold)
                    {
                        if (!attacker.IsPlayer || Character.TeamID != attacker.TeamID)
                        {
                            InformOtherNPCs(cumulativeDamage);
                        }
                    }
                    if (Character.IsBot)
                    {
                        var combatMode = DetermineCombatMode(Character, cumulativeDamage);
                        if (attacker.IsPlayer && !Character.IsInstigator && !ObjectiveManager.IsCurrentObjective<AIObjectiveCombat>())
                        {
                            switch (combatMode)
                            {
                                case AIObjectiveCombat.CombatMode.Defensive:
                                case AIObjectiveCombat.CombatMode.Retreat:
                                    if (Character.IsSecurity)
                                    {
                                        Character.Speak(TextManager.Get("dialogattackedbyfriendlysecurityresponse").Value, null, 0.5f, "attackedbyfriendlysecurityresponse".ToIdentifier(), minDurationBetweenSimilar: 10.0f);
                                    }
                                    else
                                    {
                                        Character.Speak(TextManager.Get("DialogAttackedByFriendly").Value, null, 0.5f, "attackedbyfriendly".ToIdentifier(), minDurationBetweenSimilar: 10.0f);
                                    }
                                    break;
                                case AIObjectiveCombat.CombatMode.Offensive:
                                case AIObjectiveCombat.CombatMode.Arrest:
                                    Character.Speak(TextManager.Get("dialogattackedbyfriendlysecurityarrest").Value, null, 0.5f, "attackedbyfriendlysecurityarrest".ToIdentifier(), minDurationBetweenSimilar: 10.0f);
                                    break;
                                case AIObjectiveCombat.CombatMode.None:
                                    if (Character.IsSecurity && realDamage > 1)
                                    {
                                        Character.Speak(TextManager.Get("dialogattackedbyfriendlysecurityresponse").Value, null, 0.5f, "attackedbyfriendlysecurityresponse".ToIdentifier(), minDurationBetweenSimilar: 10.0f);
                                    }
                                    break;
                            }
                        }
                        // If the attacker is using a low damage and high frequency weapon like a repair tool, we shouldn't use any delay.
                        AddCombatObjective(combatMode, attacker, delay: realDamage > 1 ? GetReactionTime() : 0);
                    }
                    if (!isAttackerFightingEnemy)
                    {
                        (GameMain.GameSession?.GameMode as CampaignMode)?.OutpostNPCAttacked(Character, attacker, attackResult);
                    }
                }
            }
            else
            {
                if (Character.Submarine != null && Character.Submarine.GetConnectedSubs().Contains(attacker.Submarine))
                {
                    // Non-friendly
                    InformOtherNPCs();
                }
                if (Character.IsBot)
                {
                    AddCombatObjective(DetermineCombatMode(Character), attacker);
                }
            }

            void InformOtherNPCs(float cumulativeDamage = 0)
            {
                foreach (Character otherCharacter in Character.CharacterList)
                {
                    if (otherCharacter == Character || otherCharacter.IsUnconscious || otherCharacter.Removed) { continue; }
                    if (otherCharacter.Submarine != Character.Submarine) { continue; }
                    if (otherCharacter.Submarine != attacker.Submarine) { continue; }
                    if (otherCharacter.Info?.Job == null || otherCharacter.IsInstigator) { continue; }
                    if (otherCharacter.IsPlayer) { continue; }
                    if (otherCharacter.AIController is not HumanAIController otherHumanAI) { continue; }
                    if (!otherHumanAI.IsFriendly(Character)) { continue; }
                    if (otherHumanAI.objectiveManager.IsCurrentObjective<AIObjectiveCombat>() || otherHumanAI.objectiveManager.HasActiveObjective<AIObjectiveCombat>())
                    {
                        // Already in combat, don't change target (because we are not attacked by the enemy)
                        return;
                    }
                    if (attacker.AIController is EnemyAIController && otherHumanAI.IsFriendly(attacker))
                    {
                        // Don't react to friendly enemy AI attacking other characters. E.g. husks attacking someone when whe are a cultist.
                        continue;
                    }
                    bool isWitnessing = 
                        otherHumanAI.VisibleHulls.Contains(Character.CurrentHull) || 
                        otherHumanAI.VisibleHulls.Contains(attacker.CurrentHull) ||
                        otherCharacter.CanSeeTarget(attacker, seeThroughWindows: true);
                    if (!isWitnessing) 
                    { 
                        if (Character.IsDead || Character.IsUnconscious || otherCharacter.TeamID != Character.TeamID)
                        {
                            // Dead or in different team -> cannot report.
                            continue;
                        }
                        if (otherHumanAI.objectiveManager.HasOrders())
                        {
                            // Unless witnessing the attack, don't react, if have been ordered to do something.
                            // The combat objective would take a higher prio than the order.
                            continue;
                        }
                        if (!CheckReportRange(Character, otherCharacter, ReportRange))
                        {
                            // Not inside report range -> cannot report.
                            continue;
                        }
                    }
                    var combatMode = DetermineCombatMode(otherCharacter, cumulativeDamage, isWitnessing);
                    if (!isWitnessing)
                    {
                        if (combatMode is AIObjectiveCombat.CombatMode.Defensive or AIObjectiveCombat.CombatMode.Retreat)
                        {
                            // Ignore defensive and retreating behavior, unless witnessing the attack.
                            continue;
                        }
                    }
                    float delay = isWitnessing ? GetReactionTime() : Rand.Range(2.0f, 3.0f, Rand.RandSync.Unsynced);
                    otherHumanAI.AddCombatObjective(combatMode, attacker, delay);
                }
            }

            AIObjectiveCombat.CombatMode DetermineCombatMode(Character c, float cumulativeDamage = 0, bool isWitnessing = false)
            {
                if (c.AIController is not HumanAIController humanAI) { return AIObjectiveCombat.CombatMode.None; }
                if (!IsFriendly(attacker))
                {
                    if (c.Submarine == null)
                    {
                        // Outside
                        return attacker.Submarine == null ? AIObjectiveCombat.CombatMode.Defensive : AIObjectiveCombat.CombatMode.Retreat;
                    }
                    if (!c.Submarine.GetConnectedSubs().Contains(attacker.Submarine))
                    {
                        // Attacked from an unconnected submarine (pirate/pvp)
                        return 
                            humanAI.ObjectiveManager.CurrentOrder is AIObjectiveOperateItem operateOrder && operateOrder.GetTarget() is Controller ? 
                                AIObjectiveCombat.CombatMode.None : AIObjectiveCombat.CombatMode.Retreat;
                    }
                    if (c.IsKiller)
                    {
                        return AIObjectiveCombat.CombatMode.Offensive;
                    }
                    return humanAI.ObjectiveManager.HasObjectiveOrOrder<AIObjectiveFightIntruders>() ? AIObjectiveCombat.CombatMode.Offensive : AIObjectiveCombat.CombatMode.Defensive;
                }
                else
                {
                    if (isAttackerInfected)
                    {
                        cumulativeDamage = 100;
                    }
                    if (attacker.IsPlayer && c.TeamID == attacker.TeamID)
                    {
                        // Bots in the player team never act aggressively when attacked by the player
                        return Character == c && cumulativeDamage > minorDamageThreshold ? AIObjectiveCombat.CombatMode.Retreat : AIObjectiveCombat.CombatMode.None;
                    }
                    if (c.Submarine == null || !c.Submarine.GetConnectedSubs().Contains(attacker.Submarine))
                    {
                        // Outside or attacked from an unconnected submarine -> don't react.
                        return AIObjectiveCombat.CombatMode.None;
                    }
                    // If there are any enemies around, just ignore the friendly fire
                    if (Character.CharacterList.Any(ch => ch.Submarine == c.Submarine && !ch.Removed && !ch.IsIncapacitated && !IsFriendly(ch) && VisibleHulls.Contains(ch.CurrentHull)))
                    {
                        isAttackerFightingEnemy = true;
                        return AIObjectiveCombat.CombatMode.None;
                    }
                    if (c.IsKiller)
                    {
                        return AIObjectiveCombat.CombatMode.Offensive;
                    }
                    if (isWitnessing && c.CombatAction != null && !c.IsSecurity)
                    {
                        return c.CombatAction.WitnessReaction;
                    }
                    if (!attacker.IsInstigator && c.IsOnFriendlyTeam(attacker) && FindInstigator() is Character instigator)
                    {
                        // The guards don't react to player's aggressions when there's an instigator around
                        isAttackerFightingEnemy = true;
                        return c.IsSecurity ? AIObjectiveCombat.CombatMode.None : instigator.CombatAction?.WitnessReaction ?? AIObjectiveCombat.CombatMode.Retreat;
                    }
                    if (attacker.TeamID == CharacterTeamType.FriendlyNPC && !eitherIsMentallyUnstable)
                    {
                        if (c.IsSecurity)
                        {
                            return attacker.CombatAction?.GuardReaction ?? AIObjectiveCombat.CombatMode.Offensive;
                        }
                        else
                        {
                            return attacker.CombatAction?.WitnessReaction ?? AIObjectiveCombat.CombatMode.Retreat;
                        }
                    }
                    else
                    {
                        if (humanAI.ObjectiveManager.GetLastActiveObjective<AIObjectiveCombat>() is AIObjectiveCombat currentCombatObjective && currentCombatObjective.Enemy == attacker)
                        {
                            // Already targeting the attacker -> treat as a more serious threat.
                            cumulativeDamage *= 2;
                            currentCombatObjective.AllowHoldFire = false;
                            attacker.IsCriminal = true;
                            attacker.IsActingOffensively = true;
                        }
                        if (attacker.IsCriminal)
                        {
                            // Always react if the attacker has been misbehaving earlier.
                            cumulativeDamage = Math.Max(cumulativeDamage, minorDamageThreshold);
                        }
                        if (cumulativeDamage > majorDamageThreshold)
                        {
                            attacker.IsCriminal = true;
                            attacker.IsActingOffensively = true;
                            if (c.IsSecurity)
                            {
                                return AIObjectiveCombat.CombatMode.Offensive;
                            }
                            else
                            {
                                return c == Character ? AIObjectiveCombat.CombatMode.Defensive : AIObjectiveCombat.CombatMode.Retreat;
                            }
                        }
                        else if (cumulativeDamage > minorDamageThreshold)
                        {
                            attacker.IsActingOffensively = true;
                            return c.IsSecurity ? AIObjectiveCombat.CombatMode.Arrest : AIObjectiveCombat.CombatMode.Retreat;
                        }
                        else
                        {
                            return AIObjectiveCombat.CombatMode.None;
                        }
                    }

                    Character FindInstigator()
                    {
                        if (Character.IsInstigator)
                        {
                            return Character;
                        }
                        if (attacker.IsInstigator)
                        {
                            return attacker;
                        }
                        if (c.IsInstigator)
                        {
                            return c;
                        }
                        if (c.AIController is HumanAIController humanAi)
                        {
                            return Character.CharacterList.FirstOrDefault(ch => ch.Submarine == c.Submarine && !ch.Removed && !ch.IsIncapacitated && ch.IsInstigator && humanAi.VisibleHulls.Contains(ch.CurrentHull));
                        }
                        return null;
                    }
                }
            }
        }

        public void AddCombatObjective(AIObjectiveCombat.CombatMode mode, Character target, float delay = 0, Func<AIObjective, bool> abortCondition = null, Action onAbort = null, Action onCompleted = null, bool allowHoldFire = false, bool speakWarnings = false)
        {
            if (mode == AIObjectiveCombat.CombatMode.None) { return; }
            if (Character.IsDead || Character.IsIncapacitated || Character.Removed) { return; }
            if (!Character.IsBot) { return; }
            if (ObjectiveManager.Objectives.FirstOrDefault(o => o is AIObjectiveCombat) is AIObjectiveCombat combatObjective)
            {
                // Don't replace offensive mode with something else
                if (combatObjective.Mode == AIObjectiveCombat.CombatMode.Offensive && mode != AIObjectiveCombat.CombatMode.Offensive) { return; }
                if (combatObjective.Mode != mode || combatObjective.Enemy != target || (combatObjective.Enemy == null && target == null))
                {
                    // Replace the old objective with the new.
                    ObjectiveManager.Objectives.Remove(combatObjective);
                    ObjectiveManager.AddObjective(CreateCombatObjective());
                }
            }
            else
            {
                if (delay > 0)
                {
                    ObjectiveManager.AddObjective(CreateCombatObjective(), delay);
                }
                else
                {
                    ObjectiveManager.AddObjective(CreateCombatObjective());
                }
            }

            AIObjectiveCombat CreateCombatObjective()
            {
                var objective = new AIObjectiveCombat(Character, target, mode, objectiveManager)
                {
                    AbortCondition = abortCondition,
                    AllowHoldFire = allowHoldFire,
                    SpeakWarnings = speakWarnings
                };
                if (onAbort != null)
                {
                    objective.Abandoned += onAbort;
                }
                if (onCompleted != null)
                {
                    objective.Completed += onCompleted;
                }
                return objective;
            }
        }

        public void SetOrder(Order order, bool speak = true)
        {
            objectiveManager.SetOrder(order, speak);
#if CLIENT
            HintManager.OnSetOrder(Character, order);
#endif
        }

        public AIObjective SetForcedOrder(Order order)
        {
            var objective = ObjectiveManager.CreateObjective(order);
            ObjectiveManager.SetForcedOrder(objective);
            return objective;
        }

        public void ClearForcedOrder()
        {
            ObjectiveManager.ClearForcedOrder();
        }

        public override void SelectTarget(AITarget target)
        {
            SelectedAiTarget = target;
        }

        public override void Reset()
        {
            base.Reset();
            objectiveManager.SortObjectives();
            SortTimer = sortObjectiveInterval;
            float waitDuration = characterWaitOnSwitch;
            if (ObjectiveManager.IsCurrentObjective<AIObjectiveIdle>())
            {
                waitDuration *= 2;
            }
            ObjectiveManager.WaitTimer = waitDuration;
        }

        public override bool Escape(float deltaTime) => UpdateEscape(deltaTime, canAttackDoors: false);

        private void CheckCrouching(float deltaTime)
        {
            crouchRaycastTimer -= deltaTime;
            if (crouchRaycastTimer > 0.0f) { return; }

            crouchRaycastTimer = crouchRaycastInterval;

            //start the raycast in front of the character in the direction it's heading to
            Vector2 startPos = Character.SimPosition;
            startPos.X += MathHelper.Clamp(Character.AnimController.TargetMovement.X, -1.0f, 1.0f);

            //do a raycast upwards to find any walls
            if (!Character.AnimController.TryGetCollider(0, out PhysicsBody mainCollider))
            {
                mainCollider = Character.AnimController.Collider;
            }
            float margin = 0.1f;
            if (shouldCrouch)
            {
                margin *= 2;
            }
            float minCeilingDist = mainCollider.Height / 2 + mainCollider.Radius + margin;

            shouldCrouch = Submarine.PickBody(startPos, startPos + Vector2.UnitY * minCeilingDist, null, Physics.CollisionWall, customPredicate: (fixture) => { return fixture.Body.UserData is not Submarine; }) != null;
        }

        public bool AllowCampaignInteraction()
        {
            if (Character == null || Character.Removed) { return false; }

            //some events might want to allow talking/examining characters that are incapacitated or in some "emergency" ai state,
            //so let's ignore those here
            var type = Character.CampaignInteractionType;
            if (type != CampaignMode.InteractionType.None &&
                type != CampaignMode.InteractionType.Talk &&
                type != CampaignMode.InteractionType.Examine)
            {
                if (Character.IsIncapacitated) { return false; }
                switch (ObjectiveManager.CurrentObjective)
                {
                    case AIObjectiveCombat _:
                    case AIObjectiveFindSafety _:
                    case AIObjectiveExtinguishFires _:
                    case AIObjectiveFightIntruders _:
                    case AIObjectiveFixLeaks _:
                        return false;
                }
            }
            return true;
        }
        
        /// <param name="objectiveManager">Used for checking the objective.</param>
        public bool NeedsDivingGear(Hull hull, out bool needsSuit, AIObjectiveManager objectiveManager = null)
        {
            needsSuit = false;
            bool needsAir = Character.NeedsAir && Character.CharacterHealth.OxygenLowResistance < 1;
            if (hull == null || 
                hull.WaterPercentage > 90 || 
                hull.LethalPressure > 0 || 
                hull.ConnectedGaps.Any(gap => !gap.IsRoomToRoom && gap.Open > 0.9f))
            {
                if (!Character.IsImmuneToPressure)
                {
                    // Always require a diving suit when operating an item in a flooding room.
                    needsSuit = hull == null || hull.LethalPressure > 0 || objectiveManager?.CurrentOrder is AIObjectiveOperateItem operateItem && operateItem.GetTarget().Item.CurrentHull == hull;   
                }
                return needsAir || needsSuit;
            }
            if (hull.WaterPercentage > 60 || (hull.IsWetRoom && hull.WaterPercentage > 10) || hull.OxygenPercentage < HULL_LOW_OXYGEN_PERCENTAGE + 1)
            {
                return needsAir;
            }
            return false;
        }

        public static bool HasDivingGear(Character character, float conditionPercentage = 0, bool requireOxygenTank = true) => 
            HasDivingSuit(character, conditionPercentage, requireOxygenTank) || HasDivingMask(character, conditionPercentage, requireOxygenTank);

        /// <summary>
        /// Check whether the character has a diving suit in usable condition, suitable pressure protection for the depth, plus some oxygen.
        /// </summary>
        public static bool HasDivingSuit(Character character, float conditionPercentage = 0, bool requireOxygenTank = true, bool requireSuitablePressureProtection = true) 
            => HasItem(character, Tags.HeavyDivingGear, out _, requireOxygenTank ? Tags.OxygenSource : Identifier.Empty, conditionPercentage, requireEquipped: true,
                predicate: (Item item) => 
                    character.HasEquippedItem(item, InvSlotType.OuterClothes | InvSlotType.InnerClothes) &&
                    (!requireSuitablePressureProtection || AIObjectiveFindDivingGear.IsSuitablePressureProtection(item, Tags.HeavyDivingGear, character)));

        /// <summary>
        /// Check whether the character has a diving mask in usable condition plus some oxygen.
        /// </summary>
        public static bool HasDivingMask(Character character, float conditionPercentage = 0, bool requireOxygenTank = true) 
            => HasItem(character, Tags.LightDivingGear, out _, requireOxygenTank ? Tags.OxygenSource : Identifier.Empty, conditionPercentage, requireEquipped: true);

        private static List<Item> matchingItems = new List<Item>();

        /// <summary>
        /// Note: uses a single list for matching items. The item is reused each time when the method is called. So if you use the method twice, and then refer to the first items, you'll actually get the second. 
        /// To solve this, create a copy of the collection or change the code so that you first handle the first items and only after that query for the next items.
        /// </summary>
        public static bool HasItem(Character character, Identifier tagOrIdentifier, out IEnumerable<Item> items, Identifier containedTag = default, float conditionPercentage = 0, bool requireEquipped = false, bool recursive = true, Func<Item, bool> predicate = null)
        {
            matchingItems.Clear();
            items = matchingItems;
            if (character?.Inventory == null) { return false; }
            matchingItems = character.Inventory.FindAllItems(i => (i.Prefab.Identifier == tagOrIdentifier || i.HasTag(tagOrIdentifier)) &&
                i.ConditionPercentage >= conditionPercentage &&
                (!requireEquipped || character.HasEquippedItem(i)) &&
                (predicate == null || predicate(i)), recursive, matchingItems);
            items = matchingItems;
            foreach (var item in matchingItems)
            {
                if (item == null) { continue; }

                if (containedTag.IsEmpty || item.OwnInventory == null)
                {
                    //no contained items required, this item's ok
                    return true;
                }
                var suitableSlot = item.GetComponent<ItemContainer>().FindSuitableSubContainerIndex(containedTag);
                if (suitableSlot == null)
                {
                    //no restrictions on the suitable slot
                    return item.ContainedItems.Any(it => it.HasTag(containedTag) && it.ConditionPercentage > conditionPercentage);
                }
                else
                {
                    return item.ContainedItems.Any(it => it.HasTag(containedTag) && it.ConditionPercentage > conditionPercentage && it.ParentInventory.IsInSlot(it, suitableSlot.Value));
                }
            }
            return false;
        }

        public static void StructureDamaged(Structure structure, float damageAmount, Character character)
        {
            const float MaxDamagePerSecond = 5.0f;
            const float MaxDamagePerFrame = MaxDamagePerSecond * (float)Timing.Step;

            const float WarningThreshold = 5.0f;
            const float ArrestThreshold = 20.0f;
            const float KillThreshold = 50.0f;

            if (character == null || damageAmount <= 0.0f) { return; }
            if (structure?.Submarine == null || !structure.Submarine.Info.IsOutpost || character.TeamID == structure.Submarine.TeamID) { return; }
            //structure not indestructible = something that's "meant" to be destroyed, like an ice wall in mines
            if (!structure.Prefab.IndestructibleInOutposts) { return; }

            bool someoneSpoke = false;
            float maxAccumulatedDamage = 0.0f;
            foreach (Character otherCharacter in Character.CharacterList)
            {
                if (otherCharacter == character || otherCharacter.TeamID == character.TeamID || otherCharacter.IsDead ||
                    otherCharacter.Info?.Job == null ||
                    otherCharacter.AIController is not HumanAIController otherHumanAI ||
                    Vector2.DistanceSquared(otherCharacter.WorldPosition, character.WorldPosition) > 1000.0f * 1000.0f)
                {
                    continue;
                }
                if (!otherCharacter.CanSeeTarget(character, seeThroughWindows: true)) { continue; }

                otherHumanAI.structureDamageAccumulator.TryAdd(character, 0.0f);
                float prevAccumulatedDamage = otherHumanAI.structureDamageAccumulator[character];
                otherHumanAI.structureDamageAccumulator[character] += MathHelper.Clamp(damageAmount, -MaxDamagePerFrame, MaxDamagePerFrame);
                float accumulatedDamage = Math.Max(otherHumanAI.structureDamageAccumulator[character], maxAccumulatedDamage);
                maxAccumulatedDamage = Math.Max(accumulatedDamage, maxAccumulatedDamage);

                if (GameMain.GameSession?.Campaign?.Map?.CurrentLocation?.Reputation != null && character.IsPlayer)
                {
                    var reputationLoss = damageAmount * Reputation.ReputationLossPerWallDamage;
                    GameMain.GameSession.Campaign.Map.CurrentLocation.Reputation.AddReputation(-reputationLoss, Reputation.MaxReputationLossFromWallDamage);
                }

                if (!character.IsCriminal)
                {
                    if (accumulatedDamage <= WarningThreshold) { return; }

                    if (accumulatedDamage > WarningThreshold && prevAccumulatedDamage <= WarningThreshold &&
                        !someoneSpoke && !character.IsIncapacitated && character.Stun <= 0.0f)
                    {
                        //if the damage is still fairly low, wait and see if the character keeps damaging the walls to the point where we need to intervene
                        if (accumulatedDamage < ArrestThreshold)
                        {
                            if (otherHumanAI.ObjectiveManager.CurrentObjective is AIObjectiveIdle idleObjective)
                            {
                                idleObjective.FaceTargetAndWait(character, 5.0f);
                            }
                        }
                        otherCharacter.Speak(TextManager.Get("dialogdamagewallswarning").Value, null, Rand.Range(0.5f, 1.0f), "damageoutpostwalls".ToIdentifier(), 10.0f);
                        someoneSpoke = true;
                    }
                }
                
                // React if we are security
                if (character.IsCriminal ||
                    (accumulatedDamage > ArrestThreshold && prevAccumulatedDamage <= ArrestThreshold) ||
                    (accumulatedDamage > KillThreshold && prevAccumulatedDamage <= KillThreshold))
                {
                    var combatMode = accumulatedDamage > KillThreshold ? AIObjectiveCombat.CombatMode.Offensive : AIObjectiveCombat.CombatMode.Arrest;
                    if (combatMode == AIObjectiveCombat.CombatMode.Offensive)
                    {
                        character.IsCriminal = true;
                        character.IsActingOffensively = true;
                    }
                    if (!TriggerSecurity(otherHumanAI, combatMode))
                    {
                        // Else call the others
                        foreach (Character security in Character.CharacterList.Where(c => c.TeamID == otherCharacter.TeamID).OrderBy(c => Vector2.DistanceSquared(character.WorldPosition, c.WorldPosition)))
                        {
                            if (!TriggerSecurity(security.AIController as HumanAIController, combatMode))
                            {
                                // Only alert one guard at a time
                                return;
                            }
                        }
                    }
                }
            }

            bool TriggerSecurity(HumanAIController humanAI, AIObjectiveCombat.CombatMode combatMode)
            {
                if (humanAI == null) { return false; }
                if (!humanAI.Character.IsSecurity) { return false; }
                if (humanAI.ObjectiveManager.IsCurrentObjective<AIObjectiveCombat>()) { return false; }
                humanAI.AddCombatObjective(combatMode, character, delay: GetReactionTime(),
                    onCompleted: () => 
                    { 
                        //if the target is arrested successfully, reset the damage accumulator
                        foreach (Character anyCharacter in Character.CharacterList)
                        {
                            if (anyCharacter.AIController is HumanAIController anyAI)
                            {
                                anyAI.structureDamageAccumulator?.Remove(character);
                            }
                        }
                    });
                return true;
            }
        }

        public static void ItemTaken(Item item, Character thief)
        {
            if (item == null || thief == null || item.GetComponent<LevelResource>() != null) { return; }
            if (thief.IsBot && item.HasTag(AIObjectiveGetItem.AllowedItemsToTake))
            {
                // Bots are allowed to take diving gear or extinguishers, when they need them, without it being considered as stealing.
                return;
            }
            bool someoneSpoke = false;
            if (item.Illegitimate && item.GetRootInventoryOwner() is Character itemOwner && itemOwner != thief && itemOwner.TeamID == thief.TeamID)
            {
                // The player attempts to use a bot as a mule or get them arrested -> just arrest the player instead.
                thief.IsCriminal = true;
            }
            bool foundIllegitimateItems = item.Illegitimate || item.OwnInventory?.FindItem(it => it.Illegitimate, recursive: true) != null;
            if (foundIllegitimateItems && thief.TeamID != CharacterTeamType.FriendlyNPC)
            {
                foreach (Character otherCharacter in Character.CharacterList)
                {
                    if (otherCharacter == thief || otherCharacter.TeamID == thief.TeamID || otherCharacter.IsIncapacitated || otherCharacter.Stun > 0.0f ||
                        otherCharacter.Info?.Job == null || otherCharacter.AIController is not HumanAIController otherHumanAI || otherCharacter.IsEscorted ||
                        Vector2.DistanceSquared(otherCharacter.WorldPosition, thief.WorldPosition) > 1000.0f * 1000.0f)
                    {
                        continue;
                    }
                    //if (!otherCharacter.IsFacing(thief.WorldPosition)) { continue; }
                    if (!otherCharacter.CanSeeTarget(thief, seeThroughWindows: true)) { continue; }
                    // Don't react if the player is taking an extinguisher and there's any fires on the sub, or diving gear when the sub is flooding
                    // -> allow them to use the emergency items
                    if (thief.Submarine != null)
                    {
                        var connectedHulls = thief.Submarine.GetHulls(alsoFromConnectedSubs: true);
                        if (item.HasTag(Tags.FireExtinguisher) && connectedHulls.Any(h => h.FireSources.Any())) { continue; }
                        if (item.HasTag(Tags.DivingGear) && connectedHulls.Any(h => h.ConnectedGaps.Any(g => AIObjectiveFixLeaks.IsValidTarget(g, thief)))) { continue; }
                    }
                    if (item.HasTag(Tags.Handcuffs) && thief.HasEquippedItem(item))
                    {
                        // Handcuffed -> don't react.
                        continue;
                    }
                    if (!item.StolenDuringRound)
                    {
                        item.StolenDuringRound = true;
                        ApplyStealingReputationLoss(item);
#if CLIENT
                        HintManager.OnStoleItem(thief, item);
#endif
                    }
                    if (!someoneSpoke)
                    {
                        otherCharacter.Speak(TextManager.Get("dialogstealwarning").Value, null, Rand.Range(0.5f, 1.0f), "thief".ToIdentifier(), 10.0f);
                        someoneSpoke = true;
                    }
                    // React if we are security
                    if (!TriggerSecurity(otherHumanAI))
                    {
                        // Else call the others
                        foreach (Character security in Character.CharacterList.Where(c => c.TeamID == otherCharacter.TeamID).OrderBy(c => Vector2.DistanceSquared(thief.WorldPosition, c.WorldPosition)))
                        {
                            if (TriggerSecurity(security.AIController as HumanAIController))
                            {
                                // Only alert one guard at a time
                                break;
                            }
                        }
                    }
                }
            }
            else if (item.OwnInventory?.FindItem(it => it.Illegitimate, true) is { } foundItem)
            {
                ItemTaken(foundItem, thief);
            }

            bool TriggerSecurity(HumanAIController humanAI)
            {
                if (humanAI == null) { return false; }
                if (!humanAI.Character.IsSecurity) { return false; }
                if (humanAI.ObjectiveManager.IsCurrentObjective<AIObjectiveCombat>()) { return false; }
                if (humanAI.ObjectiveManager.GetObjective<AIObjectiveFindThieves>() is { } findThieves)
                {
                    findThieves.InspectEveryone();
                }
                bool isCriminal = thief.IsCriminal;
                humanAI.AddCombatObjective(AIObjectiveCombat.CombatMode.Arrest, thief, delay: GetReactionTime(),
                    abortCondition: obj => !isCriminal && thief.Inventory.FindItem(it => it.Illegitimate, recursive: true) == null,
                    onAbort: () =>
                    {
                        if (!item.Removed && !humanAI.ObjectiveManager.IsCurrentObjective<AIObjectiveGetItem>())
                        {
                            humanAI.ObjectiveManager.AddObjective(new AIObjectiveGetItem(humanAI.Character, item, humanAI.ObjectiveManager, equip: false)
                            {
                                BasePriority = 10
                            });
                        }
                    },
                    allowHoldFire: !isCriminal,
                    speakWarnings: !isCriminal);
                return true;
            }
        }

        public static void ApplyStealingReputationLoss(Item item)
        {
            if (Level.Loaded?.Type == LevelData.LevelType.Outpost &&
                GameMain.GameSession?.Campaign?.Map?.CurrentLocation != null)
            {
                var reputationLoss = MathHelper.Clamp(
                    (item.Prefab.GetMinPrice() ?? 0) * Reputation.ReputationLossPerStolenItemPrice,
                    Reputation.MinReputationLossPerStolenItem, Reputation.MaxReputationLossPerStolenItem);
                GameMain.GameSession.Campaign.Map.CurrentLocation.Reputation?.AddReputation(-reputationLoss);
            }
        }

        // 0.225 - 0.375
        private static float GetReactionTime() => reactionTime * Rand.Range(0.75f, 1.25f);

        /// <summary>
        /// Updates the hull safety for all ai characters in the team. The idea is that the crew communicates (magically) via radio about the threats.
        /// The safety levels need to be calculated for each bot individually, because the formula takes into account things like current orders.
        /// There's now a cached value per each hull, which should prevent too frequent calculations.
        /// </summary>
        private static void PropagateHullSafety(Character character, Hull hull)
        {
            DoForEachBot(character, humanAi => humanAi.RefreshHullSafety(hull));
        }

        public void AskToRecalculateHullSafety(Hull hull) => dirtyHullSafetyCalculations.Add(hull);

        private void RefreshHullSafety(Hull hull)
        {
            var visibleHulls = dirtyHullSafetyCalculations.Contains(hull) ? hull.GetConnectedHulls(includingThis: true, searchDepth: 1) : null;
            float hullSafety = GetHullSafety(hull, Character, visibleHulls);
            if (hullSafety > HULL_SAFETY_THRESHOLD)
            {
                UnsafeHulls.Remove(hull);
            }
            else
            {
                UnsafeHulls.Add(hull);
            }
        }

        private static void RefreshTargets(Character character, Order order, Hull hull)
        {
            switch (order.Identifier.Value.ToLowerInvariant())
            {
                case "reportfire":
                    AddTargets<AIObjectiveExtinguishFires, Hull>(character, hull);
                    break;
                case "reportbreach":
                    foreach (var gap in hull.ConnectedGaps)
                    {
                        if (AIObjectiveFixLeaks.IsValidTarget(gap, character))
                        {
                            AddTargets<AIObjectiveFixLeaks, Gap>(character, gap);
                        }
                    }
                    break;
                case "reportbrokendevices":
                    foreach (var item in Item.RepairableItems)
                    {
                        if (item.CurrentHull != hull) { continue; }
                        if (AIObjectiveRepairItems.IsValidTarget(item, character))
                        {
                            if (item.Repairables.All(r => r.IsBelowRepairThreshold)) { continue; }
                            AddTargets<AIObjectiveRepairItems, Item>(character, item);
                        }
                    }
                    break;
                case "reportintruders":
                    foreach (var enemy in Character.CharacterList)
                    {
                        if (enemy.CurrentHull != hull) { continue; }
                        if (AIObjectiveFightIntruders.IsValidTarget(enemy, character, targetCharactersInOtherSubs: false))
                        {
                            AddTargets<AIObjectiveFightIntruders, Character>(character, enemy);
                        }
                    }
                    break;
                case "requestfirstaid":
                    foreach (var c in Character.CharacterList)
                    {
                        if (c.CurrentHull != hull) { continue; }
                        if (AIObjectiveRescueAll.IsValidTarget(c, character, out _))
                        {
                            AddTargets<AIObjectiveRescueAll, Character>(character, c);
                        }
                    }
                    break;
                default:
#if DEBUG
                    DebugConsole.ThrowError(order.Identifier + " not implemented!");
#endif
                    break;
            }
        }

        private static bool AddTargets<T1, T2>(Character caller, T2 target) where T1 : AIObjectiveLoop<T2>
        {
            bool targetAdded = false;
            DoForEachBot(caller, humanAI =>
            {
                if (caller != humanAI.Character && caller.SpeechImpediment >= 100) { return; }
                var objective = humanAI.ObjectiveManager.GetObjective<T1>();
                if (objective != null)
                {
                    if (!targetAdded && objective.AddTarget(target))
                    {
                        targetAdded = true;
                    }
                }
            }, range: (caller.AIController as HumanAIController)?.ReportRange ?? float.PositiveInfinity);
            return targetAdded;
        }

        public static void RemoveTargets<T1, T2>(Character caller, T2 target) where T1 : AIObjectiveLoop<T2>
        {
            DoForEachBot(caller, humanAI =>
                humanAI.ObjectiveManager.GetObjective<T1>()?.ReportedTargets.Remove(target));
        }

        private void StoreHullSafety(Hull hull, HullSafety safety)
        {
            if (knownHulls.ContainsKey(hull))
            {
                // Update existing. Shouldn't currently happen, but things might change.
                knownHulls[hull] = safety;
            }
            else
            {
                // Add new
                knownHulls.Add(hull, safety);
            }
        }

        private float CalculateHullSafety(Hull hull, Character character, IEnumerable<Hull> visibleHulls = null)
        {
            bool isCurrentHull = character == Character && character.CurrentHull == hull;
            if (hull == null)
            {
                float hullSafety = character.IsProtectedFromPressure ? 0 : 100;
                if (isCurrentHull)
                {
                    CurrentHullSafety = hullSafety;
                }
                return hullSafety;
            }
            if (isCurrentHull && visibleHulls == null)
            {
                // Use the cached visible hulls
                visibleHulls = VisibleHulls;
            }
            bool ignoreFire = objectiveManager.CurrentOrder is AIObjectiveExtinguishFires { Priority: > 0 } || objectiveManager.HasActiveObjective<AIObjectiveExtinguishFire>();
            bool ignoreOxygen = HasDivingGear(character);
            bool ignoreEnemies = ObjectiveManager.HasObjectiveOrOrder<AIObjectiveFightIntruders>();
            float safety = CalculateHullSafety(hull, visibleHulls, character, ignoreWater: false, ignoreOxygen, ignoreFire, ignoreEnemies);
            if (isCurrentHull)
            {
                CurrentHullSafety = safety;
            }
            return safety;
        }
        
        /// <summary>
        /// Returns hull safety for the character without ignoring any threats.
        /// Useful for example, when we need to calculate a safety value of the hull regardless of the protective equipment or buffs of the character.
        /// No caching involved (always recalculated).
        /// </summary>
        public static float CalculateObjectiveHullSafety(Character character) => CalculateHullSafety(
            hull: character.CurrentHull, 
            visibleHulls: character.AIController?.VisibleHulls ?? character.GetVisibleHulls(),
            character, 
            ignoreEnemies: false, ignoreFire: false, ignoreWater: false, ignoreOxygen: false, ignorePressureProtection: true);

        private static float CalculateHullSafety(Hull hull, IEnumerable<Hull> visibleHulls, Character character, bool ignoreWater = false, bool ignoreOxygen = false, bool ignoreFire = false, bool ignoreEnemies = false, bool ignorePressureProtection = false)
        {
            bool isProtectedFromPressure = character.IsProtectedFromPressure;
            if (!ignorePressureProtection)
            {
                if (hull == null) { return isProtectedFromPressure ? 100 : 0; }
                if (hull.LethalPressure > 0 && !isProtectedFromPressure) { return 0; }
            }
            else
            {
                if (hull == null) { return 0; }
            }

            // Oxygen factor should be 1 with 70% oxygen or more and 0.1 when the oxygen level is 30% or lower.
            // With insufficient oxygen, the safety of the hull should be 39, all the other factors aside. So, just below the HULL_SAFETY_THRESHOLD.
            float oxygenFactor = ignoreOxygen ? 1 : MathHelper.Lerp((HULL_SAFETY_THRESHOLD - 1) / 100, 1, MathUtils.InverseLerp(HULL_LOW_OXYGEN_PERCENTAGE, 100 - HULL_LOW_OXYGEN_PERCENTAGE, hull.OxygenPercentage));
            float waterFactor = 1;
            if (!ignoreWater && !isProtectedFromPressure)
            {
                if (visibleHulls != null)
                {
                    // Take the visible hulls into account too, because otherwise multi-hull rooms on several floors (with platforms) will yield unexpected results.
                    float relativeWaterVolume = visibleHulls.Sum(s => s.WaterVolume) / visibleHulls.Sum(s => s.Volume);
                    waterFactor = MathHelper.Lerp(1, HULL_SAFETY_THRESHOLD / 2 / 100, relativeWaterVolume);
                }
                else
                {
                    float relativeWaterVolume = hull.WaterVolume / hull.Volume;
                    waterFactor = MathHelper.Lerp(1, HULL_SAFETY_THRESHOLD / 2 / 100, relativeWaterVolume);
                }
            }
            if (!ignoreOxygen)
            {
                if (!character.NeedsOxygen || character.CharacterHealth.OxygenLowResistance >= 1)
                {
                    oxygenFactor = 1;
                }
            }
            float fireFactor = 1;
            if (!ignoreFire)
            {
                float fire = CalculateFire(hull) + hull.linkedTo.Sum(e => CalculateFire(e as Hull));
                fireFactor = MathHelper.Lerp(1, 0, MathHelper.Clamp(fire, 0, 1));
                
                float CalculateFire(Hull h)
                {
                    if (h is not Hull) { return 0; }
                    bool isInDamageRange = h.FireSources.Any(fs => fs.IsInDamageRange(character, fs.DamageRange));
                    if (isInDamageRange) { return 1; }
                    // Even the smallest fire reduces the safety by 50%
                    return h.FireSources.Count * 0.5f + h.FireSources.Sum(fs => fs.DamageRange) / h.Size.X;
                }
            }
            float enemyFactor = 1;
            if (!ignoreEnemies)
            {
                float enemyCount = 0;
                foreach (Character c in Character.CharacterList)
                {
                    float countModifier = 1.0f;
                    if (c.CurrentHull == null) { continue; }
                    if (visibleHulls == null)
                    {
                        if (c.CurrentHull != hull && !c.CurrentHull.linkedTo.Contains(hull)) { continue; }
                    }
                    else
                    {
                        if (!visibleHulls.Contains(c.CurrentHull)) { continue; }
                        if (c.CurrentHull != hull && !c.CurrentHull.linkedTo.Contains(hull))
                        {
                            // Enemy in a visible room, but not in the same room -> a lower threat
                            countModifier = 0.25f;
                        }
                    }
                    if (IsActive(c) && !IsFriendly(character, c) && !c.IsHandcuffed)
                    {
                        enemyCount += countModifier;
                    }
                }
                // The hull safety decreases 90% per enemy up to 100%,
                // and 22.5% per each enemy in the visible, adjacent rooms
                enemyFactor = MathHelper.Lerp(1, 0, MathHelper.Clamp(enemyCount * 0.9f, 0, 1));
            }
            float dangerousItemsFactor = 1f;
            foreach (Item item in Item.DangerousItems)
            {
                if (item.CurrentHull == hull) 
                { 
                    dangerousItemsFactor = 0;
                    break;
                }
            }
            float safety = oxygenFactor * waterFactor * fireFactor * enemyFactor * dangerousItemsFactor;
            return MathHelper.Clamp(safety * 100, 0, 100);
        }

        public float GetHullSafety(Hull hull, Character character, IEnumerable<Hull> visibleHulls = null)
        {
            if (hull == null)
            {
                return CalculateHullSafety(null, character, visibleHulls);
            }
            if (!knownHulls.TryGetValue(hull, out HullSafety hullSafety))
            {
                hullSafety = new HullSafety(CalculateHullSafety(hull, character, visibleHulls));
                StoreHullSafety(hull, hullSafety);
            }
            else if (hullSafety.IsStale)
            {
                hullSafety.Reset(CalculateHullSafety(hull, character, visibleHulls));
            }
            return hullSafety.safety;
        }

        public static float GetHullSafety(Hull hull, IEnumerable<Hull> visibleHulls, Character character, bool ignoreWater = false, bool ignoreOxygen = false, bool ignoreFire = false, bool ignoreEnemies = false)
        {
            if (hull == null)
            {
                return CalculateHullSafety(null, visibleHulls, character, ignoreWater, ignoreOxygen, ignoreFire, ignoreEnemies);
            }
            HullSafety hullSafety;
            if (character.AIController is HumanAIController controller)
            {
                if (!controller.knownHulls.TryGetValue(hull, out hullSafety))
                {
                    hullSafety = new HullSafety(CalculateHullSafety(hull, visibleHulls, character, ignoreWater, ignoreOxygen, ignoreFire, ignoreEnemies));
                    controller.StoreHullSafety(hull, hullSafety);
                }
                else if (hullSafety.IsStale)
                {
                    hullSafety.Reset(CalculateHullSafety(hull, visibleHulls, character, ignoreWater, ignoreOxygen, ignoreFire, ignoreEnemies));
                }
            }
            else
            {
#if DEBUG
                DebugConsole.ThrowError("Cannot store the hull safety, because was unable to cast the AIController as HumanAIController. This should never happen!");
#endif
                return CalculateHullSafety(hull, visibleHulls, character, ignoreWater, ignoreOxygen, ignoreFire, ignoreEnemies);
            }
            return hullSafety.safety;
        }

        public static bool IsFriendly(Character me, Character other, bool onlySameTeam = false, bool ignoreHuskDisguising = false)
        {
            if (onlySameTeam)
            {
                ignoreHuskDisguising = true;
            }
            if (other.IsHusk && !ignoreHuskDisguising)
            {
                return me.IsDisguisedAsHusk;
            }
            else
            {
                if (other.IsPrisoner && me.IsPrisoner)
                {
                    // Both prisoners
                    return true;
                }
                if (other.IsHostileEscortee && me.IsHostileEscortee)
                {
                    // Both hostile escortees
                    return true;
                }
            }  
            bool sameTeam = me.TeamID == other.TeamID;
            bool teamGood = sameTeam || !onlySameTeam && me.IsOnFriendlyTeam(other);
            if (!teamGood)
            {
                return false;  
            }
            if (other.IsPet)
            {
                // Hostile NPCs are hostile to all pets, unless they are in the same team.
                return sameTeam || me.TeamID != CharacterTeamType.None;
            }
            else
            {
                if (!me.IsSameSpeciesOrGroup(other)) { return false; }
            }
            if (GameMain.GameSession?.GameMode is CampaignMode && 
                //ignore hostile faction if offering services that don't get disabled by faction hostility
                (me.CampaignInteractionType == CampaignMode.InteractionType.None || CampaignMode.HostileFactionDisablesInteraction(me.CampaignInteractionType)))
            {
                if ((me.TeamID == CharacterTeamType.FriendlyNPC && other.TeamID == CharacterTeamType.Team1) ||
                    (me.TeamID == CharacterTeamType.Team1 && other.TeamID == CharacterTeamType.FriendlyNPC))
                {
                    Character npc = me.TeamID == CharacterTeamType.FriendlyNPC ? me : other;

                    if (npc.AIController is HumanAIController npcAI)
                    {
                        return !npcAI.IsInHostileFaction();
                    }
                }
            }
            return true;
        }

        public bool IsInHostileFaction()
        {
            if (GameMain.GameSession?.GameMode is not CampaignMode campaign) { return false; }
            if (Character.IsEscorted) { return false; }

            Identifier npcFaction = Character.Faction;
            Identifier currentLocationFaction = campaign.Map?.CurrentLocation?.Faction?.Prefab.Identifier ?? Identifier.Empty;

            if (npcFaction.IsEmpty)
            {
                //if faction identifier is not specified, assume the NPC is a member of the faction that owns the outpost
                npcFaction = currentLocationFaction;
            }
            if (!currentLocationFaction.IsEmpty && npcFaction == currentLocationFaction)
            {
                if (campaign.CurrentLocation is { IsFactionHostile: true })
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsActive(Character c) => c is { Enabled: true, IsUnconscious: false };

        public static bool IsTrueForAllBotsInTheCrew(Character character, Func<HumanAIController, bool> predicate)
        {
            if (character == null) { return false; }
            foreach (var c in Character.CharacterList)
            {
                if (!IsBotInTheCrew(character, c)) { continue; }
                if (!predicate(c.AIController as HumanAIController))
                {
                    return false;
                }
            }
            return true;
        }

        public static bool IsTrueForAnyBotInTheCrew(Character character, Func<HumanAIController, bool> predicate)
        {
            if (character == null) { return false; }
            foreach (var c in Character.CharacterList)
            {
                if (!IsBotInTheCrew(character, c)) { continue; }
                if (predicate(c.AIController as HumanAIController))
                {
                    return true;
                }
            }
            return false;
        }

        public static int CountBotsInTheCrew(Character character, Func<HumanAIController, bool> predicate = null)
        {
            if (character == null) { return 0; }
            int count = 0;
            foreach (var other in Character.CharacterList)
            {
                if (!IsBotInTheCrew(character, other)) { continue; }
                if (predicate == null || predicate(other.AIController as HumanAIController))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Including the player characters in the same team.
        /// </summary>
        public bool IsTrueForAnyCrewMember(Func<Character, bool> predicate, bool onlyActive = true, bool onlyConnectedSubs = false)
        {
            foreach (var c in Character.CharacterList)
            {
                if (!IsActive(c)) { continue; }
                if (c.TeamID != Character.TeamID) { continue; }
                if (onlyActive && c.IsIncapacitated) { continue; }
                if (onlyConnectedSubs)
                {
                    if (Character.Submarine == null)
                    {
                        if (c.Submarine != null)
                        {
                            return false;
                        }
                    }
                    else if (c.Submarine != Character.Submarine && !Character.Submarine.GetConnectedSubs().Contains(c.Submarine))
                    {
                        return false;
                    }
                }
                if (predicate(c))
                {
                    return true;
                }
            }
            return false;
        }

        private static void DoForEachBot(Character character, Action<HumanAIController> action, float range = float.PositiveInfinity)
        {
            if (character == null) { return; }
            foreach (Character c in Character.CharacterList)
            {
                if (IsBotInTheCrew(character, c) && CheckReportRange(character, c, range))
                {
                    action(c.AIController as HumanAIController);
                }
            }
        }

        private static bool CheckReportRange(Character character, Character target, float range)
        {
            if (float.IsPositiveInfinity(range)) { return true; }
            if (character.CurrentHull == null || target.CurrentHull == null)
            {
                return Vector2.DistanceSquared(character.WorldPosition, target.WorldPosition) <= range * range;
            }
            else
            {
                return character.CurrentHull.GetApproximateDistance(character.Position, target.Position, target.CurrentHull, range, distanceMultiplierPerClosedDoor: 2) <= range;
            }
        }

        private static bool IsBotInTheCrew(Character self, Character other) => IsActive(other) && other.TeamID == self.TeamID && !other.IsIncapacitated && other.IsBot && other.AIController is HumanAIController;

        public static bool IsItemTargetedBySomeone(ItemComponent target, CharacterTeamType team, out Character operatingCharacter)
        {
            operatingCharacter = null;
            if (target?.Item == null) { return false; }
            float highestPriority = -1.0f;
            float highestPriorityModifier = -1.0f;
            foreach (Character c in Character.CharacterList)
            {
                if (c == null) { continue; }
                if (c.Removed) { continue; }
                if (c.TeamID != team) { continue; }
                if (c.IsIncapacitated) { continue; }
                if (c.SelectedItem == target.Item)
                {
                    operatingCharacter = c;
                    return true;
                }
                if (c.AIController is HumanAIController { ObjectiveManager: AIObjectiveManager objectiveManager })
                {
                    foreach (var objective in objectiveManager.Objectives)
                    {
                        if (objective is not AIObjectiveOperateItem operateObjective) { continue; }
                        if (operateObjective.Component?.Item != target.Item) { continue; }
                        if (operateObjective.Priority < highestPriority) { continue; }
                        if (operateObjective.PriorityModifier < highestPriorityModifier) { continue; }
                        operatingCharacter = c;
                        highestPriority = operateObjective.Priority;
                        highestPriorityModifier = operateObjective.PriorityModifier;
                    }
                }
            }
            return operatingCharacter != null;
        }

        #region Wrappers
        public bool IsFriendly(Character other, bool onlySameTeam = false) => IsFriendly(Character, other, onlySameTeam);
        public bool IsTrueForAnyBotInTheCrew(Func<HumanAIController, bool> predicate) => IsTrueForAnyBotInTheCrew(Character, predicate);
        public bool IsTrueForAllBotsInTheCrew(Func<HumanAIController, bool> predicate) => IsTrueForAllBotsInTheCrew(Character, predicate);
        public int CountBotsInTheCrew(Func<HumanAIController, bool> predicate = null) => CountBotsInTheCrew(Character, predicate);
        #endregion
    }
}
