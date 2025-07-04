﻿using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma.Extensions;
using Barotrauma.Items.Components;

namespace Barotrauma
{
    class AIObjectiveGoTo : AIObjective
    {
        public override Identifier Identifier { get; set; } = "go to".ToIdentifier();
        public override string DebugTag => $"{Identifier} ({Target?.ToString() ?? "none"})";

        public override bool KeepDivingGearOn => GetTargetHull() == null;

        /// <summary>
        /// Is the goal of this objective to get diving gear (i.e. has it been created by <see cref="AIObjectiveFindDivingGear"/>)? 
        /// If so, the objective won't attempt to create another objective if the path requires diving gear 
        /// (wouldn't make sense to start looking for diving gear so the bot can get to a room they're trying to get diving gear from!)
        /// </summary>
        public bool IsFindDivingGearSubObjective;

        private AIObjectiveFindDivingGear findDivingGear;
        private readonly bool repeat;
        //how long until the path to the target is declared unreachable
        private float waitUntilPathUnreachable;
        private readonly bool getDivingGearIfNeeded;

        /// <summary>
        /// Doesn't allow the objective to complete if this condition is false
        /// </summary>
        public Func<bool> requiredCondition;
        public Func<PathNode, bool> endNodeFilter;

        public Func<float> PriorityGetter;

        public bool IsFollowOrder;
        public bool IsWaitOrder;
        public bool Mimic;

        public bool SpeakIfFails { get; set; } = true;
        public bool DebugLogWhenFails { get; set; } = true;
        public bool UsePathingOutside { get; set; } = true;

        public float ExtraDistanceWhileSwimming;
        public float ExtraDistanceOutsideSub;
        private float _closeEnoughMultiplier = 1;
        public float CloseEnoughMultiplier
        {
            get { return _closeEnoughMultiplier; }
            set { _closeEnoughMultiplier = Math.Max(value, 1); }
        }
        private float _closeEnough = 50;
        private readonly float minDistance = 50;
        private readonly float seekGapsInterval = 1;
        private float seekGapsTimer;
        private bool cantFindDivingGear;

        /// <summary>
        /// Display units
        /// </summary>
        public float CloseEnough
        {
            get
            {
                if (IsFollowOrder && Target is Character targetCharacter && (targetCharacter.CurrentHull == null) != (character.CurrentHull == null))
                {
                    // Keep close when the target is going inside/outside
                    return minDistance;
                }
                float dist = _closeEnough * CloseEnoughMultiplier;
                float extraMultiplier = Math.Clamp(CloseEnoughMultiplier * 0.6f, 1, 3);
                if (character.AnimController.InWater)
                {
                    dist += ExtraDistanceWhileSwimming * extraMultiplier;
                }
                if (character.CurrentHull == null)
                {
                    dist += ExtraDistanceOutsideSub * extraMultiplier;
                }
                return dist;
            }
            set
            {
                _closeEnough = Math.Max(minDistance, value);
            }
        }
        public bool IgnoreIfTargetDead { get; set; }
        public bool AllowGoingOutside { get; set; }

        public bool FaceTargetOnCompleted { get; set; } = true;

        public bool AlwaysUseEuclideanDistance { get; set; } = true;

        /// <summary>
        /// If true, the distance to the destination is calculated from the character's AimSourcePos (= shoulder) instead of the collider's position
        /// </summary>
        public bool UseDistanceRelativeToAimSourcePos { get; set; } = false;

        public override bool AbandonWhenCannotCompleteSubObjectives => false;

        protected override bool AllowOutsideSubmarine => AllowGoingOutside;
        protected override bool AllowInAnySub => true;

        /// <summary>
        /// NPC line for when the NPC fails to find a path to a target. 
        /// Note that this line includes the tag [name], which needs to be replaced with the name of the target.
        /// </summary>
        public static readonly Identifier DialogCannotReachTarget = "dialogcannotreachtarget".ToIdentifier();
        /// <summary>
        /// Generic NPC line for when the NPC fails to find a path to some place/target.
        /// </summary>
        public static readonly Identifier DialogCannotReachPlace = "dialogcannotreachplace".ToIdentifier();
        /// <summary>
        /// NPC line for when the NPC fails to find a path to a patient they're trying to treat. 
        /// Note that this line includes the tag [name], which needs to be replaced with the name of the target.
        /// </summary>
        public static readonly Identifier DialogCannotReachPatient = "dialogcannotreachpatient".ToIdentifier();
        /// <summary>
        /// NPC line for when the NPC fails to find a path to a fire they're trying to extinguish.
        /// Note that this line includes the tag [name], which needs to be replaced with the name of the room the NPC is trying to get to.
        /// </summary>
        public static readonly Identifier DialogCannotReachFire = "dialogcannotreachfire".ToIdentifier();
        /// <summary>
        /// NPC line for when the NPC fails to find a path to a leak they're trying to fix.
        /// Note that this line includes the tag [name], which needs to be replaced with the name of the room the NPC is trying to get to.
        /// </summary>
        public static readonly Identifier DialogCannotReachLeak = "dialogcannotreachleak".ToIdentifier();

