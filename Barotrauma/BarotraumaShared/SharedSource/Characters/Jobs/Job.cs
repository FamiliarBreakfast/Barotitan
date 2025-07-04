﻿using System;
using Barotrauma.Items.Components;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    class Job
    {
        private readonly JobPrefab prefab;

        private readonly Dictionary<Identifier, Skill> skills;

        public LocalizedString Name => prefab.Name;

        public LocalizedString Description => prefab.Description;

        public JobPrefab Prefab => prefab;

        public int Variant;

        public Skill PrimarySkill { get; private set; }

        public Job(JobPrefab jobPrefab, bool isPvP) : this(jobPrefab, isPvP, randSync: Rand.RandSync.Unsynced, variant: 0) { }

        public Job(JobPrefab jobPrefab, bool isPvP, Rand.RandSync randSync, int variant, params Skill[] s)
        {
            prefab = jobPrefab;
            Variant = variant;

            skills = new Dictionary<Identifier, Skill>();
            foreach (var skill in s) { skills.Add(skill.Identifier, skill); }
            foreach (SkillPrefab skillPrefab in prefab.Skills)
            {
                Skill skill;
                if (skills.ContainsKey(skillPrefab.Identifier))
                {
                    skill = skills[skillPrefab.Identifier];
                    skills[skillPrefab.Identifier] = new Skill(skill.Identifier, skill.Level);
                }
                else
                {
                    skill = new Skill(skillPrefab, isPvP, randSync);
                    skills.Add(skillPrefab.Identifier, skill);
                }
                if (skillPrefab.IsPrimarySkill) { PrimarySkill = skill; }
            }
        }

        public Job(ContentXElement element)
        {
            Identifier identifier = element.GetAttributeIdentifier("identifier", "");
            JobPrefab p;
            if (!JobPrefab.Prefabs.ContainsKey(identifier))
            {
                DebugConsole.ThrowError($"Could not find the job {identifier}. Giving the character a random job.",
                    contentPackage: element.ContentPackage);
                p = JobPrefab.Random(Rand.RandSync.Unsynced);
            }
            else
            {
                p = JobPrefab.Prefabs[identifier];
            }
            prefab = p;
            skills = new Dictionary<Identifier, Skill>();
            foreach (var subElement in element.Elements())
            {
                if (subElement.NameAsIdentifier() != "skill") { continue; }
                Identifier skillIdentifier = subElement.GetAttributeIdentifier("identifier", "");
                if (skillIdentifier.IsEmpty) { continue; }
                var skill = new Skill(skillIdentifier, subElement.GetAttributeFloat("level", 0));
                skills.Add(skillIdentifier, skill);
                if (skillIdentifier == prefab.PrimarySkill?.Identifier) { PrimarySkill = skill; }
            }
        }

        public static Job Random(bool isPvP, Rand.RandSync randSync)
        {
            var prefab = JobPrefab.Random(randSync);
            int variant = Rand.Range(0, prefab.Variants, randSync);
            return new Job(prefab, isPvP, randSync, variant);
        }

        public IEnumerable<Skill> GetSkills()
        {
            return skills.Values;
        }

        public float GetSkillLevel(Identifier skillIdentifier)
        {
            if (skillIdentifier.IsEmpty) { return 0.0f; }
            skills.TryGetValue(skillIdentifier, out Skill skill);
            return skill?.Level ?? 0.0f;
        }

        public Skill GetSkill(Identifier skillIdentifier)
        {
            if (skillIdentifier.IsEmpty) { return null; }
            skills.TryGetValue(skillIdentifier, out Skill skill);
            return skill;
        }

        public void OverrideSkills(Dictionary<Identifier, float> newSkills)
        {
            skills.Clear();            
            foreach (var newSkillInfo in newSkills)
            {
                var newSkill = new Skill(newSkillInfo.Key, newSkillInfo.Value);
                if (PrimarySkill != null && newSkill.Identifier == PrimarySkill.Identifier)
                {
                    PrimarySkill = newSkill;
                }
                skills.Add(newSkillInfo.Key, newSkill);
            }
        }

        public void IncreaseSkillLevel(Identifier skillIdentifier, float increase, bool increasePastMax)
        {
            if (skills.TryGetValue(skillIdentifier, out Skill skill))
            {
                skill.IncreaseSkill(increase, increasePastMax);
            }
            else
            {
                skills.Add(
                    skillIdentifier,
                    new Skill(skillIdentifier, increase));
            }
        }
        
        /// <summary>
        /// Note: Does not automatically filter items by team or by game mode. See <see cref="JobItem.GetItemIdentifier(CharacterTeamType?, JobItem.GameModeType)"/>
        /// </summary>
        public bool HasJobItem(Func<JobPrefab.JobItem, bool> predicate) => prefab.HasJobItem(Variant, predicate);

        public void GiveJobItems(Character character, bool isPvPMode, WayPoint spawnPoint = null)
        {
            if (!prefab.JobItems.TryGetValue(Variant, out var spawnItems)) { return; }

            foreach (JobPrefab.JobItem jobItem in spawnItems)
            {
                //spawn the "root items" here, InitializeJobItem goes through the children recursively
                if (jobItem.ParentItem != null) { continue; }
                for (int i = 0; i < jobItem.Amount; i++)
                {
                    InitializeJobItem(character, isPvPMode, jobItem, spawnItems, spawnPoint);
                }
            }

            if (GameMain.GameSession is { TraitorsEnabled: true } && character.IsSecurity)
            {
                var traitorGuidelineItem = ItemPrefab.Prefabs.Find(ip => ip.Tags.Contains(Tags.TraitorGuidelinesForSecurity));
                Entity.Spawner.AddItemToSpawnQueue(traitorGuidelineItem, character.Inventory);
            }
        }

        private void InitializeJobItem(Character character, bool isPvPMode, JobPrefab.JobItem jobItem, IEnumerable<JobPrefab.JobItem> allJobItems, WayPoint spawnPoint = null, Item parentItem = null)
        {
            Identifier itemIdentifier = jobItem.GetItemIdentifier(character.TeamID, isPvPMode);
            if (itemIdentifier.IsEmpty) { return; }
            if ((MapEntityPrefab.FindByIdentifier(itemIdentifier) ?? MapEntityPrefab.FindByName(itemIdentifier.Value)) is not ItemPrefab itemPrefab)
            {
                DebugConsole.ThrowErrorLocalized($"Tried to spawn \"{Name}\" with the item \"{itemIdentifier}\". Matching item prefab not found.");
                return;
            }

            Item item = new Item(itemPrefab, character.Position, null);

#if SERVER
            if (GameMain.Server != null && Entity.Spawner != null)
            {
                if (GameMain.Server.EntityEventManager.UniqueEvents.Any(ev => ev.Entity == item))
                {
                    string errorMsg = $"Error while spawning job items. Item {item.Name} created network events before the spawn event had been created.";
                    DebugConsole.ThrowError(errorMsg);
                    GameAnalyticsManager.AddErrorEventOnce("Job.InitializeJobItem:EventsBeforeSpawning", GameAnalyticsManager.ErrorSeverity.Error, errorMsg);
                    GameMain.Server.EntityEventManager.UniqueEvents.RemoveAll(ev => ev.Entity == item);
                    GameMain.Server.EntityEventManager.Events.RemoveAll(ev => ev.Entity == item);
                }

                Entity.Spawner.CreateNetworkEvent(new EntitySpawner.SpawnEntity(item));
            }
#endif

            if (jobItem.Equip)
            {
                //if the item is both pickable and wearable, try to wear it instead of picking it up
                List<InvSlotType> allowedSlots =
                   item.GetComponents<Pickable>().Count() > 1 ?
                   new List<InvSlotType>(item.GetComponent<Wearable>()?.AllowedSlots ?? item.GetComponent<Pickable>().AllowedSlots) :
                   new List<InvSlotType>(item.AllowedSlots);
                allowedSlots.Remove(InvSlotType.Any);
                item.UnequipAutomatically = false;
                character.Inventory.TryPutItem(item, null, allowedSlots);
            }
            else
            {
                character.Inventory.TryPutItem(item, null, item.AllowedSlots);
            }

            Wearable wearable = item.GetComponent<Wearable>();
            if (wearable != null)
            {
                if (Variant > 0 && Variant <= wearable.Variants)
                {
                    wearable.Variant = Variant;
                }
                else
                {
                    wearable.Variant = wearable.Variant; //force server event
                    if (wearable.Variants > 0 && Variant == 0)
                    {
                        //set variant to the same as the wearable to get the rest of the character's gear
                        //to use the same variant (if possible)
                        Variant = wearable.Variant;
                    }
                }
            }

            IdCard idCardComponent = item.GetComponent<IdCard>();
            idCardComponent?.Initialize(spawnPoint, character);

            foreach (WifiComponent wifiComponent in item.GetComponents<WifiComponent>())
            {
                wifiComponent.TeamID = character.TeamID;
            }

            parentItem?.Combine(item, user: null);

            foreach (JobPrefab.JobItem childItem in allJobItems)
            {
                if (childItem.ParentItem == jobItem)
                {
                    for (int i = 0; i < childItem.Amount; i++)
                    {
                        InitializeJobItem(character, isPvPMode, childItem, allJobItems, spawnPoint, parentItem: item);
                    }
                }
            }
        }

        public XElement Save(XElement parentElement)
        {
            XElement jobElement = new XElement("job");

            jobElement.Add(new XAttribute("name", Name));
            jobElement.Add(new XAttribute("identifier", prefab.Identifier));

            foreach (KeyValuePair<Identifier, Skill> skill in skills)
            {
                jobElement.Add(new XElement("skill", new XAttribute("identifier", skill.Value.Identifier), new XAttribute("level", skill.Value.Level)));
            }
            
            parentElement.Add(jobElement);
            return jobElement;
        }
    }
}
