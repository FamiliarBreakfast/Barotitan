﻿using Barotrauma.Abilities;
using Barotrauma.Extensions;
using FarseerPhysics;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma.Items.Components
{
    partial class ItemContainer : ItemComponent, IDrawableComponent
    {
        readonly record struct ActiveContainedItem(Item Item, StatusEffect StatusEffect, bool ExcludeBroken, bool ExcludeFullCondition, bool BlameEquipperForDeath);

        readonly record struct ContainedItem(Item Item, bool Hide, Vector2? ItemPos, float Rotation);

        class SlotRestrictions
        {
            public int MaxStackSize;
            public List<RelatedItem> ContainableItems;
            public readonly bool AutoInject;

            public SlotRestrictions(int maxStackSize, List<RelatedItem> containableItems, bool autoInject)
            {
                MaxStackSize = maxStackSize;
                ContainableItems = containableItems;
                AutoInject = autoInject;
            }

            public bool MatchesItem(Item item)
            {
                return ContainableItems == null || ContainableItems.Count == 0 || ContainableItems.Any(c => c.MatchesItem(item));
            }

            public bool MatchesItem(ItemPrefab itemPrefab)
            {
                return ContainableItems == null || ContainableItems.Count == 0 || ContainableItems.Any(c => c.MatchesItem(itemPrefab));
            }

            public bool MatchesItem(Identifier identifierOrTag)
            {
                return 
                    ContainableItems == null || ContainableItems.Count == 0 || 
                    ContainableItems.Any(c => c.Identifiers.Contains(identifierOrTag) && !c.ExcludedIdentifiers.Contains(identifierOrTag));
            }
        }

        public readonly NamedEvent<ItemContainer> OnContainedItemsChanged = new NamedEvent<ItemContainer>();

        private bool alwaysContainedItemsSpawned;

        public readonly ItemInventory Inventory;

        private readonly List<ActiveContainedItem> activeContainedItems = new List<ActiveContainedItem>();

        private readonly List<ContainedItem> containedItems = new List<ContainedItem>();

        private List<ushort>[] itemIds;

        //how many items can be contained
        private int capacity;
        [Serialize(5, IsPropertySaveable.No, description: "How many items can be contained inside this item.")]
        public int Capacity
        {
            get { return capacity; }
            private set
            {
                capacity = Math.Max(value, 0);
                MainContainerCapacity = value;
            }
        }
        /// <summary>
        /// The capacity of the main container without taking the sub containers into account. Only differs when there's a sub container defined for the component.
        /// </summary>
        public int MainContainerCapacity { get; private set; }

        //how many items can be contained
        private int maxStackSize;
        [Serialize(64, IsPropertySaveable.No, description: "How many items can be stacked in one slot. Does not increase the maximum stack size of the items themselves, e.g. a stack of bullets could have a maximum size of 8 but the number of bullets in a specific weapon could be restricted to 6.")]
        public int MaxStackSize
        {
            get { return maxStackSize; }
            set { maxStackSize = Math.Max(value, 1); }
        }

        private bool hideItems;
        [Serialize(true, IsPropertySaveable.No, description: "Should the items contained inside this item be hidden."
            + " If set to false, you should use the ItemPos and ItemInterval properties to determine where the items get rendered.")]
        public bool HideItems
        {
            get { return hideItems; }
            set
            {
                hideItems = value;
                Drawable = !hideItems;
            }
        }
        
        [Serialize("0.0,0.0", IsPropertySaveable.No, description: "The position where the contained items get drawn at (offset from the upper left corner of the sprite in pixels).")]
        public Vector2 ItemPos { get; set; }

        [Serialize("0.0,0.0", IsPropertySaveable.No, description: "The interval at which the contained items are spaced apart from each other (in pixels).")]
        public Vector2 ItemInterval { get; set; }

        [Serialize(100, IsPropertySaveable.No, description: "How many items are placed in a row before starting a new row.")]
        public int ItemsPerRow { get; set; }

        [Serialize(false, IsPropertySaveable.No, description: "Should items be drawn based on their position within the inventory?")]
        public bool ItemsUseInventoryPlacement { get; set; }

        [Serialize(true, IsPropertySaveable.No, description: "Should the inventory of this item be visible when the item is selected. Note that this does not prevent dragging and dropping items to the item.")]
        public bool DrawInventory
        {
            get;
            set;
        }

        [Serialize(true, IsPropertySaveable.No, "Allow dragging and dropping items to deposit items into this inventory.")]
        public bool AllowDragAndDrop
        {
            get;
            set;
        }
        
        [Serialize(true, IsPropertySaveable.No)]
        public bool AllowSwappingContainedItems
        {
            get;
            set;
        }

        [Serialize(true, IsPropertySaveable.Yes, description: "When this item is equipped, and you 'quick use' (double click / equip button) another equippable item, should the game attempt to move that item inside this one?")]
        public bool QuickUseMovesItemsInside { get; set; }

        [Serialize(false, IsPropertySaveable.No, description: "If set to true, interacting with this item will make the character interact with the contained item(s), automatically picking them up if they can be picked up.")]
        public bool AutoInteractWithContained
        {
            get;
            set;
        }

        private ImmutableHashSet<Identifier> autoInteractWithContainedTags = ImmutableHashSet<Identifier>.Empty;
        [Serialize("", IsPropertySaveable.Yes, description: $"Interacting with this container will autointeract with contained items that have one of these tags. Only valid if {nameof(AutoInteractWithContained)} is set to true.")]
        public string AutoInteractWithContainedTags
        {
            get { return autoInteractWithContainedTags.ConvertToString(); }
            set
            {
                autoInteractWithContainedTags = value.ToIdentifiers().ToImmutableHashSet();
            }
        }

        [Serialize(true, IsPropertySaveable.No, description: "Is the container accessible in general.")]
        public bool AllowAccess { get; set; }

        [Serialize(false, IsPropertySaveable.No, description: "Is the container only accessible when it's broken. Doesn't apply to editors.")]
        public bool AccessOnlyWhenBroken { get; set; }

        [Serialize(true, IsPropertySaveable.No, description: "Is the container accessible when dropped.")]
        public bool AllowAccessWhenDropped { get; set; }

        [Serialize(5, IsPropertySaveable.No, description: "How many inventory slots the inventory has per row.")]
        public int SlotsPerRow { get; set; }

        private readonly HashSet<Identifier> containableRestrictions = new HashSet<Identifier>();
        [Editable, Serialize("", IsPropertySaveable.Yes, description: "Define items (by identifiers or tags) that bots should place inside this container. If empty, no restrictions are applied.")]
        public string ContainableRestrictions
        {
            get { return string.Join(",", containableRestrictions); }
            set
            {
                containableRestrictions.Clear();
                if (!value.IsNullOrEmpty())
                {
                    foreach (var str in value.Split(','))
                    {
                        if (str.IsNullOrWhiteSpace()) { continue; }
                        containableRestrictions.Add(str.ToIdentifier());
                    }
                }
            }
        }

        [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Should this container be automatically filled with items?")]
        public bool AutoFill { get; set; }

        private float itemRotation;
        [Serialize(0.0f, IsPropertySaveable.No, description: "The rotation in which the contained sprites are drawn (in degrees).")]
        public float ItemRotation
        {
            get { return MathHelper.ToDegrees(itemRotation); }
            set { itemRotation = MathHelper.ToRadians(value); }
        }

        [Serialize("", IsPropertySaveable.No, description: "Specify an item for the container to spawn with.")]
        public string SpawnWithId
        {
            get;
            set;
        }

        [Serialize(false, IsPropertySaveable.No, description: "Should the items configured using SpawnWithId spawn if this item is broken.")]
        public bool SpawnWithIdWhenBroken
        {
            get;
            set;
        }
        
        [Serialize(false, IsPropertySaveable.No, description: "Should the items be injected into the user.")]
        public bool AutoInject
        {
            get;
            set;
        }

        [Serialize(0.5f, IsPropertySaveable.No, description: "The health threshold that the user must reach in order to activate the autoinjection.")]
        public float AutoInjectThreshold
        {
            get;
            set;
        }

        [Serialize(false, IsPropertySaveable.No)]
        public bool RemoveContainedItemsOnDeconstruct { get; set; }

        /// <summary>
        /// Can be used by status effects to lock the inventory
        /// </summary>
        public bool Locked
        {
            get { return Inventory.Locked; }
            set { Inventory.Locked = value; }
        }

        /// <summary>
        /// Can be used by status effects
        /// </summary>
        public int ContainedItemCount
        {
            get => Inventory.AllItems.Count();
        }

        /// <summary>
        /// Can be used by status effects
        /// </summary>
        public int ContainedNonBrokenItemCount
        {
            get => Inventory.AllItems.Count(it => it.Condition > 0.0f);
        }

        public int ExtraStackSize
        {
            get => Inventory.ExtraStackSize;
            set => Inventory.ExtraStackSize = value;
        }

        private readonly ImmutableArray<SlotRestrictions> slotRestrictions;

        readonly List<ISerializableEntity> targets = new List<ISerializableEntity>();

        private float prevContainedItemRefreshRotation;
        private Vector2 prevContainedItemRefreshPosition;

        private float autoInjectCooldown = 1.0f;
        const float AutoInjectInterval = 1.0f;

        private bool subContainersCanAutoInject;


        public bool ShouldBeContained(string[] identifiersOrTags, out bool isRestrictionsDefined)
        {
            isRestrictionsDefined = containableRestrictions.Any();
            if (slotRestrictions.None(s => s.MatchesItem(item))) { return false; }
            if (!isRestrictionsDefined) { return true; }
            return identifiersOrTags.Any(id => containableRestrictions.Any(r => r == id));
        }

        public bool ShouldBeContained(Item item, out bool isRestrictionsDefined)
        {
            isRestrictionsDefined = containableRestrictions.Any();
            if (slotRestrictions.None(s => s.MatchesItem(item))) { return false; }
            if (!isRestrictionsDefined) { return true; }
            return containableRestrictions.Any(id => item.Prefab.Identifier == id || item.HasTag(id));
        }
        
        private ImmutableHashSet<Identifier> containableItemIdentifiers;
        public ImmutableHashSet<Identifier> ContainableItemIdentifiers => containableItemIdentifiers;

        public List<RelatedItem> ContainableItems { get; }
        public List<RelatedItem> AllSubContainableItems { get; }

        public readonly bool HasSubContainers;

        public bool hasSignalConnections;

        private string totalConditionValueString = "", totalConditionPercentageString = "", totalItemsString = "";
        private float prevTotalConditionValue = 0, prevTotalConditionPercentage = 0; int prevTotalItems = 0;

        public ItemContainer(Item item, ContentXElement element)
            : base(item, element)
        {
            int totalCapacity = capacity;

            foreach (var subElement in element.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "containable":
                        RelatedItem containable = RelatedItem.Load(subElement, returnEmpty: false, parentDebugName: item.Name);
                        if (containable == null)
                        {
                            DebugConsole.ThrowError("Error in item config \"" + item.ConfigFilePath + "\" - containable with no identifiers.",
                                contentPackage: element.ContentPackage);
                            continue;
                        }
                        ContainableItems ??= new List<RelatedItem>();
                        ContainableItems.Add(containable);
                        break;
                    case "subcontainer":
                        totalCapacity += subElement.GetAttributeInt("capacity", 1);
                        HasSubContainers = true;
                        break;
                }
            }
            Inventory = new ItemInventory(item, this, totalCapacity, SlotsPerRow);

            // we have to assign this here because the fields are serialized before the inventory is created otherwise
            ExtraStackSize = element.GetAttributeInt(nameof(ExtraStackSize), 0);

            List<SlotRestrictions> newSlotRestrictions = new List<SlotRestrictions>(totalCapacity);
            for (int i = 0; i < capacity; i++)
            {
                newSlotRestrictions.Add(new SlotRestrictions(maxStackSize, ContainableItems, autoInject: false));
            }

            int subContainerIndex = capacity;
            foreach (var subElement in element.Elements())
            {
                if (subElement.Name.ToString().ToLowerInvariant() != "subcontainer") { continue; }
       
                int subCapacity = subElement.GetAttributeInt("capacity", 1);
                int subMaxStackSize = subElement.GetAttributeInt("maxstacksize", maxStackSize);
                bool autoInject = subElement.GetAttributeBool("autoinject", false);

                subContainersCanAutoInject |= autoInject;

                var subContainableItems = new List<RelatedItem>();
                foreach (var subSubElement in subElement.Elements())
                {
                    if (subSubElement.Name.ToString().ToLowerInvariant() != "containable") { continue; }

                    RelatedItem containable = RelatedItem.Load(subSubElement, returnEmpty: false, parentDebugName: item.Name);
                    if (containable == null)
                    {
                        DebugConsole.ThrowError("Error in item config \"" + item.ConfigFilePath + "\" - containable with no identifiers.",
                            contentPackage: element.ContentPackage);
                        continue;
                    }
                    subContainableItems.Add(containable);
                    AllSubContainableItems ??= new List<RelatedItem>();
                    AllSubContainableItems.Add(containable);
                }

                for (int i = subContainerIndex; i < subContainerIndex + subCapacity; i++)
                {
                    newSlotRestrictions.Add(new SlotRestrictions(subMaxStackSize, subContainableItems, autoInject));
                }
                subContainerIndex += subCapacity;
            }
            capacity = totalCapacity;
            slotRestrictions = newSlotRestrictions.ToImmutableArray();
            System.Diagnostics.Debug.Assert(totalCapacity == slotRestrictions.Length);
            InitProjSpecific(element);
        }

        public void ReloadContainableRestrictions(ContentXElement element)
        {
            int containableIndex = 0;
            foreach (var subElement in element.GetChildElements("containable"))
            {
                RelatedItem containable = RelatedItem.Load(subElement, returnEmpty: false, parentDebugName: item.Name);
                if (containable == null)
                {
                    DebugConsole.ThrowError("Error when loading containable restrictions for \"" + item.Name + "\" - containable with no identifiers.",
                        contentPackage: element.ContentPackage);
                    continue;
                }
                ContainableItems[containableIndex] = containable;
                containableIndex++;
                if (containableIndex >= ContainableItems.Count) { break; }            
            }
            for (int i = 0; i < capacity; i++)
            {
                slotRestrictions[i].ContainableItems = ContainableItems;
            }
#if CLIENT
            if (element.GetChildElement("clearsubcontainerrestrictions") != null)
            {
                for (int i = capacity - MainContainerCapacity; i < capacity; i++)
                {
                    slotRestrictions[i].MaxStackSize = MaxStackSize;
                    slotIcons[i] = null;
                }
            }
#endif
        }

        public int GetMaxStackSize(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= capacity)
            {
                return 0;
            }
            return slotRestrictions[slotIndex].MaxStackSize;
        }

        partial void InitProjSpecific(ContentXElement element);

        public void OnItemContained(Item containedItem)
        {
            int index = Inventory.FindIndex(containedItem);
            RelatedItem relatedItem = null;
            if (index >= 0 && index < slotRestrictions.Length)
            {
                if (slotRestrictions[index].ContainableItems != null)
                {
                    activeContainedItems.RemoveAll(i => i.Item == containedItem);
                    foreach (var containableItem in slotRestrictions[index].ContainableItems)
                    {
                        if (!containableItem.MatchesItem(containedItem)) { continue; }
                        //the 1st matching ContainableItem of the slot determines the hiding, position and rotation of the item
                        relatedItem ??= containableItem;
                        foreach (StatusEffect effect in containableItem.StatusEffects)
                        {
                            ActiveContainedItem activeContainedItem = new(containedItem, effect, containableItem.ExcludeBroken, containableItem.ExcludeFullCondition, containableItem.BlameEquipperForDeath);
                            activeContainedItems.Add(activeContainedItem);

                            if (!ShouldApplyEffects(activeContainedItem) || item.Submarine is { Loading: true} || initializingLoadedItems || 
                                containedItem.OnInsertedEffectsApplied) 
                            { 
                                continue; 
                            }
                            activeContainedItem.StatusEffect.Apply(ActionType.OnInserted, deltaTime: 1, item, targets);
                        }
                        containedItem.OnInsertedEffectsApplied = true;
                    }
                }
            }

            var containedItemInfo = new ContainedItem(containedItem,
                        Hide: relatedItem?.Hide ?? false,
                        ItemPos: relatedItem?.ItemPos,
                        Rotation: relatedItem?.Rotation ?? 0.0f);
            containedItems.RemoveAll(d => d.Item == containedItem);

            if (hideItems)
            {
                //if the items aren't visible, the draw order doesn't matter and we can skip the sorting
                containedItems.Add(containedItemInfo);
            }
            else
            {
                int containedIndex = 0;
                while (containedIndex < containedItems.Count)
                {
                    if (index <= Inventory.FindIndex(containedItems[containedIndex].Item))
                    {
                        break;
                    }
                    containedIndex++;
                }
                //sort drawables by their order in the inventory
                containedItems.Insert(containedIndex, containedItemInfo);
            }

            if (item.GetComponent<Planter>() != null)
            {
                GameAnalyticsManager.AddDesignEvent("MicroInteraction:" + (GameMain.GameSession?.GameMode?.Preset.Identifier.Value ?? "null") + ":GardeningPlanted:" + containedItem.Prefab.Identifier);
            }

            //no need to Update() if this item has no statuseffects and no physics body, and if there are no signal connections.
            IsActive = hasSignalConnections || activeContainedItems.Count > 0 || Inventory.AllItems.Any(static it => it.body != null);

            if (IsActive && item.GetRootInventoryOwner() is Character owner && 
                owner.HasEquippedItem(item, predicate: slot => slot.HasFlag(InvSlotType.LeftHand) || slot.HasFlag(InvSlotType.RightHand)))
            {
                // Set the contained items active if there's an item inserted inside the container. Enables e.g. the rifle flashlight when it's attached to the rifle (put inside of it).
                SetContainedActive(true);
            }
            if (containedItem.FlippedX != item.FlippedX)
            {
                containedItem.FlipX(relativeToSub: false);
            }
            if (containedItem.FlippedY != item.FlippedY)
            {
                containedItem.FlipY(relativeToSub: false);
            }
            item.SetContainedItemPositions();
            CharacterHUD.RecreateHudTextsIfFocused(item, containedItem);
            OnContainedItemsChanged.Invoke(this);
        }

        public override void Move(Vector2 amount, bool ignoreContacts = false)
        {
            SetContainedItemPositions();
        }

        public void OnItemRemoved(Item containedItem)
        {
            foreach (ActiveContainedItem activeContainedItem in activeContainedItems)
            {
                if (activeContainedItem.Item != containedItem || !ShouldApplyEffects(activeContainedItem)) { continue; }
                activeContainedItem.StatusEffect.Apply(ActionType.OnRemoved, deltaTime: 1, item, targets);
            }

            containedItem.OnInsertedEffectsApplied = false;

            activeContainedItems.RemoveAll(i => i.Item == containedItem);
            containedItems.RemoveAll(i => i.Item == containedItem);
            item.SetContainedItemPositions();
            //deactivate if the inventory is empty
            IsActive = hasSignalConnections || activeContainedItems.Count > 0 || Inventory.AllItems.Any(static it => it.body != null);
            CharacterHUD.RecreateHudTextsIfFocused(item, containedItem);
            OnContainedItemsChanged.Invoke(this);
        }

        public bool BlameEquipperForDeath()
        {
            return activeContainedItems.Any(c => c.BlameEquipperForDeath);
        }

        public bool CanBeContained(Item item)
        {
            if (!AllowAccessWhenDropped && this.item.body is { Enabled: true }) { return false; }
            return slotRestrictions.Any(s => s.MatchesItem(item));
        }

        public bool CanBeContained(Item item, int index)
        {
            if (index < 0 || index >= capacity) { return false; }
            if (!AllowAccessWhenDropped && this.item.body is { Enabled: true }) { return false; }
            return slotRestrictions[index].MatchesItem(item);
        }

        public bool CanBeContained(ItemPrefab itemPrefab)
        {
            return slotRestrictions.Any(s => s.MatchesItem(itemPrefab));
        }

        public bool CanBeContained(ItemPrefab itemPrefab, int index)
        {
            if (index < 0 || index >= capacity) { return false; }
            return slotRestrictions[index].MatchesItem(itemPrefab);
        }

        public bool ContainsItemsWithSameIdentifier(Item item)
        {
            if (item == null) { return false; }
            foreach (var containedItem in Inventory.AllItems)
            {
                if (containedItem.Prefab.Identifier == item.Prefab.Identifier)
                {
                    return true;
                }
            }
            return false;
        }

        public override void FlipX(bool relativeToSub)
        {
            base.FlipX(relativeToSub);
            if (HideItems) { return; }
            if (item.body == null) { return; }
            foreach (Item containedItem in Inventory.AllItems)
            {
                if (containedItem.body != null && containedItem.body.Enabled && containedItem.body.Dir != item.body.Dir)
                {
                    containedItem.FlipX(relativeToSub);
                }
            }
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (!string.IsNullOrEmpty(SpawnWithId) && !alwaysContainedItemsSpawned)
            {
                SpawnAlwaysContainedItems();
                alwaysContainedItemsSpawned = true;
            }

            if (hasSignalConnections)
            {
                float totalConditionValue = 0, totalConditionPercentage = 0; int totalItems = 0;
                foreach (var item in Inventory.AllItems)
                {
                    if (!MathUtils.NearlyEqual(item.Condition, 0))
                    {
                        totalConditionValue += item.Condition;
                        totalConditionPercentage += item.ConditionPercentage;
                        totalItems++;
                    }
                }

                if (!MathUtils.NearlyEqual(totalConditionValue, prevTotalConditionValue))
                {
                    totalConditionValueString = ((int)totalConditionValue).ToString(CultureInfo.InvariantCulture);
                    prevTotalConditionValue = totalConditionValue;
                }

                if (!MathUtils.NearlyEqual(totalConditionPercentage, prevTotalConditionPercentage))
                {
                    totalConditionPercentageString = ((int)totalConditionPercentage).ToString(CultureInfo.InvariantCulture);
                    prevTotalConditionPercentage = totalConditionPercentage;
                }

                if (totalItems != prevTotalItems)
                {
                    totalItemsString = totalItems.ToString(CultureInfo.InvariantCulture);
                    prevTotalItems = totalItems;
                }

                item.SendSignal(totalConditionValueString, "contained_conditions");
                item.SendSignal(totalConditionPercentageString, "contained_conditions_percentage");
                item.SendSignal(totalItemsString, "contained_items");
            }

            if (item.ParentInventory is CharacterInventory ownerInventory)
            {
                SetContainedItemPositionsIfNeeded();

                if (AutoInject || subContainersCanAutoInject)
                {
                    //normally autoinjection should delete the (medical) item, so it only gets applied once
                    //but in multiplayer clients aren't allowed to remove items themselves, so they may be able to trigger this dozens of times
                    //before the server notifies them of the item being removed, leading to a sharp lag spike.
                    //this can also happen with mods, if there's a way to autoinject something that doesn't get removed On Use.
                    //so let's ensure the item is only applied once per second at most.

                    autoInjectCooldown -= deltaTime;
                    if (autoInjectCooldown <= 0.0f &&
                        ownerInventory?.Owner is Character ownerCharacter && 
                        //no point in trying to heal if the character is already dead
                        !ownerCharacter.IsDead &&
                        ownerCharacter.HealthPercentage / 100f <= AutoInjectThreshold &&
                        ownerCharacter.HasEquippedItem(item))
                    {
                        if (AutoInject)
                        {
                            Inventory.AllItemsMod.ForEach(i => Inject(i));
                        }
                        else
                        {
                            for (int i = 0; i < slotRestrictions.Length; i++)
                            {
                                if (slotRestrictions[i].AutoInject)
                                {
                                    Inventory.GetItemsAt(i).ForEachMod(i => Inject(i));
                                }
                            }
                        }
                        void Inject(Item item)
                        {
                            item.ApplyStatusEffects(ActionType.OnSuccess, 1.0f, ownerCharacter, useTarget: ownerCharacter);
                            item.ApplyStatusEffects(ActionType.OnUse, 1.0f, ownerCharacter, useTarget: ownerCharacter);
                            item.GetComponent<GeneticMaterial>()?.Equip(ownerCharacter);
                        }
                        autoInjectCooldown = AutoInjectInterval;
                    }
                }

            }
            else if (item.body != null && item.body.Enabled)
            {
                if (item.body.FarseerBody.Awake)
                {
                    SetContainedItemPositionsIfNeeded();
                }
            }
            else if (!hasSignalConnections && activeContainedItems.Count == 0)
            {
                IsActive = false;
                return;
            }

            foreach (ActiveContainedItem activeContainedItem in activeContainedItems)
            {
                if (!ShouldApplyEffects(activeContainedItem)) { continue; }

                StatusEffect effect = activeContainedItem.StatusEffect;
                effect.Apply(ActionType.OnActive, deltaTime, item, targets);
                effect.Apply(ActionType.OnContaining, deltaTime, item, targets);
                if (item.GetComponent<Wearable>() is Wearable { IsActive: true })
                {
                    effect.Apply(ActionType.OnWearing, deltaTime, item, targets);
                }
            }
        }

        private bool ShouldApplyEffects(ActiveContainedItem activeContainedItem)
        {
            Item contained = activeContainedItem.Item;
            if (activeContainedItem.ExcludeBroken && contained.Condition <= 0) { return false; }
            if (activeContainedItem.ExcludeFullCondition && contained.IsFullCondition) { return false; }
            StatusEffect effect = activeContainedItem.StatusEffect;

            targets.Clear();
            if (effect.HasTargetType(StatusEffect.TargetType.This))
            {
                targets.AddRange(item.AllPropertyObjects);
            }
            if (effect.HasTargetType(StatusEffect.TargetType.Contained))
            {
                targets.AddRange(contained.AllPropertyObjects);
            }
            if (effect.HasTargetType(StatusEffect.TargetType.Character) && item.ParentInventory?.Owner is Character character)
            {
                targets.Add(character);
            }
            if (effect.HasTargetType(StatusEffect.TargetType.NearbyItems) || effect.HasTargetType(StatusEffect.TargetType.NearbyCharacters))
            {
                effect.AddNearbyTargets(item.WorldPosition, targets);
            }
            return true;
        }

        /// <summary>
        /// Set the positions of the contained items if this item has moved/rotated enough
        /// </summary>
        private void SetContainedItemPositionsIfNeeded()
        {
            if (Vector2.DistanceSquared(prevContainedItemRefreshPosition, item.Position) > 10.0f ||
                Math.Abs(prevContainedItemRefreshRotation - item.body?.Rotation ?? item.RotationRad) > 0.01f)
            {
                SetContainedItemPositions();
                prevContainedItemRefreshPosition = item.Position;
                prevContainedItemRefreshRotation = item.body?.Rotation ?? item.RotationRad;
            }
        }

        public override void UpdateBroken(float deltaTime, Camera cam)
        {
            //update when the item is broken too to get OnContaining effects to execute and contained item positions to update
            if (IsActive)
            {
                Update(deltaTime, cam);
            }
        }

        public override bool HasRequiredItems(Character character, bool addMessage, LocalizedString msg = null)
        {
            return IsAccessible() && base.HasRequiredItems(character, addMessage, msg);
        }
        
        /// <summary>
        /// Is the container currently accessible. Use this method for checking the accessibility logic, instead of using custom logic on the properties. 
        /// Use <see cref="HasRequiredItems"/> instead to do a transitive check, where the items of the character are also checked.
        /// </summary>
        public bool IsAccessible()
        {
            if (!AllowAccess) { return false; }
            if (AccessOnlyWhenBroken) 
            { 
                if (Screen.Selected is { IsEditor: true })
                {
                    // AccessOnlyWhenBroken doesn't apply to editors.
                    return true;
                }
                return item.Condition <= 0;
            }
            return true; 
        }

        public override bool Select(Character character)
        {
            if (item.Container != null) { return false; }
            if (!IsAccessible()) { return false; }
            if (AutoInteractWithContained && character.SelectedItem == null && Screen.Selected is not { IsEditor: true })
            {
                foreach (Item contained in Inventory.AllItems)
                {
                    if (CanAutoInteractWithContained(contained) && contained.TryInteract(character))
                    {
                        character.FocusedItem = contained;
                        return false;
                    }
                }
            }
            var abilityItem = new AbilityItemContainer(item);
            character.CheckTalents(AbilityEffectType.OnOpenItemContainer, abilityItem);

            if (item.ParentInventory?.Owner == character)
            {
                //can't select ItemContainers in the character's inventory (the inventory is drawn by hovering the cursor over the inventory slot, not as a GUIFrame)
                return false;
            }
            else
            {
                return base.Select(character);
            }
        }

        public override bool Pick(Character picker)
        {
            if (!IsAccessible()) { return false; }
            if (AutoInteractWithContained && Screen.Selected is not { IsEditor: true })
            {
                foreach (Item contained in Inventory.AllItems)
                {
                    if (CanAutoInteractWithContained(contained) && contained.TryInteract(picker))
                    {
                        picker.FocusedItem = contained;
                        return true;
                    }
                }
            }

            IsActive = true;

            return picker != null;
        }

        public override bool Combine(Item item, Character user)
        {
            if (!AllowDragAndDrop && user != null) { return false; }
            if (!slotRestrictions.Any(s => s.MatchesItem(item))) { return false; }
            if (user != null && !user.CanAccessInventory(Inventory)) { return false; }
            //genetic materials use special logic for combining, don't allow doing it by placing them inside each other here
            if (this.Item.GetComponent<GeneticMaterial>() != null) { return false; }

            if (Inventory.TryPutItem(item, user))
            {            
                IsActive = true;
                if (hideItems && item.body != null) { item.body.Enabled = false; }
                            
                return true;
            }

            return false;
        }

        public override void Drop(Character dropper, bool setTransform = true)
        {
            IsActive = true;
            SetContainedActive(false);
        }

        public override void Equip(Character character)
        {
            IsActive = true;
            if (character != null && character.HasEquippedItem(item, predicate: slot => slot.HasFlag(InvSlotType.LeftHand) || slot.HasFlag(InvSlotType.RightHand)))
            {
                SetContainedActive(true);
            }
            else
            {
                SetContainedActive(false);
            }
        }

        private bool CanAutoInteractWithContained(Item containedItem)
        {
            return AutoInteractWithContained && 
                (autoInteractWithContainedTags.None() || autoInteractWithContainedTags.Any(t => containedItem.HasTag(t)));
        }

        private void SetContainedActive(bool active)
        {
            if ((ContainableItems == null || !ContainableItems.Any(c => c.SetActive)) && 
                (AllSubContainableItems == null || !AllSubContainableItems.Any(c => c.SetActive))) 
            { 
                return; 
            }
            foreach (Item containedItem in Inventory.AllItems)
            {
                RelatedItem containableItem = FindContainableItem(containedItem);
                if (containableItem != null && containableItem.SetActive)
                {
                    foreach (var ic in containedItem.Components)
                    {
                        ic.IsActive = active;
                    }
                    if (containedItem.body != null)
                    {
                        containedItem.body.Enabled = active;
                        if (active)
                        {
                            containedItem.body.PhysEnabled = false;
                        }
                    }
                }
            }
            if (active)
            {
                FlipX(false);
            }
        }

        private RelatedItem FindContainableItem(Item item)
        {
            int index = Inventory.FindIndex(item);
            if (index == -1 ) { return null; }
             return slotRestrictions[index]?.ContainableItems?.FirstOrDefault(ci => ci.MatchesItem(item));
        }

        /// <summary>
        /// Returns the index of the first slot whose restrictions match the specified tag or identifier
        /// </summary>
        public int? FindSuitableSubContainerIndex(Identifier itemTagOrIdentifier)
        {
            for (int i = 0; i < slotRestrictions.Length; i++)
            {
                if (slotRestrictions[i].MatchesItem(itemTagOrIdentifier)) { return i; }
            }
            return null;
        }

        public override void ReceiveSignal(Signal signal, Connection connection)
        {
            switch (connection.Name)
            {
                case "activate":
                case "use":
                case "trigger_in":
                    if (signal.value != "0")
                    {
                        item.Use(1.0f, user: signal.sender);
                    }
                    break;
            }
        }

#warning There's some code duplication here and in DrawContainedItems() method, but it's not straightforward to get rid of it, because of slightly different logic and the usage of draw positions vs. positions etc. Should probably be splitted into smaller methods.
        public void SetContainedItemPositions()
        {
            if (containedItems.Count == 0) { return; }

            var rootBody = item.RootContainer?.body ?? item.body;

            Vector2 transformedItemPos = GetContainedPosition(
                drawPosition: false,
                out Vector2 transformedItemIntervalHorizontal,
                out Vector2 transformedItemIntervalVertical,
                out bool flippedX,
                out bool flippedY);

            int i = 0;
            Vector2 currentItemPos = transformedItemPos;
            foreach (ContainedItem contained in containedItems)
            {
                Vector2 itemPos = currentItemPos;
                if (contained.ItemPos.HasValue)
                {
                    Vector2 pos = contained.ItemPos.Value;
                    if (item.body != null)
                    {
                        Matrix transform = Matrix.CreateRotationZ(item.body.Rotation);
                        pos.X *= rootBody.Dir;
                        itemPos = Vector2.Transform(pos, transform) + item.body.Position;
                    }
                    else
                    {
                        itemPos = pos;
                        // This code is aped based on above. Not tested.
                        if (flippedX)
                        {
                            itemPos.X = -itemPos.X;
                            itemPos.X += item.Rect.Width;
                        }
                        if (flippedY)
                        {
                            itemPos.Y = -itemPos.Y;
                            itemPos.Y -= item.Rect.Height;
                        }
                        itemPos += new Vector2(item.Rect.X, item.Rect.Y);
                        if (Math.Abs(item.RotationRad) > 0.01f)
                        {
                            Matrix transform = Matrix.CreateRotationZ(item.RotationRad);
                            itemPos = Vector2.Transform(itemPos - item.Position, transform) + item.Position;
                        }
                    }
                }                

                if (contained.Item.body != null)
                {
                    try
                    {
                        Vector2 simPos = ConvertUnits.ToSimUnits(itemPos);
                        float rotation = itemRotation;
                        if (contained.Rotation != 0)
                        {
                            rotation = MathHelper.ToRadians(contained.Rotation);
                        }
                        if (item.body != null)
                        {
                            rotation *= rootBody.Dir;
                            rotation += item.body.Rotation;
                        }
                        else
                        {
                            //flip if flipped on one axis but not both (flipping on both axes is basically "double negative" and makes the rotation normal again)
                            if (flippedX ^ flippedY) { rotation = -rotation; }
                            rotation += -item.RotationRad;
                        }
                        contained.Item.body.FarseerBody.SetTransformIgnoreContacts(ref simPos, rotation);
                        contained.Item.body.UpdateDrawPosition(interpolate: false);
                    }
                    catch (Exception e)
                    {
                        DebugConsole.Log("SetTransformIgnoreContacts threw an exception in SetContainedItemPositions (" + e.Message + ")\n" + e.StackTrace.CleanupStackTrace());
                        GameAnalyticsManager.AddErrorEventOnce("ItemContainer.SetContainedItemPositions.InvalidPosition:" + contained.Item.Name,
                            GameAnalyticsManager.ErrorSeverity.Error,
                            "SetTransformIgnoreContacts threw an exception in SetContainedItemPositions (" + e.Message + ")\n" + e.StackTrace.CleanupStackTrace());
                    }
                    contained.Item.body.Submarine = item.Submarine;
                }

                contained.Item.Rect =
                    new Rectangle(
                        (int)(itemPos.X - contained.Item.Rect.Width / 2.0f),
                        (int)(itemPos.Y + contained.Item.Rect.Height / 2.0f),
                        contained.Item.Rect.Width, contained.Item.Rect.Height);

                contained.Item.Submarine = item.Submarine;
                contained.Item.CurrentHull = item.CurrentHull;
                contained.Item.SetContainedItemPositions();

                foreach (var lightComponent in contained.Item.GetComponents<LightComponent>())
                {
                    lightComponent.SetLightSourceTransform();
                }

                i++;
                if (Math.Abs(ItemInterval.X) > 0.001f && Math.Abs(ItemInterval.Y) > 0.001f)
                {
                    //interval set on both axes -> use a grid layout
                    currentItemPos += transformedItemIntervalHorizontal;
                    if (i % ItemsPerRow == 0)
                    {
                        currentItemPos = transformedItemPos;
                        currentItemPos += transformedItemIntervalVertical * (i / ItemsPerRow);
                    }
                }
                else
                {
                    currentItemPos += transformedItemIntervalHorizontal + transformedItemIntervalVertical;
                }
            }
        }

        private Vector2 GetContainedPosition(bool drawPosition, 
            out Vector2 transformedItemIntervalHorizontal, out Vector2 transformedItemIntervalVertical, 
            out bool flippedX, out bool flippedY)
        {
            Vector2 transformedItemPos = ItemPos * item.Scale;
            Vector2 transformedItemInterval = ItemInterval * item.Scale;
            transformedItemIntervalHorizontal = new Vector2(transformedItemInterval.X, 0.0f);
            transformedItemIntervalVertical = new Vector2(0.0f, transformedItemInterval.Y);

            if (item.RootContainer != null)
            {
                flippedX = item.RootContainer.FlippedX && item.RootContainer.Prefab.CanSpriteFlipX;
                flippedY = item.RootContainer.FlippedY && item.RootContainer.Prefab.CanSpriteFlipY;
            }
            else
            {
                flippedX = item.FlippedX && item.Prefab.CanSpriteFlipX;
                flippedY = item.FlippedY && item.Prefab.CanSpriteFlipY;
            }

            var rootBody = item.RootContainer?.body ?? item.body;
            bool bodyFlipped = rootBody is { Dir: -1 };

            if (ItemPos == Vector2.Zero && ItemInterval == Vector2.Zero && !drawPosition)
            {
                transformedItemPos = item.Position;
            }
            else
            {
                if (item.body == null)
                {
                    if (flippedX)
                    {
                        transformedItemPos.X = -transformedItemPos.X;
                        transformedItemPos.X += item.Rect.Width;
                        transformedItemInterval.X = -transformedItemInterval.X;
                        transformedItemIntervalHorizontal.X = -transformedItemIntervalHorizontal.X;
                    }
                    if (flippedY)
                    {
                        transformedItemPos.Y = -transformedItemPos.Y;
                        transformedItemPos.Y -= item.Rect.Height;
                        transformedItemInterval.Y = -transformedItemInterval.Y;
                        transformedItemIntervalVertical.Y = -transformedItemIntervalVertical.Y;
                    }
                    transformedItemPos += new Vector2(item.Rect.X, item.Rect.Y);
                    if (drawPosition)
                    {
                        if (item.Submarine != null) { transformedItemPos += item.Submarine.DrawPosition; }
                    }
                    if (Math.Abs(item.RotationRad) > 0.01f)
                    {
                        Matrix transform = Matrix.CreateRotationZ(-item.RotationRad);
                        transformedItemPos = 
                            drawPosition ?
                            Vector2.Transform(transformedItemPos - item.DrawPosition, transform) + item.DrawPosition :
                            Vector2.Transform(transformedItemPos - item.Position, transform) + item.Position;
                        transformedItemIntervalVertical = Vector2.Transform(transformedItemIntervalVertical, transform);
                        transformedItemIntervalHorizontal = Vector2.Transform(transformedItemIntervalHorizontal, transform);
                    }
                }
                else
                {
                    Matrix transform = Matrix.CreateRotationZ(drawPosition ? item.body.DrawRotation : item.body.Rotation);
                    if (bodyFlipped)
                    {
                        transformedItemPos.X = -transformedItemPos.X;
                        transformedItemInterval.X = -transformedItemInterval.X;
                        transformedItemIntervalHorizontal.X = -transformedItemIntervalHorizontal.X;
                    }

                    transformedItemPos = Vector2.Transform(transformedItemPos, transform);
                    transformedItemIntervalVertical = Vector2.Transform(transformedItemIntervalVertical, transform);
                    transformedItemIntervalHorizontal = Vector2.Transform(transformedItemIntervalHorizontal, transform);
                    transformedItemPos += drawPosition ? item.body.DrawPosition : item.body.Position;
                }
            }
            return transformedItemPos;
        }

        public override void OnItemLoaded()
        {
            Inventory.AllowSwappingContainedItems = AllowSwappingContainedItems;
            containableItemIdentifiers = slotRestrictions.SelectMany(s => s.ContainableItems?.SelectMany(ri => ri.Identifiers) ?? Enumerable.Empty<Identifier>()).ToImmutableHashSet();
            hasSignalConnections = item.Connections?.Any(c => c.Name is "contained_conditions" or "contained_conditions_percentage" or "contained_items") ?? false;
            if (item.Submarine == null || !item.Submarine.Loading)
            {
                SpawnAlwaysContainedItems();
            }
        }

        private bool initializingLoadedItems;

        public override void OnMapLoaded()
        {
            if (itemIds != null)
            {
                initializingLoadedItems = true;
                for (ushort i = 0; i < itemIds.Length; i++)
                {
                    if (i >= Inventory.Capacity) 
                    {
                        //legacy support: before item stacking was implemented, revolver for example had a separate slot for each bullet
                        //now there's just one, try to put the extra items where they fit (= stack them)
                        Inventory.TryPutItem(item, user: null, createNetworkEvent: false);
                        continue;
                    }
                    foreach (ushort id in itemIds[i])
                    {
                        if (Entity.FindEntityByID(id) is not Item item) { continue; }
                        Inventory.TryPutItem(item, i, false, false, null, createNetworkEvent: false, ignoreCondition: true);
                    }
                }
                initializingLoadedItems = false;
                itemIds = null;
            }

            //outpost and ruins are loaded in multiple stages (each module is loaded separately)
            //spawning items at this point during the generation will cause ID overlaps with the entities in the modules loaded afterwards
            //so let's not spawn them at this point, but in the 1st Update()
            if (item.Submarine?.Info != null && (item.Submarine.Info.IsOutpost || item.Submarine.Info.IsRuin))
            {
                if (SpawnWithId.Length > 0)
                {
                    IsActive = true;
                }
            }
            else
            {
                SpawnAlwaysContainedItems();
            }

            SetContainedItemPositions();
        }

        private void SpawnAlwaysContainedItems()
        {
            if (SpawnWithId.Length > 0 && (item.Condition > 0.0f || SpawnWithIdWhenBroken))
            {
                string[] splitIds = SpawnWithId.Split(',');
                foreach (string id in splitIds)
                {
                    ItemPrefab prefab = ItemPrefab.Prefabs.Find(m => m.Identifier == id);
                    if (prefab != null && Inventory != null && Inventory.CanProbablyBePut(prefab))
                    {
                        bool isEditor = false;
#if CLIENT
                        isEditor = Screen.Selected == GameMain.SubEditorScreen;
#endif
                        if (!isEditor && (Entity.Spawner == null || Entity.Spawner.Removed) && GameMain.NetworkMember == null)
                        {
                            var spawnedItem = new Item(prefab, Vector2.Zero, null);
                            Inventory.TryPutItem(spawnedItem, null, spawnedItem.AllowedSlots, createNetworkEvent: false); 
                            alwaysContainedItemsSpawned = true;
                        }
                        else
                        {
                            IsActive = true;
                            Entity.Spawner?.AddItemToSpawnQueue(prefab, Inventory, spawnIfInventoryFull: false, onSpawned: (Item item) => { alwaysContainedItemsSpawned = true; });
                        }
                    }
                }
            }
        }

        protected override void ShallowRemoveComponentSpecific()
        {
        }

        protected override void RemoveComponentSpecific()
        {
            base.RemoveComponentSpecific();
#if CLIENT
            inventoryTopSprite?.Remove();
            inventoryBackSprite?.Remove();
            inventoryBottomSprite?.Remove();
            ContainedStateIndicator?.Remove();

            if (SubEditorScreen.IsSubEditor())
            {
                Inventory.DeleteAllItems();
                return;
            }
#endif
            //if we're unloading the whole sub, no need to drop anything (everything's going to be removed anyway)
            if (!Submarine.Unloading)
            {
                Inventory.AllItemsMod.ForEach(it => it.Drop(null));
            }
        }

        public override void Load(ContentXElement componentElement, bool usePrefabValues, IdRemap idRemap, bool isItemSwap)
        {
            base.Load(componentElement, usePrefabValues, idRemap, isItemSwap);

            string containedString = componentElement.GetAttributeString("contained", "");
            string[] itemIdStrings = containedString.Split(',');
            itemIds = new List<ushort>[itemIdStrings.Length];
            for (int i = 0; i < itemIdStrings.Length; i++)
            {
                itemIds[i] ??= new List<ushort>();
                foreach (string idStr in itemIdStrings[i].Split(';'))
                {
                    if (!int.TryParse(idStr, out int id)) { continue; }
                    itemIds[i].Add(idRemap.GetOffsetId(id));
                }
            }
            ExtraStackSize = componentElement.GetAttributeInt(nameof(ExtraStackSize), 0);
        }

        public override XElement Save(XElement parentElement)
        {
            XElement componentElement = base.Save(parentElement);
            string[] itemIdStrings = new string[Inventory.Capacity];
            for (int i = 0; i < Inventory.Capacity; i++)
            {
                var items = Inventory.GetItemsAt(i);
                itemIdStrings[i] = string.Join(';', items.Select(it => it.ID.ToString()));
            }
            componentElement.Add(new XAttribute("contained", string.Join(',', itemIdStrings)));
            componentElement.Add(new XAttribute(nameof(ExtraStackSize), ExtraStackSize));
            return componentElement;
        }
    }

    class AbilityItemContainer : AbilityObject, IAbilityItem
    {
        public AbilityItemContainer(Item item)
        {
            Item = item;
        }
        public Item Item { get; set; }
    }
}