        public Identifier DialogueIdentifier { get; set; } = DialogCannotReachPlace;
        private readonly Identifier ExoSuitRefuel = "dialog.exosuit.refuel".ToIdentifier();
        private readonly Identifier ExoSuitOutOfFuel = "dialog.exosuit.outoffuel".ToIdentifier();
            
        public LocalizedString TargetName { get; set; }

        public ISpatialEntity Target { get; private set; }

        public float? OverridePriority = null;

        public Func<bool> SpeakCannotReachCondition { get; set; }

        protected override float GetPriority()
        {
            bool isOrder = objectiveManager.IsOrder(this);
            if (!IsAllowed)
            {
                Priority = 0;
                Abandon = !isOrder;
                return Priority;
            }
            if (Target is null or Entity { Removed: true })
            {
                Priority = 0;
                Abandon = !isOrder;
            }
            if (IgnoreIfTargetDead && Target is Character { IsDead: true })
            {
                Priority = 0;
                Abandon = !isOrder;
            }
            else
            {
                if (PriorityGetter != null)
                {
                    Priority = PriorityGetter();
                }
                else if (OverridePriority.HasValue)
                {
                    Priority = OverridePriority.Value;
                }
                else
                {
                    Priority = isOrder ? objectiveManager.GetOrderPriority(this) : 10;
                }
            }
            return Priority;
        }

        private readonly float avoidLookAheadDistance = 5;
        private readonly float pathWaitingTime = 3;

        public AIObjectiveGoTo(ISpatialEntity target, Character character, AIObjectiveManager objectiveManager, bool repeat = false, bool getDivingGearIfNeeded = true, float priorityModifier = 1, float closeEnough = 0)
            : base(character, objectiveManager, priorityModifier)
        {
            Target = target;
            this.repeat = repeat;
            waitUntilPathUnreachable = pathWaitingTime;
            this.getDivingGearIfNeeded = getDivingGearIfNeeded;
            if (Target is Item i)
            {
                CloseEnough = Math.Max(CloseEnough, i.InteractDistance + Math.Max(i.Rect.Width, i.Rect.Height) / 2);
            }
            else if (Target is Character)
            {
                //if closeEnough value is given, allow setting CloseEnough as low as 50, otherwise above AIObjectiveGetItem.DefaultReach
                CloseEnough = Math.Max(closeEnough, MathUtils.NearlyEqual(closeEnough, 0.0f) ? AIObjectiveGetItem.DefaultReach : minDistance);
            }
            else
            {
                CloseEnough = closeEnough;
            }
        }

        private void SpeakCannotReach()
        {
#if DEBUG
            if (DebugLogWhenFails)
            {
                DebugConsole.NewMessage($"{character.Name}: Cannot reach the target: {Target}", Color.Yellow);
            }
#endif
            if (!character.IsOnPlayerTeam) { return; }
            if (objectiveManager.CurrentOrder != objectiveManager.CurrentObjective) { return; }
            if (DialogueIdentifier == null) { return; }
            if (!SpeakIfFails) { return; }
            if (SpeakCannotReachCondition != null && !SpeakCannotReachCondition()) { return; }

            if (TargetName == null && DialogueIdentifier == DialogCannotReachTarget)
            {
#if DEBUG
                DebugConsole.ThrowError(
                    $"Error in {nameof(SpeakCannotReach)}: "+
                    $"attempted to use a dialog line that mentions the target (dialogue identifier: {DialogueIdentifier}), but the name of the target ({(Target?.ToString() ?? "null")}) isn't set.");
#endif
                DialogueIdentifier = DialogCannotReachPlace;
            }

            LocalizedString msg = TargetName == null ?
                TextManager.Get(DialogueIdentifier) :
                TextManager.GetWithVariable(DialogueIdentifier, "[name]".ToIdentifier(), TargetName, formatCapitals: Target is Character ? FormatCapitals.No : FormatCapitals.Yes);
            if (msg.IsNullOrEmpty() || !msg.Loaded) { return; }
            character.Speak(msg.Value, identifier: DialogueIdentifier, minDurationBetweenSimilar: 20.0f);
        }

        public void ForceAct(float deltaTime) => Act(deltaTime);

        protected override void Act(float deltaTime)
        {
            if (Target == null)
            {
                Abandon = true;
                return;
            }
            if (checkExoSuitTimer <= 0)
            {
                checkExoSuitTimer = CheckExoSuitTime * Rand.Range(0.9f, 1.1f);
                if (character.GetEquippedItem(Tags.PoweredDivingSuit, InvSlotType.OuterClothes) is { OwnInventory: Inventory exoSuitInventory } exoSuit &&
                    exoSuit.GetComponent<Powered>() is not { HasPower: true })
                {
                    if (HumanAIController.HasItem(character, Tags.DivingSuitFuel, out IEnumerable<Item> fuelRods, conditionPercentage: 1, recursive: true))
                    {
                        // Try to switch the fuel sources
                        if (character.IsOnPlayerTeam)
                        {
                            character.Speak(TextManager.Get(ExoSuitRefuel).Value, minDurationBetweenSimilar: 10f, identifier: ExoSuitRefuel);
                        }
                        // Have to copy the list, because it's modified when we unequip the item.
                        foreach (Item containedItem in exoSuit.ContainedItems.ToList())
                        {
                            if (containedItem.HasTag(Tags.DivingSuitFuel) && containedItem.Condition <= 0)
                            {
                                character.Unequip(containedItem);
                            }
                        }
                        // Refuel
                        // The information about the target slot is defined in a status effect. We could parse it, but let's keep it simple and just presume that the target slot is the second slot, as it the case with the vanilla exosuits.
                        const int targetSlot = 1;
                        Item fuelRod = fuelRods.MaxBy(b => b.Condition);
                        exoSuitInventory.TryPutItem(fuelRod, targetSlot, allowSwapping: true, allowCombine: true, user: character);
                    }
                    else if (character.IsOnPlayerTeam)
                    {
                        character.Speak(TextManager.Get(ExoSuitOutOfFuel).Value, minDurationBetweenSimilar: 30.0f, identifier: ExoSuitOutOfFuel);
                    }
                }
            }
            else
            {
                checkExoSuitTimer -= deltaTime;
            }
            if (Target == character || character.SelectedBy != null && HumanAIController.IsFriendly(character.SelectedBy))
            {
                // Wait
                character.AIController.SteeringManager.Reset();
                return;
            }
            character.SelectedItem = null;
            if (character.SelectedSecondaryItem != null && !character.SelectedSecondaryItem.IsLadder)
            {
                character.SelectedSecondaryItem = null;
            }
            if (Target is Entity e)
            {
                if (e.Removed)
                {
                    Abandon = true;
                    return;
                }
                else
                {
                    character.AIController.SelectTarget(e.AiTarget);
                }
            }
            Hull targetHull = GetTargetHull();
            if (!IsFollowOrder)
            {
                // Abandon if going through unsafe paths or targeting unsafe hulls.
                bool isUnreachable = HumanAIController.UnreachableHulls.Contains(targetHull);
                if (!objectiveManager.CurrentObjective.IgnoreUnsafeHulls)
                {
                    // Wait orders check this so that the bot temporarily leaves the unsafe hull.
                    // Non-orders (that are not set to ignore the unsafe hulls) abandon. In practice this means e.g. repair and clean up item subobjectives (of the looping parent objective).
                    // Other orders are only abandoned if the hull is unreachable, because the path is invalid or not found at all.
                    if (IsWaitOrder || !objectiveManager.HasOrders())
                    {
                        if (HumanAIController.UnsafeHulls.Contains(targetHull))
                        {
                            isUnreachable = true;
                            HumanAIController.AskToRecalculateHullSafety(targetHull);
                        }
                        else if (PathSteering?.CurrentPath != null)
                        {
                            foreach (WayPoint wp in PathSteering.CurrentPath.Nodes)
                            {
                                if (wp.CurrentHull == null) { continue; }
                                if (HumanAIController.UnsafeHulls.Contains(wp.CurrentHull))
                                {
                                    isUnreachable = true;
                                    HumanAIController.AskToRecalculateHullSafety(wp.CurrentHull);
                                }
                            }
                        }
                    }
                }
                if (isUnreachable)
                {
                    SteeringManager.Reset();
                    if (PathSteering?.CurrentPath != null)
                    {
                        PathSteering.CurrentPath.Unreachable = true;
                    }
                    if (repeat)
                    {
                        SpeakCannotReach();
                    }
                    else
                    {
                        Abandon = true;
                    }
                    return;
                }
            }
            bool insideSteering = SteeringManager == PathSteering && PathSteering.CurrentPath != null && !PathSteering.IsPathDirty;
            bool isInside = character.CurrentHull != null;
            bool hasOutdoorNodes = insideSteering && PathSteering.CurrentPath.HasOutdoorsNodes;
            if (isInside && hasOutdoorNodes && !AllowGoingOutside)
            {
                Abandon = true;
            }
            else if (HumanAIController.SteeringManager == PathSteering)
            {
                waitUntilPathUnreachable -= deltaTime;
                if (HumanAIController.IsCurrentPathNullOrUnreachable)
                {
                    SteeringManager.Reset();
                    if (waitUntilPathUnreachable < 0)
                    {
                        waitUntilPathUnreachable = pathWaitingTime;
                        if (repeat && !IsCompleted)
                        {
                            if (!IsDoneFollowing())
                            {
                                SpeakCannotReach();
                            }
                        }
                        else
                        {
                            Abandon = true;
                        }
                    }
                }
                else if (HumanAIController.HasValidPath(requireUnfinished: false))
                {
                    waitUntilPathUnreachable = pathWaitingTime;
                }
            }
            if (Abandon) { return; }

            if (!IsFindDivingGearSubObjective)
            {
                bool needsDivingSuit = (!isInside || hasOutdoorNodes) && !character.IsImmuneToPressure;
                bool tryToGetDivingGear = needsDivingSuit || HumanAIController.NeedsDivingGear(targetHull, out needsDivingSuit);
                bool tryToGetDivingSuit = needsDivingSuit;
                Character followTarget = Target as Character;
                if (Mimic && !character.IsImmuneToPressure)
                {
                    if (HumanAIController.HasDivingSuit(followTarget))
                    {
                        tryToGetDivingGear = true;
                        tryToGetDivingSuit = true;
                    }
                    else if (HumanAIController.HasDivingMask(followTarget) && character.CharacterHealth.OxygenLowResistance < 1)
                    {
                        tryToGetDivingGear = true;
                    }
                }
                bool needsEquipment = false;
                float minOxygen = AIObjectiveFindDivingGear.GetMinOxygen(character);
                if (tryToGetDivingSuit)
                {
                    needsEquipment = !HumanAIController.HasDivingSuit(character, minOxygen, requireSuitablePressureProtection: !objectiveManager.FailedToFindDivingGearForDepth);
                }
                else if (tryToGetDivingGear)
                {
                    needsEquipment = !HumanAIController.HasDivingGear(character, minOxygen);
                }
                if (!getDivingGearIfNeeded)
                {
                    if (needsEquipment)
                    {
                        // Don't try to reach the target without proper equipment.
                        Abandon = true;
                        return;
                    }
                }
                else
                {
                    if (character.LockHands)
                    {
                        cantFindDivingGear = true;
                    }
                    if (cantFindDivingGear && needsDivingSuit)
                    {
                        // Don't try to reach the target without a suit because it's lethal.
                        Abandon = true;
                        return;
                    }
                    if (needsEquipment && !cantFindDivingGear)
                    {
                        SteeringManager.Reset();
                        TryAddSubObjective(ref findDivingGear, () => new AIObjectiveFindDivingGear(character, needsDivingSuit: tryToGetDivingSuit, objectiveManager),
                            onAbandon: () =>
                            {
                                cantFindDivingGear = true;
                                if (needsDivingSuit)
                                {
                                    // Shouldn't try to reach the target without a suit, because it's lethal.
                                    Abandon = true;
                                }
                                else
                                {
                                    // Try again without requiring the diving suit (or mask)
                                    RemoveSubObjective(ref findDivingGear);
                                    TryAddSubObjective(ref findDivingGear, () => new AIObjectiveFindDivingGear(character, needsDivingSuit: !tryToGetDivingSuit, objectiveManager),
                                        onAbandon: () =>
                                        {
                                            Abandon = character.CurrentHull != null && (objectiveManager.CurrentOrder != this || Target.Submarine == null);
                                            RemoveSubObjective(ref findDivingGear);
                                        },
                                        onCompleted: () =>
                                        {
                                            RemoveSubObjective(ref findDivingGear);
                                        });
                                }
                            },
                            onCompleted: () => RemoveSubObjective(ref findDivingGear));
                        return;
                    }
                }
            }            
            if (IsDoneFollowing())
            {
                OnCompleted();
                return;
            }
            float maxGapDistance = 500;
            Character targetCharacter = Target as Character;
            if (character.AnimController.InWater)
            {
                if (character.CurrentHull == null ||
                    IsFollowOrder && 
                    targetCharacter != null && (targetCharacter.CurrentHull == null) != (character.CurrentHull == null) &&
                    Vector2.DistanceSquared(character.WorldPosition, Target.WorldPosition) < maxGapDistance * maxGapDistance)
                {
                    if (seekGapsTimer > 0)
                    {
                        seekGapsTimer -= deltaTime;
                    }
                    else
                    {
                        bool isRuins = character.Submarine?.Info.IsRuin != null || Target.Submarine?.Info.IsRuin != null;
                        bool isEitherOneInside = isInside || Target.Submarine != null;
                        if (isEitherOneInside && (!isRuins || !HumanAIController.HasValidPath()))
                        {
                            SeekGaps(maxGapDistance);
                            seekGapsTimer = seekGapsInterval * Rand.Range(0.1f, 1.1f);
                            if (TargetGap != null)
                            {
                                // Check that nothing is blocking the way
                                Vector2 rayStart = character.SimPosition;
                                Vector2 rayEnd = TargetGap.SimPosition;
                                if (TargetGap.Submarine != null && character.Submarine == null)
                                {
                                    rayStart -= TargetGap.Submarine.SimPosition;
                                }
                                else if (TargetGap.Submarine == null && character.Submarine != null)
                                {
                                    rayEnd -= character.Submarine.SimPosition;
                                }
                                var closestBody = Submarine.CheckVisibility(rayStart, rayEnd, ignoreSubs: true);
                                if (closestBody != null)
                                {
                                    TargetGap = null;
                                }
                            }
                        }
                        else
                        {
                            TargetGap = null;
                        }
                    }
                }
                else
                {
                    TargetGap = null;
                }
                if (TargetGap != null)
                {
                    if (TargetGap.FlowTargetHull != null && HumanAIController.SteerThroughGap(TargetGap, IsFollowOrder ? Target.WorldPosition : TargetGap.FlowTargetHull.WorldPosition, deltaTime))
                    {
                        SteeringManager.SteeringAvoid(deltaTime, avoidLookAheadDistance, weight: 1);
                        return;
                    }
                    else
                    {
                        TargetGap = null;
                    }
                }
                if (checkScooterTimer <= 0)
                {
                    useScooter = false;
                    checkScooterTimer = CheckScooterTime * Rand.Range(0.9f, 1.1f);
                    Item scooter = null;
                    bool shouldUseScooter = Mimic && targetCharacter != null && targetCharacter.HasEquippedItem(Tags.Scooter, allowBroken: false);
                    if (!shouldUseScooter)
                    {
                        float threshold = 500;
                        if (isInside)
                        {
                            Vector2 diff = Target.WorldPosition - character.WorldPosition;
                            shouldUseScooter = Math.Abs(diff.X) > threshold || Math.Abs(diff.Y) > 150;
                        }
                        else
                        {
                            shouldUseScooter = Vector2.DistanceSquared(character.WorldPosition, Target.WorldPosition) > threshold * threshold;
                        }
                    }
                    if (HumanAIController.HasItem(character, Tags.Scooter, out IEnumerable<Item> equippedScooters, recursive: false, requireEquipped: true))
                    {
                        // Currently equipped scooter
                        scooter = equippedScooters.FirstOrDefault();
                    }
                    else if (shouldUseScooter)
                    {
                        bool hasHandsFull = character.HasHandsFull(out (Item leftHandItem, Item rightHandItem) items);
                        if (hasHandsFull)
                        {
                            hasHandsFull = !character.TryPutItemInAnySlot(items.leftHandItem) && 
                                           !character.TryPutItemInAnySlot(items.rightHandItem) &&
                                           !character.TryPutItemInBag(items.leftHandItem) && 
                                           !character.TryPutItemInBag(items.rightHandItem);
                        }
                        if (!hasHandsFull)
                        {
                            bool hasBattery = false;
                            if (HumanAIController.HasItem(character, Tags.Scooter, out IEnumerable<Item> nonEquippedScootersWithBattery, containedTag: Tags.MobileBattery, conditionPercentage: 1, requireEquipped: false))
                            {
                                scooter = nonEquippedScootersWithBattery.FirstOrDefault();
                                hasBattery = true;
                            }
                            else if (HumanAIController.HasItem(character, Tags.Scooter, out IEnumerable<Item> nonEquippedScootersWithoutBattery, requireEquipped: false))
                            {
                                scooter = nonEquippedScootersWithoutBattery.FirstOrDefault();
                                // Non-recursive so that the bots won't take batteries from other items. Also means that they can't find batteries inside containers. Not sure how to solve this.
                                hasBattery = HumanAIController.HasItem(character, Tags.MobileBattery, out _, requireEquipped: false, conditionPercentage: 1, recursive: false);
                            }
                            if (scooter != null && hasBattery)
                            {
                                // Equip only if we have a battery available
                                HumanAIController.TakeItem(scooter, character.Inventory, equip: true, dropOtherIfCannotMove: false, allowSwapping: true, storeUnequipped: false);
                            }
                        }
                    }
                    if (scooter != null && character.HasEquippedItem(scooter))
                    {
                        if (shouldUseScooter)
                        {
                            useScooter = true;
                            // Check the battery
                            if (scooter.ContainedItems.None(i => i.Condition > 0))
                            {
                                // Try to switch batteries
                                if (HumanAIController.HasItem(character, Tags.MobileBattery, out IEnumerable<Item> batteries, conditionPercentage: 1, recursive: false))
                                {
                                    scooter.ContainedItems.ForEachMod(emptyBattery => character.Inventory.TryPutItem(emptyBattery, character, CharacterInventory.AnySlot));
                                    if (!scooter.Combine(batteries.OrderByDescending(b => b.Condition).First(), character))
                                    {
                                        useScooter = false;
                                    }
                                }
                                else
                                {
                                    useScooter = false;
                                }
                            }
                        }
                        if (!useScooter)
                        {
                            character.TryPutItemInAnySlot(scooter);
                        }
                    }
                }
                else
                {
                    checkScooterTimer -= deltaTime;
                }
            }
            else
            {
                TargetGap = null;
                useScooter = false;
                checkScooterTimer = 0;
            }
            if (SteeringManager == PathSteering)
            {
                Vector2 targetPos = character.GetRelativeSimPosition(Target);
                Func<PathNode, bool> nodeFilter = null;
                if (isInside && !AllowGoingOutside)
                {
                    nodeFilter = n => n.Waypoint.CurrentHull != null;
                }
                else if (!isInside)
                {
                    if (HumanAIController.UseOutsideWaypoints)
                    {
                        nodeFilter = n => n.Waypoint.Submarine == null;
                    }
                    else
                    {
                        nodeFilter = n => n.Waypoint.Submarine != null || n.Waypoint.Ruin != null;
                    }         
                }
                if (!isInside && !UsePathingOutside)
                {
                    character.ReleaseSecondaryItem();
                    PathSteering.SteeringSeekSimple(character.GetRelativeSimPosition(Target), 10);
                    if (character.AnimController.InWater)
                    {
                        SteeringManager.SteeringAvoid(deltaTime, avoidLookAheadDistance, weight: 15);
                    }
                }
                else
                {
                    PathSteering.SteeringSeek(targetPos, weight: 1,
                        startNodeFilter: n => (n.Waypoint.CurrentHull == null) == (character.CurrentHull == null),
                        endNodeFilter: endNodeFilter,
                        nodeFilter: nodeFilter,
                        checkVisiblity: Target is Item || Target is Character);
                }
                if (!isInside && (PathSteering.CurrentPath == null || PathSteering.IsPathDirty || PathSteering.CurrentPath.Unreachable))
                {
                    if (useScooter)
                    {
                        UseScooter(Target.WorldPosition);
                    }
                    else
                    {
                        character.ReleaseSecondaryItem();
                        SteeringManager.SteeringManual(deltaTime, Vector2.Normalize(Target.WorldPosition - character.WorldPosition));
                        if (character.AnimController.InWater)
                        {
                            SteeringManager.SteeringAvoid(deltaTime, avoidLookAheadDistance, weight: 2);
                        }
                    }
                }
                else if (useScooter && PathSteering.CurrentPath?.CurrentNode != null)
                {
                    UseScooter(PathSteering.CurrentPath.CurrentNode.WorldPosition);
                }
            }
            else
            {
                if (useScooter)
                {
                    UseScooter(Target.WorldPosition);
                }
                else
                {
                    character.ReleaseSecondaryItem();
                    SteeringManager.SteeringSeek(character.GetRelativeSimPosition(Target), 10);
                    if (character.AnimController.InWater)
                    {
                        SteeringManager.SteeringAvoid(deltaTime, avoidLookAheadDistance, weight: 15);
                    }
                }
            }

            void UseScooter(Vector2 targetWorldPos)
            {
                if (!character.HasEquippedItem("scooter".ToIdentifier())) { return; }
                SteeringManager.Reset();
                character.ReleaseSecondaryItem();
                character.CursorPosition = targetWorldPos;
                if (character.Submarine != null)
                {
                    character.CursorPosition -= character.Submarine.Position;
                }
                Vector2 diff = character.CursorPosition - character.Position;
                Vector2 dir = Vector2.Normalize(diff);
                if (character.CurrentHull == null && IsFollowOrder)
                {
                    float sqrDist = diff.LengthSquared();
                    if (sqrDist > MathUtils.Pow2(CloseEnough * 1.5f))
                    {
                        SteeringManager.SteeringManual(1.0f, dir);
                    }
                    else
                    {
                        float dot = Vector2.Dot(dir, VectorExtensions.Forward(character.AnimController.Collider.Rotation + MathHelper.PiOver2));
                        bool isFacing = dot > 0.9f;
                        if (!isFacing && sqrDist > MathUtils.Pow2(CloseEnough))
                        {
                            SteeringManager.SteeringManual(1.0f, dir);
                        }
                    }
                }
                else
                {
                    SteeringManager.SteeringManual(1.0f, dir);
                }
                character.SetInput(InputType.Aim, false, true);
                character.SetInput(InputType.Shoot, false, true);
            }
            
            bool IsDoneFollowing()
            {
                if (repeat && IsCloseEnough)
                {
                    if (requiredCondition == null || requiredCondition())
                    {
                        if (character.CanSeeTarget(Target) && (!character.IsClimbing || IsFollowOrder))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        }

        private bool useScooter;
        private float checkScooterTimer;
        private const float CheckScooterTime = 0.5f;
        
        private float checkExoSuitTimer;
        private const float CheckExoSuitTime = 2.0f;

        public Hull GetTargetHull() => GetTargetHull(Target);

        public static Hull GetTargetHull(ISpatialEntity target)
        {
            if (target is Hull h)
            {
                return h;
            }
            else if (target is Item i)
            {
                return i.CurrentHull;
            }
            else if (target is Character c)
            {
                return c.CurrentHull ?? c.AnimController.CurrentHull;
            }
            else if (target is Structure structure)
            {
                return Hull.FindHull(structure.Position, useWorldCoordinates: false);
            }
            else if (target is Gap g)
            {
                return g.FlowTargetHull;
            }
            else if (target is WayPoint wp)
            {
                return wp.CurrentHull;
            }
            else if (target is FireSource fs)
            {
                return fs.Hull;
            }
            else if (target is OrderTarget ot)
            {
                return ot.Hull;
            }
            return null;
        }

        public Gap TargetGap { get; private set; }
        private void SeekGaps(float maxDistance)
        {
            Gap selectedGap = null;
            float selectedDistance = -1;
            Vector2 toTargetNormalized = Vector2.Normalize(Target.WorldPosition - character.WorldPosition);
            foreach (Gap gap in Gap.GapList)
            {
                if (gap.Open < 1) { continue; }
                if (gap.Submarine == null) { continue; }
                if (!IsFollowOrder)
                {
                    if (gap.FlowTargetHull == null) { continue; }
                    if (gap.Submarine != Target.Submarine) { continue; }
                }
                Vector2 toGap = gap.WorldPosition - character.WorldPosition;
                if (Vector2.Dot(Vector2.Normalize(toGap), toTargetNormalized) < 0) { continue; }
                float squaredDistance = toGap.LengthSquared();
                if (squaredDistance > maxDistance * maxDistance) { continue; }
                if (selectedGap == null || squaredDistance < selectedDistance)
                {
                    selectedGap = gap;
                    selectedDistance = squaredDistance;
                }
            }
            TargetGap = selectedGap;
        }

        public bool IsCloseEnough
        {
            get
            {
                if (character.IsClimbing)
                {
                    if (SteeringManager == PathSteering && PathSteering.CurrentPath != null && !PathSteering.CurrentPath.Finished && PathSteering.IsCurrentNodeLadder && !PathSteering.CurrentPath.IsAtEndNode)
                    {
                        if (Target.WorldPosition.Y > character.WorldPosition.Y)
                        {
                            // The target is still above us
                            return false;
                        }
                        if (!character.AnimController.IsAboveFloor)
                        {
                            // Going through a hatch
                            return false;
                        }
                        if (Target is Item targetItem && targetItem.GetComponent<Pickable>() == null)
                        {
                            // Targeting a static item, such as a reactor or a controller -> Don't complete, until we are no longer climbing.
                            return false;
                        }
                    }
                }
                if (!AlwaysUseEuclideanDistance && !character.AnimController.InWater)
                {
                    float yDist = Math.Abs(Target.WorldPosition.Y - character.WorldPosition.Y);
                    if (yDist > CloseEnough) { return false; }
                    float xDist = Math.Abs(Target.WorldPosition.X - character.WorldPosition.X);
                    return xDist <= CloseEnough;
                }
                Vector2 sourcePos = UseDistanceRelativeToAimSourcePos ? character.AnimController.AimSourceWorldPos : character.WorldPosition;
                return Vector2.DistanceSquared(Target.WorldPosition, sourcePos) < CloseEnough * CloseEnough;
            }
        }

        protected override bool CheckObjectiveState()
        {
            // First check the distance and then if can interact (heaviest)
            if (Target == null)
            {
                Abandon = true;
                return false;
            }
            if (repeat)
            {
                return false;
            }
            else
            {
                if (IsCloseEnough)
                {
                    if (requiredCondition == null || requiredCondition())
                    {
                        if (Target is Item item)
                        {
                            if (character.CanInteractWith(item, out _, checkLinked: false)) { IsCompleted = true; }
                        }
                        else if (Target is Character targetCharacter)
                        {
                            character.SelectCharacter(targetCharacter);
                            if (character.CanInteractWith(targetCharacter, skipDistanceCheck: true)) { IsCompleted = true; }
                            character.DeselectCharacter();
                        }
                        else
                        {
                            IsCompleted = true;
                        }
                    }
                }
            }
            return IsCompleted;
        }

        protected override void OnAbandon()
        {
            StopMovement();
            if (SteeringManager == PathSteering)
            {
                PathSteering.ResetPath();
            }
            SpeakCannotReach();
            base.OnAbandon();
        }

        private void StopMovement()
        {
            SteeringManager?.Reset();
            if (FaceTargetOnCompleted && Target is Entity { Removed: false })
            {
                HumanAIController.FaceTarget(Target);
            }
        }

        protected override void OnCompleted()
        {
            StopMovement();
            if (Target is WayPoint { Ladders: null })
            {
                // Release ladders when ordered to wait at a spawnpoint.
                // This is a special case specifically meant for NPCs that spawn in outposts with a wait order.
                // Otherwise they might keep holding to the ladders when the target is just next to it.
                if (character.IsClimbing && character.AnimController.IsAboveFloor)
                {
                    character.StopClimbing();
                }
            }
            base.OnCompleted();
        }

        public override void Reset()
        {
            base.Reset();
            findDivingGear = null;
            seekGapsTimer = 0;
            TargetGap = null;
            if (SteeringManager is IndoorsSteeringManager pathSteering)
            {
                pathSteering.ResetPath();
            }
        }
        
        public bool ShouldRun(bool run)
        {
            if (run && objectiveManager.ForcedOrder == this && IsWaitOrder && !character.IsOnPlayerTeam)
            {
                // NPCs with a wait order don't run.
                run = false;
            }
            else if (Target != null)
            {
                if (character.CurrentHull == null)
                {
                    run = Vector2.DistanceSquared(character.WorldPosition, Target.WorldPosition) > 300 * 300;
                }
                else
                {
                    float yDiff = Target.WorldPosition.Y - character.WorldPosition.Y;
                    if (Math.Abs(yDiff) > 100)
                    {
                        run = true;
                    }
                    else
                    {
                        float xDiff = Target.WorldPosition.X - character.WorldPosition.X;
                        run = Math.Abs(xDiff) > 500;
                    }
                }
            }
            return run;
        }
    }
}
