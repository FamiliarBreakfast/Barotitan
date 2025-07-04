﻿using Barotrauma.Extensions;
using Barotrauma.IO;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    partial class MultiPlayerCampaign : CampaignMode
    {
        private readonly List<CharacterCampaignData> characterData = new List<CharacterCampaignData>();
        private readonly Dictionary<ushort, Wallet> walletsToCheck = new Dictionary<ushort, Wallet>();
        private readonly HashSet<NetWalletTransaction> transactions = new HashSet<NetWalletTransaction>();
        private const float clientCheckInterval = 10;
        private float clientCheckTimer = clientCheckInterval;
        
        /// <summary>
        /// Temporary backup storage for characters that have been overwritten by SaveSingleCharacter, this will be gone
        /// once the round ends or the server closes. Currently needed to enable the console command "revive" in ironman mode.
        /// </summary>
        public List<CharacterCampaignData> replacedCharacterDataBackup = new List<CharacterCampaignData>();

        public override Wallet GetWallet(Client client = null)
        {
            if (client is null) { throw new ArgumentNullException(nameof(client), "Client should not be null in multiplayer"); }

            if (client.Character is { } character)
            {
                return character.Wallet;
            }

            return Wallet.Invalid;
        }

        private bool forceMapUI;
        public bool ForceMapUI
        {
            get { return forceMapUI; }
            set
            {
                if (forceMapUI == value) { return; }
                forceMapUI = value;
                IncrementLastUpdateIdForFlag(NetFlags.MapAndMissions);
            }
        }

        public bool GameOver { get; private set; }

        class SavedExperiencePoints
        {
            public readonly Option<AccountId> AccountId;
            public readonly Address Address;
            public readonly int ExperiencePoints;

            public SavedExperiencePoints(Client client)
            {
                AccountId = client.AccountId;
                Address = client.Connection.Endpoint.Address;
                ExperiencePoints = client.Character?.Info?.ExperiencePoints ?? 0;
            }

            public SavedExperiencePoints(XElement element)
            {
                AccountId = Networking.AccountId.Parse(
                    element.GetAttributeString("accountid", null)
                    ?? element.GetAttributeString("steamid", ""));
                Address = Address.Parse(
                        element.GetAttributeString("address", null)
                        ?? element.GetAttributeString("endpoint", ""))
                    .Fallback(new UnknownAddress());
                ExperiencePoints = element.GetAttributeInt("points", 0);
            }
        }

        private readonly List<SavedExperiencePoints> savedExperiencePoints = new List<SavedExperiencePoints>();

        public override bool Paused
        {
            get { return ForceMapUI || CoroutineManager.IsCoroutineRunning("LevelTransition"); }
        }

        private bool purchasedHullRepairs, purchasedLostShuttles, purchasedItemRepairs;
        public override bool PurchasedHullRepairs 
        { 
            get { return purchasedHullRepairs; }
            set
            {
                if (purchasedHullRepairs == value) { return; }
                purchasedHullRepairs = value;
                PurchasedHullRepairsInLatestSave |= value;
                IncrementLastUpdateIdForFlag(NetFlags.Misc);
                DebugConsole.NewMessage("Set PurchasedHullRepairs to " + PurchasedHullRepairs, Color.Cyan);
            }
        }
        public override bool PurchasedLostShuttles
        {
            get { return purchasedLostShuttles; }
            set
            {
                if (purchasedLostShuttles == value) { return; }
                purchasedLostShuttles = value;
                PurchasedLostShuttlesInLatestSave |= value;
                IncrementLastUpdateIdForFlag(NetFlags.Misc);
            }
        }
        public override bool PurchasedItemRepairs
        {
            get { return purchasedItemRepairs; }
            set
            {
                if (purchasedItemRepairs == value) { return; }
                purchasedItemRepairs = value;
                PurchasedItemRepairsInLatestSave |= value;
                IncrementLastUpdateIdForFlag(NetFlags.Misc);
            }
        }

        public static void StartNewCampaign(string savePath, string subPath, string seed, CampaignSettings startingSettings)
        {
            if (string.IsNullOrWhiteSpace(savePath)) { return; }

            GameMain.GameSession = new GameSession(new SubmarineInfo(subPath), Option.None, CampaignDataPath.CreateRegular(savePath), GameModePreset.MultiPlayerCampaign, startingSettings, seed);
            GameMain.NetLobbyScreen.ToggleCampaignMode(true);
            SaveUtil.SaveGame(GameMain.GameSession.DataPath);

            DebugConsole.NewMessage("Campaign started!", Color.Cyan);
            DebugConsole.NewMessage("Current location: " + GameMain.GameSession.Map.CurrentLocation.DisplayName, Color.Cyan);
            ((MultiPlayerCampaign)GameMain.GameSession.GameMode).LoadInitialLevel();
        }

        public static void LoadCampaign(CampaignDataPath path, Client client)
        {
            GameMain.NetLobbyScreen.ToggleCampaignMode(true);
            try
            {
                SaveUtil.LoadGame(path);
                if (GameMain.GameSession.GameMode is MultiPlayerCampaign mpCampaign)
                {
                    mpCampaign.LastSaveID++;
                }
                else
                {
                    DebugConsole.ThrowError("Failed to load a campaign. Unexpected game mode: " + GameMain.GameSession.GameMode ?? "none");
                    return;
                }
            }
            catch (Exception e)
            {
                string errorMsg = $"Error while loading the save {path.LoadPath}";
                if (client != null)
                {
                    GameMain.Server?.SendDirectChatMessage($"{errorMsg}: {e.Message}\n{e.StackTrace}", client, ChatMessageType.Error);
                }
                DebugConsole.ThrowError(errorMsg, e);
                return;
            }
            DebugConsole.NewMessage("Campaign loaded!", Color.Cyan);
            DebugConsole.NewMessage(
                GameMain.GameSession.Map.SelectedLocation == null ?
                GameMain.GameSession.Map.CurrentLocation.DisplayName :
                GameMain.GameSession.Map.CurrentLocation.DisplayName + " -> " + GameMain.GameSession.Map.SelectedLocation.DisplayName, Color.Cyan);
        }

        protected override void LoadInitialLevel()
        {
            NextLevel = map.SelectedConnection?.LevelData ?? map.CurrentLocation.LevelData;
            MirrorLevel = false;
            GameMain.Server.TryStartGame();
        }

        public static void StartCampaignSetup()
        {
            DebugConsole.NewMessage("********* CAMPAIGN SETUP *********", Color.White);
            DebugConsole.ShowQuestionPrompt("Do you want to start a new campaign? Y/N", (string arg) =>
            {
                if (arg.Equals("y", StringComparison.OrdinalIgnoreCase) || arg.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    DebugConsole.ShowQuestionPrompt("Enter a save name for the campaign:", (string saveName) =>
                    {
                        string savePath = SaveUtil.CreateSavePath(SaveUtil.SaveType.Multiplayer, saveName);
                        StartNewCampaign(savePath, GameMain.NetLobbyScreen.SelectedSub.FilePath, GameMain.NetLobbyScreen.LevelSeed, CampaignSettings.Empty);
                    });
                }
                else
                {
                    var saveFiles = SaveUtil.GetSaveFiles(SaveUtil.SaveType.Multiplayer, includeInCompatible: false).ToArray();
                    if (saveFiles.Length == 0)
                    {
                        DebugConsole.ThrowError("No save files found.");
                        return;
                    }
                    DebugConsole.NewMessage("Saved campaigns:", Color.White);
                    for (int i = 0; i < saveFiles.Length; i++)
                    {
                        DebugConsole.NewMessage("   " + i + ". " + saveFiles[i].FilePath, Color.White);
                    }
                    DebugConsole.ShowQuestionPrompt("Select a save file to load (0 - " + (saveFiles.Length - 1) + "):", (string selectedSave) =>
                    {
                        int saveIndex = -1;
                        if (!int.TryParse(selectedSave, out saveIndex)) { return; }

                        if (saveIndex < 0 || saveIndex >= saveFiles.Length)
                        {
                            DebugConsole.ThrowError("Invalid save file index.");
                        }
                        else
                        {
                            try
                            {
                                LoadCampaign(CampaignDataPath.CreateRegular(saveFiles[saveIndex].FilePath), client: null);
                            }
                            catch (Exception ex)
                            {
                                DebugConsole.ThrowError("Failed to load the campaign.", ex);
                            }
                        }
                    });
                }
            });
        }

        public override void Start()
        {
            base.Start();
            IncrementAllLastUpdateIds();
        }

        private static bool IsOwner(Client client) => client != null && client.Connection == GameMain.Server.OwnerConnection;

        public void SaveExperiencePoints(Client client)
        {
            ClearSavedExperiencePoints(client);
            savedExperiencePoints.Add(new SavedExperiencePoints(client));
        }
        public int GetSavedExperiencePoints(Client client)
        {
            return savedExperiencePoints.Find(s => client.AccountId == s.AccountId || client.Connection.Endpoint.Address == s.Address)?.ExperiencePoints ?? 0;
        }
        public void ClearSavedExperiencePoints(Client client)
        {
            savedExperiencePoints.RemoveAll(s => client.AccountId == s.AccountId || client.Connection.Endpoint.Address == s.Address);
        }

        public void RefreshCharacterCampaignData(Character character, bool refreshHealthData)
        {
            var matchingData = characterData.FirstOrDefault(c => c.CharacterInfo == character.Info);
            matchingData?.Refresh(character, refreshHealthData: refreshHealthData);
        }

        public void SavePlayers()
        {
            //refresh the character data of clients who are still in the server
            foreach (Client c in GameMain.Server.ConnectedClients)
            {
                //ignore if the character is controlling a monster
                //(we'll just use the previously saved campaign data if there's any)
                if (c.Character != null && c.Character.Info == null)
                {
                    c.Character = null;
                }
                //use the info of the character the client is currently controlling
                // or the previously saved info if not (e.g. if the client has been spectating or died)
                var characterInfo = c.Character?.Info;
                var matchingCharacterData = characterData.Find(d => d.MatchesClient(c));
                if (matchingCharacterData != null)
                {
                    //hasn't spawned this round -> don't touch the data
                    if (!matchingCharacterData.HasSpawned) { continue; }
                    characterInfo ??= matchingCharacterData.CharacterInfo;
                }
                if (characterInfo == null || characterInfo.Discarded) { continue; }
                //reduce skills if the character has died
                if (characterInfo.CauseOfDeath != null && characterInfo.CauseOfDeath.Type != CauseOfDeathType.Disconnected)
                {
                    characterInfo.ApplyDeathEffects();
                }
                c.CharacterInfo = characterInfo;
                SetClientCharacterData(c);
            }

            //refresh the character data of clients who aren't in the server anymore
            List<CharacterCampaignData> prevCharacterData = new List<CharacterCampaignData>(characterData);
            foreach (CharacterCampaignData data in prevCharacterData)
            {
                if (data.HasSpawned && !GameMain.Server.ConnectedClients.Any(c => data.MatchesClient(c)))
                {
                    var character = Character.CharacterList.Find(c => c.Info == data.CharacterInfo && !c.IsHusk);          
                    if (character != null &&
                        (!character.IsDead || character.CauseOfDeath?.Type == CauseOfDeathType.Disconnected))
                    {
                        //character still alive (or killed by Disconnect) -> save it as-is
                        characterData.RemoveAll(cd => cd.IsDuplicate(data));
                        data.Refresh(character, refreshHealthData: character.CauseOfDeath?.Type != CauseOfDeathType.Disconnected);
                        characterData.Add(data);
                    }
                    //check the cause of death in the CharacterInfo too (the character instance may have despawned, so we can't just rely on that)
                    else if (data.CharacterInfo.CauseOfDeath is not { Type: CauseOfDeathType.Disconnected })
                    {
                        //character dead or removed -> reduce skills, remove items, health data, etc
                        data.CharacterInfo.ApplyDeathEffects();
                        data.Reset();
                    }
                }
            }

            MoveDiscardedCharacterBalancesToBank();

            characterData.ForEach(cd => cd.HasSpawned = false);
            foreach (var cd in characterData)
            {
                //remove from crewmanager - we don't need to save the data there if it's been saved as CharacterCampaignData
                //(e.g. if a client has taken over a bot, we need to do this to prevent it being saved twice)
                CrewManager.RemoveCharacterInfo(cd.CharacterInfo);
            }

            SavePets();

            //remove all items that are in someone's inventory
            foreach (Character c in Character.CharacterList)
            {
                if (c.Inventory == null) { continue; }
                if (Level.Loaded.Type == LevelData.LevelType.Outpost && c.Submarine != Level.Loaded.StartOutpost)
                {
                    Map.CurrentLocation.RegisterTakenItems(c.Inventory.AllItems.Where(it => it.SpawnedInCurrentOutpost && it.OriginalModuleIndex > 0));
                }

                if (c.Info != null && c.IsBot)
                {
                    if (c.IsDead && c.CauseOfDeath?.Type != CauseOfDeathType.Disconnected) { CrewManager.RemoveCharacterInfo(c.Info); }
                    c.Info.HealthData = new XElement("health");
                    c.CharacterHealth.Save(c.Info.HealthData);
                    c.Info.InventoryData = new XElement("inventory");
                    c.SaveInventory();
                    c.Info.SaveOrderData();
                }

                c.Inventory.DeleteAllItems();
            }

            SaveActiveOrders();
        }

        public void MoveDiscardedCharacterBalancesToBank()
        {
            foreach (var discardedCharacter in discardedCharacters)
            {
                if (discardedCharacter.WalletData != null)
                {
                    var wallet = 
                        Character.CharacterList.Find(c => c.Info == discardedCharacter.CharacterInfo)?.Wallet ?? 
                        new Wallet(Option<Character>.None(), discardedCharacter.WalletData);
                    Bank.Give(wallet.Balance);
                }
            }
            discardedCharacters.Clear();
        }

        protected override IEnumerable<CoroutineStatus> DoLevelTransition(TransitionType transitionType, LevelData newLevel, Submarine leavingSub, bool mirror)
        {
            IncrementAllLastUpdateIds();

            switch (transitionType)
            {
                case TransitionType.None:
                    throw new InvalidOperationException("Level transition failed (no transitions available).");
                case TransitionType.ReturnToPreviousLocation:
                    //deselect destination on map
                    map.SelectLocation(-1);
                    break;
                case TransitionType.ProgressToNextLocation:
                    Map.MoveToNextLocation();
                    TotalPassedLevels++;
                    break;
                case TransitionType.End:
                    EndCampaign();
                    IsFirstRound = true;
                    break;
                case TransitionType.ProgressToNextEmptyLocation:
                    Map.Visit(Map.CurrentLocation);
                    TotalPassedLevels++;
                    break;
            }

            Map.ProgressWorld(this, transitionType, GameMain.GameSession.RoundDuration);

            bool success = GameMain.Server.ConnectedClients.Any(c => c.InGame && c.Character != null && !c.Character.IsDead);
            if (success)
            {
                foreach (Client c in GameMain.Server.ConnectedClients)
                {
                    if (c.Character?.HasAbilityFlag(AbilityFlags.RetainExperienceForNewCharacter) ?? false)
                    {
                        (GameMain.GameSession?.GameMode as MultiPlayerCampaign)?.SaveExperiencePoints(c);
                    }
                }
                // Event history must be registered before ending the round or it will be cleared
                GameMain.GameSession.EventManager.StoreEventDataAtRoundEnd();
            }

            //store the currently active missions at this point so we can communicate their states to clients, they're cleared in EndRound
            List<Mission> missions = GameMain.GameSession.Missions.ToList();

            GameMain.GameSession.EndRound("", transitionType);
            
            //--------------------------------------

            if (success)
            {
                SavePlayers();
                yield return CoroutineStatus.Running;
                LeaveUnconnectedSubs(leavingSub);
                NextLevel = newLevel;
                GameMain.GameSession.SubmarineInfo = new SubmarineInfo(GameMain.GameSession.Submarine);
                SaveUtil.SaveGame(GameMain.GameSession.DataPath);
            }
            else
            {
                PendingSubmarineSwitch = null;
                GameMain.Server.EndGame(TransitionType.None, wasSaved: false);
                LoadCampaign(GameMain.GameSession.DataPath, client: null);
                LastSaveID++;
                IncrementAllLastUpdateIds();
                yield return CoroutineStatus.Success;
            }

            CrewManager?.ClearCurrentOrders();

            //--------------------------------------

            GameMain.Server.EndGame(transitionType, wasSaved: true, missions);

            ForceMapUI = false;

            NextLevel = newLevel;
            MirrorLevel = mirror;


            yield return new WaitForSeconds(EndTransitionDuration * 0.5f);
            
            //don't start the next round automatically if we just finished the campaign
            if (transitionType != TransitionType.End)
            {
                GameMain.Server.TryStartGame();
            }

            yield return CoroutineStatus.Success;
        }

        partial void InitProjSpecific()
        {
            Identifier eventId = nameof(MultiPlayerCampaign).ToIdentifier();
            CargoManager.OnItemsInBuyCrateChanged.RegisterOverwriteExisting(eventId, _ =>  IncrementLastUpdateIdForFlag(NetFlags.ItemsInBuyCrate));
            CargoManager.OnPurchasedItemsChanged.RegisterOverwriteExisting(eventId, _ =>  IncrementLastUpdateIdForFlag(NetFlags.PurchasedItems));
            CargoManager.OnSoldItemsChanged.RegisterOverwriteExisting(eventId, _ =>  IncrementLastUpdateIdForFlag(NetFlags.SoldItems));
            UpgradeManager.OnUpgradesChanged.RegisterOverwriteExisting(eventId, _ =>  IncrementLastUpdateIdForFlag(NetFlags.UpgradeManager));

            Reputation.OnAnyReputationValueChanged.RegisterOverwriteExisting(eventId, _ => IncrementLastUpdateIdForFlag(NetFlags.Reputation));

            Map.OnLocationSelected = (loc, connection) => IncrementLastUpdateIdForFlag(NetFlags.MapAndMissions);
            Map.OnMissionsSelected = (loc, mission) => IncrementLastUpdateIdForFlag(NetFlags.MapAndMissions);

            //increment save ID so clients know they're lacking the most up-to-date save file
            LastSaveID++;
        }

        public bool CanPurchaseSub(SubmarineInfo info, Client client)
            => CanAfford(info.GetPrice(), client) && GetCampaignSubs().Contains(info);

        private readonly List<CharacterCampaignData> discardedCharacters = new List<CharacterCampaignData>();
        public void DiscardClientCharacterData(Client client)
        {
            foreach (var data in characterData.ToList())
            {
                if (data.MatchesClient(client))
                {
                    if (!discardedCharacters.Any(d => d.MatchesClient(client)))
                    {
                        discardedCharacters.Add(data);
                    }
                    DebugConsole.Log($"Client \"{client}\" discarded the character ({data.Name})");
                    data.CharacterInfo.Discarded = true;
                    characterData.Remove(data);
                    IncrementLastUpdateIdForFlag(NetFlags.CharacterInfo);
                }
            }
        }

        public CharacterCampaignData GetClientCharacterData(Client client)
        {
            return characterData.Find(cd => cd.MatchesClient(client));
        }

        public CharacterCampaignData GetCharacterData(CharacterInfo characterInfo)
        {
            return characterData.Find(cd => cd.CharacterInfo == characterInfo);
        }

        public CharacterCampaignData SetClientCharacterData(Client client)
        {
            characterData.RemoveAll(cd => cd.MatchesClient(client));
            var data = new CharacterCampaignData(client);
            characterData.Add(data);
            IncrementLastUpdateIdForFlag(NetFlags.CharacterInfo);
            return data;
        }

        public void AssignClientCharacterInfos(IEnumerable<Client> connectedClients)
        {
            foreach (Client client in connectedClients)
            {
                if (client.SpectateOnly && GameMain.Server.ServerSettings.AllowSpectating) { continue; }
                var matchingData = GetClientCharacterData(client);
                if (matchingData != null) { client.CharacterInfo = matchingData.CharacterInfo; }
            }
            IncrementLastUpdateIdForFlag(NetFlags.CharacterInfo);
        }

        public Dictionary<Client, Job> GetAssignedJobs(IEnumerable<Client> connectedClients)
        {
            var assignedJobs = new Dictionary<Client, Job>();
            foreach (Client client in connectedClients)
            {
                var matchingData = GetClientCharacterData(client);
                if (matchingData != null) assignedJobs.Add(client, matchingData.CharacterInfo.Job);
            }
            return assignedJobs;
        }

        public override void Update(float deltaTime)
        {
            if (CoroutineManager.IsCoroutineRunning("LevelTransition")) { return; }

            Map?.Radiation?.UpdateRadiation(deltaTime);

            base.Update(deltaTime);

            MedicalClinic?.Update(deltaTime);

            if (Level.Loaded != null)
            {
                if (Level.Loaded.Type == LevelData.LevelType.LocationConnection)
                {
                    var transitionType = GetAvailableTransition(out _, out Submarine leavingSub); 
                    if (transitionType == TransitionType.End ||
                        (Level.Loaded.IsEndBiome && transitionType == TransitionType.ProgressToNextLocation))
                    {
                        LoadNewLevel();
                    }
                    else if (GameMain.Server.ConnectedClients.Count == 0 || GameMain.Server.ConnectedClients.Any(c => c.InGame && c.Character != null && !c.Character.IsDead))
                    {
                        if (transitionType == TransitionType.ProgressToNextLocation && Level.Loaded.EndOutpost != null && Level.Loaded.EndOutpost.DockedTo.Contains(leavingSub))
                        {
                            LoadNewLevel();
                        }
                        else if (transitionType == TransitionType.ReturnToPreviousLocation && Level.Loaded.StartOutpost != null && Level.Loaded.StartOutpost.DockedTo.Contains(leavingSub))
                        {
                            LoadNewLevel();
                        }
                    }
                }
                else if (Level.Loaded.IsEndBiome)
                {
                    var transitionType = GetAvailableTransition(out _, out Submarine leavingSub);
                    if (transitionType == TransitionType.ProgressToNextLocation)
                    {
                        LoadNewLevel();
                    }
                }
                else if (Level.Loaded.Type == LevelData.LevelType.Outpost)
                {
                    KeepCharactersCloseToOutpost(deltaTime);
                }
            }

            UpdateClientsToCheck(deltaTime);
            UpdateWallets();
        }

        private void UpdateClientsToCheck(float deltaTime)
        {
            if (clientCheckTimer < clientCheckInterval)
            {
                clientCheckTimer += deltaTime;
                return;
            }

            clientCheckTimer = 0;
            walletsToCheck.Clear();
            walletsToCheck.Add(0, Bank);

            foreach (Character character in GameSession.GetSessionCrewCharacters(CharacterType.Player))
            {
                walletsToCheck.Add(character.ID, character.Wallet);
            }
        }

        private void UpdateWallets()
        {
            foreach (var (id, wallet) in walletsToCheck)
            {
                if (wallet.HasTransactions())
                {
                    NetWalletTransaction transaction = wallet.DequeueAndMergeTransactions(id);
                    if (!wallet.ShouldForceUpdate && transaction.ChangedData.BalanceChanged.IsNone() && transaction.ChangedData.RewardDistributionChanged.IsNone()) { continue; }
                    transactions.Add(transaction);
                    wallet.ShouldForceUpdate = false;
                }
            }

            if (transactions.Count == 0) { return; }

            NetWalletUpdate walletUpdate = new NetWalletUpdate
            {
                Transactions = transactions.ToArray()
            };

            transactions.Clear();

            foreach (Client client in GameMain.Server.ConnectedClients)
            {
                IWriteMessage msg = new WriteOnlyMessage().WithHeader(ServerPacketHeader.MONEY);
                ((INetSerializableStruct)walletUpdate).Write(msg);
                GameMain.Server?.ServerPeer?.Send(msg, client.Connection, DeliveryMethod.Reliable);
            }
        }

        public override void End(TransitionType transitionType = TransitionType.None)
        {
            GameOver = !GameMain.Server.ConnectedClients.Any(c => c.InGame && c.Character != null && !c.Character.IsDead);
            base.End(transitionType);
        }

        private bool IsFlagRequired(Client c, NetFlags flag)
            => !c.LastRecvCampaignUpdate.TryGetValue(flag, out var id) || NetIdUtils.IdMoreRecent(GetLastUpdateIdForFlag(flag), id);

        public void ServerWrite(IWriteMessage msg, Client c)
        {
            System.Diagnostics.Debug.Assert(map.Locations.Count < UInt16.MaxValue);

            NetFlags requiredFlags = lastUpdateID.Keys.Where(k => IsFlagRequired(c, k)).Aggregate((NetFlags)0, (f1, f2) => f1 | f2);

            msg.WriteUInt16((UInt16)requiredFlags);

            msg.WriteBoolean(IsFirstRound);
            msg.WriteByte(CampaignID);
            msg.WriteByte(RoundID);
            msg.WriteUInt16(lastSaveID);
            msg.WriteString(map.Seed);

            if (requiredFlags.HasFlag(NetFlags.Misc))
            {
                msg.WriteUInt16(GetLastUpdateIdForFlag(NetFlags.Misc));
                msg.WriteBoolean(PurchasedHullRepairs);
                msg.WriteBoolean(PurchasedItemRepairs);
                msg.WriteBoolean(PurchasedLostShuttles);
            }

            if (requiredFlags.HasFlag(NetFlags.MapAndMissions))
            {
                msg.WriteUInt16(GetLastUpdateIdForFlag(NetFlags.MapAndMissions));
                msg.WriteBoolean(ForceMapUI);
                msg.WriteBoolean(map.AllowDebugTeleport);
                msg.WriteUInt16(map.CurrentLocationIndex == -1 ? UInt16.MaxValue : (UInt16)map.CurrentLocationIndex);
                msg.WriteUInt16(map.SelectedLocationIndex == -1 ? UInt16.MaxValue : (UInt16)map.SelectedLocationIndex);

                if (map.CurrentLocation != null)
                {
                    msg.WriteByte((byte)map.CurrentLocation.AvailableMissions.Count());
                    foreach (Mission mission in map.CurrentLocation.AvailableMissions)
                    {
                        msg.WriteIdentifier(mission.Prefab.Identifier);
                        if (mission.Locations[0] == mission.Locations[1])
                        {
                            msg.WriteByte((byte)255);
                        }
                        else
                        {
                            Location missionDestination = mission.Locations[0] == map.CurrentLocation ? mission.Locations[1] : mission.Locations[0];
                            LocationConnection connection = map.CurrentLocation.Connections.Find(c => c.OtherLocation(map.CurrentLocation) == missionDestination);
                            msg.WriteByte((byte)map.CurrentLocation.Connections.IndexOf(connection));
                        }
                    }
                }
                else
                {
                    msg.WriteByte((byte)0);
                }

                var selectedMissionIndices = map.GetSelectedMissionIndices();
                msg.WriteByte((byte)selectedMissionIndices.Count());
                foreach (int selectedMissionIndex in selectedMissionIndices)
                {
                    msg.WriteByte((byte)selectedMissionIndex);
                }

                WriteStores(msg);
            }

            if (requiredFlags.HasFlag(NetFlags.SubList))
            {
                msg.WriteUInt16(GetLastUpdateIdForFlag(NetFlags.SubList));
                var subList = GameMain.NetLobbyScreen.GetSubList();
                List<int> ownedSubmarineIndices = new List<int>();
                for (int i = 0; i < subList.Count; i++)
                {
                    if (GameMain.GameSession.OwnedSubmarines.Any(s => s.Name == subList[i].Name))
                    {
                        ownedSubmarineIndices.Add(i);
                    }
                }
                msg.WriteUInt16((ushort)ownedSubmarineIndices.Count);
                foreach (int index in ownedSubmarineIndices)
                {
                    msg.WriteUInt16((ushort)index);
                }
            }
            if (requiredFlags.HasFlag(NetFlags.UpgradeManager))
            {
                msg.WriteUInt16(GetLastUpdateIdForFlag(NetFlags.UpgradeManager));
                msg.WriteUInt16((ushort)UpgradeManager.PendingUpgrades.Count);
                foreach (var (prefab, category, level) in UpgradeManager.PendingUpgrades)
                {
                    msg.WriteIdentifier(prefab.Identifier);
                    msg.WriteIdentifier(category.Identifier);
                    msg.WriteByte((byte)level);
                }
                msg.WriteUInt16((ushort)UpgradeManager.PurchasedItemSwaps.Count);
                foreach (var itemSwap in UpgradeManager.PurchasedItemSwaps)
                {
                    msg.WriteUInt16(itemSwap.ItemToRemove.ID);
                    msg.WriteIdentifier(itemSwap.ItemToInstall?.Identifier ?? Identifier.Empty);
                }
            }

            if (requiredFlags.HasFlag(NetFlags.ItemsInBuyCrate))
            {
                msg.WriteUInt16(GetLastUpdateIdForFlag(NetFlags.ItemsInBuyCrate));
                WriteItems(msg, CargoManager.ItemsInBuyCrate);
                WriteStores(msg);
            }

            if (requiredFlags.HasFlag(NetFlags.ItemsInSellFromSubCrate))
            {
                msg.WriteUInt16(GetLastUpdateIdForFlag(NetFlags.ItemsInSellFromSubCrate));
                WriteItems(msg, CargoManager.ItemsInSellFromSubCrate);
                WriteStores(msg);
            }

            if (requiredFlags.HasFlag(NetFlags.PurchasedItems))
            {
                msg.WriteUInt16(GetLastUpdateIdForFlag(NetFlags.PurchasedItems));
                WriteItems(msg, CargoManager.PurchasedItems);
                WriteStores(msg);

            }
            if (requiredFlags.HasFlag(NetFlags.SoldItems))
            {
                msg.WriteUInt16(GetLastUpdateIdForFlag(NetFlags.SoldItems));
                WriteItems(msg, CargoManager.SoldItems);
                WriteStores(msg);
            }
            if (requiredFlags.HasFlag(NetFlags.Reputation))
            {
                msg.WriteUInt16(GetLastUpdateIdForFlag(NetFlags.Reputation));
                // hopefully we'll never have more than 128 factions
                msg.WriteByte((byte)Factions.Count);
                foreach (Faction faction in Factions)
                {
                    msg.WriteIdentifier(faction.Prefab.Identifier);
                    msg.WriteSingle(faction.Reputation.Value);
                }
            }
            if (requiredFlags.HasFlag(NetFlags.CharacterInfo))
            {
                msg.WriteUInt16(GetLastUpdateIdForFlag(NetFlags.CharacterInfo));
                var characterData = GetClientCharacterData(c);
                if (characterData?.CharacterInfo == null)
                {
                    msg.WriteBoolean(false);
                }
                else
                {
                    msg.WriteBoolean(true);
                    characterData.CharacterInfo.ServerWrite(msg);
                }
            }

            void WriteStores(IWriteMessage msg)
            {
                if (map.CurrentLocation != null)
                {
                    // Store balance
                    bool hasStores = map.CurrentLocation.Stores != null && map.CurrentLocation.Stores.Any();
                    msg.WriteBoolean(hasStores);
                    if (hasStores)
                    {
                        msg.WriteByte((byte)map.CurrentLocation.Stores.Count);
                        foreach (var store in map.CurrentLocation.Stores.Values)
                        {
                            msg.WriteIdentifier(store.Identifier);
                            msg.WriteUInt16((UInt16)store.Balance);
                        }
                    }
                }
                else
                {
                    msg.WriteByte((byte)0);
                    // Store balance
                    msg.WriteBoolean(false);
                }
            }
        }

        public void ServerRead(IReadMessage msg, Client sender)
        {
            UInt16 currentLocIndex  = msg.ReadUInt16();
            UInt16 selectedLocIndex = msg.ReadUInt16();

            byte selectedMissionCount = msg.ReadByte();
            List<int> selectedMissionIndices = new List<int>();
            for (int i = 0; i < selectedMissionCount; i++)
            {
                selectedMissionIndices.Add(msg.ReadByte());
            }

            bool purchasedHullRepairs = msg.ReadBoolean();
            bool purchasedItemRepairs = msg.ReadBoolean();
            bool purchasedLostShuttles = msg.ReadBoolean();

            var buyCrateItems = ReadPurchasedItems(msg, sender);
            var subSellCrateItems = ReadPurchasedItems(msg, sender);
            var purchasedItems = ReadPurchasedItems(msg, sender);
            var soldItems = ReadSoldItems(msg);

            ushort purchasedUpgradeCount = msg.ReadUInt16();
            List<PurchasedUpgrade> purchasedUpgrades = new List<PurchasedUpgrade>();
            for (int i = 0; i < purchasedUpgradeCount; i++)
            {
                Identifier upgradeIdentifier = msg.ReadIdentifier();
                UpgradePrefab prefab = UpgradePrefab.Find(upgradeIdentifier);

                Identifier categoryIdentifier = msg.ReadIdentifier();
                UpgradeCategory category = UpgradeCategory.Find(categoryIdentifier);

                int upgradeLevel = msg.ReadByte();

                if (category == null || prefab == null) { continue; }
                purchasedUpgrades.Add(new PurchasedUpgrade(prefab, category, upgradeLevel));
            }

            ushort purchasedItemSwapCount = msg.ReadUInt16();
            List<PurchasedItemSwap> purchasedItemSwaps = new List<PurchasedItemSwap>();
            for (int i = 0; i < purchasedItemSwapCount; i++)
            {
                UInt16 itemToRemoveID = msg.ReadUInt16();
                Identifier itemToInstallIdentifier = msg.ReadIdentifier();
                ItemPrefab itemToInstall = itemToInstallIdentifier.IsEmpty ? null : ItemPrefab.Find(string.Empty, itemToInstallIdentifier);
                if (Entity.FindEntityByID(itemToRemoveID) is not Item itemToRemove) { continue; }
                purchasedItemSwaps.Add(new PurchasedItemSwap(itemToRemove, itemToInstall));
            }

            if (purchasedUpgradeCount > 0 || purchasedItemSwapCount > 0)
            {
                //if the client attempted to purchase something, increment flag regardless of whether the upgrades were actually purchased or not
                //so we can sync the correct state in case the client incorrectly assumed they can buy something (e.g. lost permissions just as they were purchasing)
                IncrementLastUpdateIdForFlag(NetFlags.UpgradeManager);
            }

            int hullRepairCost = GetHullRepairCost();
            int itemRepairCost = GetItemRepairCost();
            int shuttleRetrieveCost = CampaignMode.ShuttleReplaceCost;
            Location location = Map.CurrentLocation;
            if (location != null)
            {
                hullRepairCost = location.GetAdjustedMechanicalCost(hullRepairCost);
                itemRepairCost = location.GetAdjustedMechanicalCost(itemRepairCost);
                shuttleRetrieveCost = location.GetAdjustedMechanicalCost(shuttleRetrieveCost);
            }

            Wallet personalWallet = GetWallet(sender);
            personalWallet?.ForceUpdate();
            if (AllowedToManageWallets(sender))
            {
                Bank.ForceUpdate();
            }

            if (purchasedHullRepairs && !PurchasedHullRepairs)
            {
                if (GetBalance(sender) >= hullRepairCost)
                {
                    TryPurchase(sender, hullRepairCost);
                    PurchasedHullRepairs = true;
                    GameAnalyticsManager.AddMoneySpentEvent(hullRepairCost, GameAnalyticsManager.MoneySink.Service, "hullrepairs");
                }
            }

            if (purchasedItemRepairs && !PurchasedItemRepairs)
            {
                if (GetBalance(sender) >= itemRepairCost)
                {
                    TryPurchase(sender, itemRepairCost);
                    PurchasedItemRepairs = true;
                    GameAnalyticsManager.AddMoneySpentEvent(itemRepairCost, GameAnalyticsManager.MoneySink.Service, "devicerepairs");
                }
            }

            if (purchasedLostShuttles && !PurchasedLostShuttles)
            {
                if (GameMain.GameSession?.SubmarineInfo != null && GameMain.GameSession.SubmarineInfo.LeftBehindSubDockingPortOccupied)
                {
                    GameMain.Server.SendDirectChatMessage(TextManager.FormatServerMessage("ReplaceShuttleDockingPortOccupied"), sender, ChatMessageType.MessageBox);
                }
                else if (TryPurchase(sender, shuttleRetrieveCost))
                {
                    PurchasedLostShuttles = true;
                    GameAnalyticsManager.AddMoneySpentEvent(shuttleRetrieveCost, GameAnalyticsManager.MoneySink.Service, "retrieveshuttle");
                }
            }

            if (currentLocIndex < Map.Locations.Count && Map.AllowDebugTeleport)
            {
                Map.SetLocation(currentLocIndex);
            }

            if (AllowedToManageCampaign(sender, ClientPermissions.ManageMap))
            {
                Map.SelectLocation(selectedLocIndex == UInt16.MaxValue ? -1 : selectedLocIndex);
                if (Map.SelectedLocation == null) { Map.SelectRandomLocation(preferUndiscovered: true); }
                if (Map.SelectedConnection != null) { Map.SelectMission(selectedMissionIndices); }
                CheckTooManyMissions(Map.CurrentLocation, sender);
            }

            var prevBuyCrateItems = new Dictionary<Identifier, List<PurchasedItem>>();
            foreach (var kvp in CargoManager.ItemsInBuyCrate)
            {
                prevBuyCrateItems.Add(kvp.Key, new List<PurchasedItem>(kvp.Value));
            }
            foreach (var store in prevBuyCrateItems)
            {
                foreach (var item in store.Value.ToList())
                {
                    CargoManager.ModifyItemQuantityInBuyCrate(store.Key, item.ItemPrefab, -item.Quantity, sender);
                }
            }
            foreach (var store in buyCrateItems)
            {
                foreach (var item in store.Value.ToList())
                {
                    if (map?.CurrentLocation?.Stores == null || !map.CurrentLocation.Stores.ContainsKey(store.Key)) { continue; }
                    int availableQuantity = map.CurrentLocation.Stores[store.Key].Stock.Find(s => s.ItemPrefab == item.ItemPrefab)?.Quantity ?? 0;
                    int alreadyPurchasedQuantity = 
                        CargoManager.GetBuyCrateItem(store.Key, item.ItemPrefab)?.Quantity ?? 0 +
                        CargoManager.GetPurchasedItemCount(store.Key, item.ItemPrefab);
                    item.Quantity = MathHelper.Clamp(item.Quantity, 0, availableQuantity - alreadyPurchasedQuantity);
                    CargoManager.ModifyItemQuantityInBuyCrate(store.Key, item.ItemPrefab, item.Quantity, sender);
                }
            }

            var prevPurchasedItems = new Dictionary<Identifier, List<PurchasedItem>>();
            foreach (var kvp in CargoManager.PurchasedItems)
            {
                prevPurchasedItems.Add(kvp.Key, new List<PurchasedItem>(kvp.Value));
            }

            foreach (var storeId in purchasedItems.Keys)
            {
                DebugConsole.Log($"Purchased items ({storeId}):\n");
                if (prevPurchasedItems.TryGetValue(storeId, out var alreadyPurchased))
                {
                    var delivered = alreadyPurchased.Where(it => it.Delivered);
                    var notDelivered = alreadyPurchased.Where(it => !it.Delivered);
                    if (delivered.Any())
                    {
                        DebugConsole.Log($"  Already delivered:\n" + string.Concat(delivered.Select(it => $"    - {it.ItemPrefab.Name} (x{it.Quantity})")));
                    }
                    if (notDelivered.Any())
                    {
                        DebugConsole.Log($"  Already purchased:\n" + string.Concat(notDelivered.Where(it => !it.Delivered).Select(it => $"    - {it.ItemPrefab.Name} (x{it.Quantity})")));
                    }
                }
                DebugConsole.Log($"  New purchases:");
                foreach (var purchasedItem in purchasedItems[storeId])
                {
                    if (purchasedItem.Delivered) { continue; }
                    int quantity = purchasedItem.Quantity;
                    if (alreadyPurchased != null)
                    {
                        quantity -= alreadyPurchased.Where(it => it.DeliverImmediately == purchasedItem.DeliverImmediately && it.ItemPrefab == purchasedItem.ItemPrefab).Sum(it => it.Quantity);
                    }
                    if (quantity > 0)
                    {
                        DebugConsole.Log($"    - {purchasedItem.ItemPrefab.Name} (x{quantity})");
                    }
                }
            }
            foreach (var storeId in soldItems.Keys)
            {
                DebugConsole.Log($"Sold items:\n" + string.Concat(soldItems[storeId].Select(it => $" - {it.ItemPrefab.Name}")));
            }

            foreach (var kvp in purchasedItems)
            {
                var storeId = kvp.Key;
                var purchasedItemList = kvp.Value;
                foreach (var purchasedItem in purchasedItemList)
                {
                    int desiredQuantity = purchasedItem.Quantity;
                    if (prevPurchasedItems.TryGetValue(storeId, out var alreadyPurchasedList) &&
                        alreadyPurchasedList.FirstOrDefault(p => p.ItemPrefab == purchasedItem.ItemPrefab && p.DeliverImmediately == purchasedItem.DeliverImmediately) is { } alreadyPurchased)
                    {
                        desiredQuantity -= alreadyPurchased.Quantity;
                    }
                    int availableQuantity = map.CurrentLocation.Stores[storeId].Stock.Find(s => s.ItemPrefab == purchasedItem.ItemPrefab)?.Quantity ?? 0;
                    purchasedItem.Quantity = Math.Min(desiredQuantity, availableQuantity);
                }
                CargoManager.PurchaseItems(storeId, purchasedItemList, removeFromCrate: false, client: sender);
            }

            foreach (var (storeIdentifier, items) in CargoManager.PurchasedItems)
            {
                if (!prevPurchasedItems.ContainsKey(storeIdentifier))
                {
                    CargoManager.LogNewItemPurchases(storeIdentifier, items, sender);
                    continue;
                }

                List<PurchasedItem> newItems = new List<PurchasedItem>();
                List<PurchasedItem> prevItems = prevPurchasedItems[storeIdentifier];

                foreach (PurchasedItem item in items)
                {
                    PurchasedItem matching = prevItems.FirstOrDefault(ppi => ppi.ItemPrefab == item.ItemPrefab);
                    if (matching is null)
                    {
                        newItems.Add(item);
                        continue;
                    }
                    if (matching.Quantity < item.Quantity)
                    {
                        newItems.Add(new PurchasedItem(item.ItemPrefab, item.Quantity - matching.Quantity, sender));
                    }
                }

                if (newItems.Any())
                {
                    CargoManager.LogNewItemPurchases(storeIdentifier, newItems, sender);
                }
            }


            bool allowedToSellSubItems = AllowedToManageCampaign(sender, ClientPermissions.SellSubItems);
            if (allowedToSellSubItems)
            {
                var prevSubSellCrateItems = new Dictionary<Identifier, List<PurchasedItem>>(CargoManager.ItemsInSellFromSubCrate);
                foreach (var store in prevSubSellCrateItems)
                {
                    foreach (var item in store.Value.ToList())
                    {
                        CargoManager.ModifyItemQuantityInSubSellCrate(store.Key, item.ItemPrefab, -item.Quantity, sender);
                    }
                }
                foreach (var store in subSellCrateItems)
                {
                    foreach (var item in store.Value.ToList())
                    {
                        CargoManager.ModifyItemQuantityInSubSellCrate(store.Key, item.ItemPrefab, item.Quantity, sender);
                    }
                }
            }

            bool allowedToSellInventoryItems = AllowedToManageCampaign(sender, ClientPermissions.SellInventoryItems);
            if (allowedToSellInventoryItems && allowedToSellSubItems)
            {
                // for some reason CargoManager.SoldItem is never cleared by the server, I've added a check to SellItems that ignores all
                // sold items that are removed so they should be discarded on the next message
                var prevSoldItems = new Dictionary<Identifier, List<SoldItem>>(CargoManager.SoldItems);
                foreach (var store in prevSoldItems)
                {
                    CargoManager.BuyBackSoldItems(store.Key, store.Value.ToList(), sender);
                }
                foreach (var store in soldItems)
                {
                    CargoManager.SellItems(store.Key, store.Value.ToList(), sender);
                }
            }
            else if (allowedToSellInventoryItems || allowedToSellSubItems)
            {
                var prevSoldItems = new Dictionary<Identifier, List<SoldItem>>(CargoManager.SoldItems);
                foreach (var store in prevSoldItems)
                {
                    store.Value.RemoveAll(predicate);
                    CargoManager.BuyBackSoldItems(store.Key, store.Value.ToList(), sender);
                }
                foreach (var store in soldItems)
                {
                    store.Value.RemoveAll(predicate);
                }
                foreach (var store in soldItems)
                {
                    CargoManager.SellItems(store.Key, store.Value.ToList(), sender);
                }
                bool predicate(SoldItem i) => allowedToSellInventoryItems != (i.Origin == SoldItem.SellOrigin.Character);
            }

            var characterList = GameSession.GetSessionCrewCharacters(CharacterType.Both);
            foreach (var (prefab, category, _) in purchasedUpgrades)
            {
                UpgradeManager.TryPurchaseUpgrade(prefab, category, client: sender);

                // unstable logging
                int price = prefab.Price.GetBuyPrice(prefab, UpgradeManager.GetUpgradeLevel(prefab, category), Map?.CurrentLocation, characterList);
                int level = UpgradeManager.GetUpgradeLevel(prefab, category);
                GameServer.Log($"SERVER: Purchased level {level} {category.Identifier}.{prefab.Identifier} for {price}", ServerLog.MessageType.ServerMessage);
            }
            foreach (var purchasedItemSwap in purchasedItemSwaps)
            {
                if (purchasedItemSwap.ItemToInstall == null)
                {
                    UpgradeManager.CancelItemSwap(purchasedItemSwap.ItemToRemove, client: sender);
                }
                else
                {
                    UpgradeManager.PurchaseItemSwap(purchasedItemSwap.ItemToRemove, purchasedItemSwap.ItemToInstall, client: sender);
                }
            }
            foreach (Item item in Item.ItemList)
            {
                if (item.PendingItemSwap != null && !purchasedItemSwaps.Any(it => it.ItemToRemove == item))
                {
                    UpgradeManager.CancelItemSwap(item);
                    item.PendingItemSwap = null;
                }
            }
        }

        public void ServerReadMoney(IReadMessage msg, Client sender)
        {
            NetWalletTransfer transfer = INetSerializableStruct.Read<NetWalletTransfer>(msg);

            if (GameMain.Server is null) { return; }

            if (transfer.Sender.TryUnwrap(out var id))
            {
                if (id != sender.CharacterID && !AllowedToManageWallets(sender)) { return; }

                Wallet wallet = GetWalletByID(id);
                if (wallet is InvalidWallet) { return; }

                TransferMoney(wallet);
            }
            else
            {
                if (!AllowedToManageWallets(sender))
                {
                    if (transfer.Receiver.TryUnwrap(out var receiverId) && receiverId == sender.CharacterID)
                    {
                        if (transfer.Amount > GameMain.Server.ServerSettings.MaximumMoneyTransferRequest) { return; }
                        GameMain.Server.Voting.StartTransferVote(sender, null, transfer.Amount, sender);
                        GameServer.Log($"{sender.Name} started a vote to transfer {transfer.Amount} mk from the bank.", ServerLog.MessageType.Money);
                    }
                    return;
                }

                TransferMoney(Bank);
            }

            void TransferMoney(Wallet from)
            {
                if (!from.TryDeduct(transfer.Amount)) { return; }

                if (transfer.Receiver.TryUnwrap(out var id))
                {
                    Wallet wallet = GetWalletByID(id);
                    if (wallet is InvalidWallet) { return; }

                    wallet.Give(transfer.Amount);
                    GameServer.Log($"{sender.Name} transferred {transfer.Amount} mk to {wallet.GetOwnerLogName()} from {from.GetOwnerLogName()}.", ServerLog.MessageType.Money);
                }
                else
                {
                    Bank.Give(transfer.Amount);
                    GameServer.Log($"{sender.Name} transferred {transfer.Amount} mk to {Bank.GetOwnerLogName()} from {from.GetOwnerLogName()}.", ServerLog.MessageType.Money);
                }
            }

            Wallet GetWalletByID(ushort id)
            {
                Character targetCharacter = Character.CharacterList.FirstOrDefault(c => c.ID == id);
                return targetCharacter is null ? Wallet.Invalid : targetCharacter.Wallet;
            }
        }

        public void ServerReadRewardDistribution(IReadMessage msg, Client sender)
        {
            NetWalletSetSalaryUpdate update = INetSerializableStruct.Read<NetWalletSetSalaryUpdate>(msg);

            if (!AllowedToManageWallets(sender)) { return; }

            if (update.Target.TryUnwrap(out ushort id))
            {
                Character targetCharacter = Character.CharacterList.FirstOrDefault(c => c.ID == id);
                targetCharacter?.Wallet.SetRewardDistribution(update.NewRewardDistribution);
                GameServer.Log($"{sender.Name} changed the salary of {targetCharacter?.Name} to {update.NewRewardDistribution}%.", ServerLog.MessageType.Money);
                return;
            }

            Bank.SetRewardDistribution(update.NewRewardDistribution);
            GameServer.Log($"{sender.Name} changed the default salary to {update.NewRewardDistribution}%.", ServerLog.MessageType.Money);
        }

        public void ResetSalaries(Client sender)
        {
            if (!AllowedToManageWallets(sender)) { return; }

            foreach (Character character in GameSession.GetSessionCrewCharacters(CharacterType.Player))
            {
                character.Wallet.SetRewardDistribution(Bank.RewardDistribution);
            }
        }

        public void ServerReadCrew(IReadMessage msg, Client sender)
        {
            UInt16[] pendingHires = null;
            bool[] pendingToReserveBench = null;
            Dictionary<int, BotStatus> existingBotsClient = null;

            bool updatePending = msg.ReadBoolean();
            if (updatePending)
            {
                ushort pendingHireLength = msg.ReadUInt16();
                pendingHires = new UInt16[pendingHireLength];
                pendingToReserveBench = new bool[pendingHireLength];
                for (int i = 0; i < pendingHireLength; i++)
                {
                    pendingHires[i] = msg.ReadUInt16();
                    pendingToReserveBench[i] = msg.ReadBoolean();
                }
            }
            
            bool validateHires = msg.ReadBoolean();

            bool renameCharacter = msg.ReadBoolean();
            UInt16 renamedIdentifier = 0;
            string newName = null;
            bool existingCrewMember = false;
            if (renameCharacter)
            {
                renamedIdentifier = msg.ReadUInt16();
                newName = Client.SanitizeName(msg.ReadString());
                existingCrewMember = msg.ReadBoolean();
                if (!GameMain.Server.IsNameValid(sender, newName, clientRenamingSelf: renamedIdentifier == sender.CharacterInfo?.ID))
                {
                    renameCharacter = false;
                }
            }

            bool fireCharacter = msg.ReadBoolean();
            int firedIdentifier = -1;
            if (fireCharacter) { firedIdentifier = msg.ReadUInt16(); }

            Location location = map?.CurrentLocation;
            CharacterInfo firedCharacter = null;
            (ushort id, string newName) appliedRename = (Entity.NullEntityID, string.Empty);

            if (location != null)
            {
                if (fireCharacter && AllowedToManageCampaign(sender, ClientPermissions.ManageHires))
                {
                    firedCharacter = CrewManager.GetCharacterInfos(includeReserveBench: true).FirstOrDefault(info => info.ID == firedIdentifier);
                    if (firedCharacter != null && (firedCharacter.Character?.IsBot ?? true))
                    {
                        CrewManager.FireCharacter(firedCharacter);
                    }
                    else
                    {
                        DebugConsole.ThrowError($"Tried to fire an invalid character ({firedIdentifier})");
                    }
                }

                if (renameCharacter)
                {
                    CharacterInfo characterInfo = null;
                    if (AllowedToManageCampaign(sender, ClientPermissions.ManageHires))
                    {
                        if (existingCrewMember && CrewManager != null)
                        {
                            characterInfo = CrewManager.GetCharacterInfos(includeReserveBench: true).FirstOrDefault(info => info.ID == renamedIdentifier);
                        }
                        else if (!existingCrewMember && location.HireManager != null)
                        {
                            characterInfo = location.HireManager.AvailableCharacters.FirstOrDefault(info => info.ID == renamedIdentifier);
                        }
                    }
                    if (characterInfo == null && renamedIdentifier == sender.CharacterInfo?.ID)
                    {
                        characterInfo = sender.CharacterInfo;
                    }
                    if (characterInfo != null &&
                        (characterInfo.Character == null || characterInfo.Character is { IsBot: true } || (characterInfo.RenamingEnabled && characterInfo == sender.CharacterInfo)))
                    {
                        GameServer.Log($"{sender.Name} renamed the character \"{characterInfo.Name}\" as \"{newName}\".", ServerLog.MessageType.ServerMessage);
                        if (existingCrewMember)
                        {
                            CrewManager.RenameCharacter(characterInfo, newName);
                            if (characterInfo == sender.CharacterInfo)
                            {
                                //renaming is only allowed once
                                characterInfo.RenamingEnabled = false;
                            }
                        }
                        else
                        {
                            location.HireManager.RenameCharacter(characterInfo, newName);
                        }
                        appliedRename = (characterInfo.ID, newName);
                    }
                    else
                    {
                        string errorMsg = $"Tried to rename an invalid character ({renamedIdentifier}, {characterInfo?.Name ?? "null"})";
                        DebugConsole.ThrowError(errorMsg);
                        GameMain.Server?.SendConsoleMessage(errorMsg, sender, Color.Red);
                    }
                }

                if (location.HireManager != null)
                {
                    if (validateHires)
                    {
                        foreach (CharacterInfo hireInfo in location.HireManager.PendingHires)
                        {
                            TryHireCharacter(location, hireInfo, client: sender);
                        }
                    }

                    if (updatePending)
                    {
                        List<CharacterInfo> pendingHireInfos = new List<CharacterInfo>();
                        int i = 0;
                        foreach (UInt16 identifier in pendingHires)
                        {
                            CharacterInfo match = location.GetHireableCharacters().FirstOrDefault(info => info.ID == identifier);
                            if (match == null)
                            {
                                DebugConsole.ThrowError($"Tried to add a character that doesn't exist ({identifier}) to pending hires");
                                continue;
                            }
                            
                            match.BotStatus = pendingToReserveBench[i++] ? BotStatus.PendingHireToReserveBench : BotStatus.PendingHireToActiveService;
                            if (match.BotStatus == BotStatus.PendingHireToActiveService)
                            {
                                //can't add more bots to active service is max has been reached
                                if (pendingHireInfos.Count(ci => ci.BotStatus == BotStatus.PendingHireToActiveService) + CrewManager.GetCharacterInfos().Count() >= CrewManager.MaxCrewSize) { continue; } 
                            }

                            pendingHireInfos.Add(match);
                        }
                        location.HireManager.PendingHires = pendingHireInfos;
                    }

                    location.HireManager.AvailableCharacters.ForEachMod(info =>
                    {
                        if (!location.HireManager.PendingHires.Contains(info))
                        {
                            location.HireManager.RenameCharacter(info, info.OriginalName);
                        }
                    });
                }
            }

            // bounce back
            if (renameCharacter && existingCrewMember)
            {
                SendCrewState(appliedRename, firedCharacter);
            }
            else
            {
                SendCrewState(firedCharacter: firedCharacter);
            }
        }

        /// <summary>
        /// Notifies the clients of the current bot situation like syncing pending and available hires
        /// </summary>
        /// <param name="hiredCharacters">Inform the clients that these characters have been hired.</param>
        /// <param name="firedCharacter">Inform the clients that this character has been fired.</param>
        /// <remarks>
        /// It might be obsolete to sync available hires. I found that the available hires are always the same between
        /// the client and the server when there's only one person on the server but when a second person joins both of
        /// their available hires are different from the server.
        /// </remarks>
        public void SendCrewState((ushort id, string newName) renamedCrewMember = default, CharacterInfo firedCharacter = null, bool createNotification = true)
        {
            List<CharacterInfo> availableHires = new List<CharacterInfo>();
            List<CharacterInfo> pendingHires = new List<CharacterInfo>();

            if (map.CurrentLocation != null && map.CurrentLocation.Type.HasHireableCharacters)
            {
                availableHires = map.CurrentLocation.GetHireableCharacters().ToList();
                pendingHires = map.CurrentLocation?.HireManager.PendingHires;
            }

            foreach (Client client in GameMain.Server.ConnectedClients)
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ServerPacketHeader.CREW);

                msg.WriteBoolean(createNotification);

                msg.WriteUInt16((ushort)availableHires.Count);
                foreach (CharacterInfo hire in availableHires)
                {
                    hire.ServerWrite(msg);
                    msg.WriteInt32(hire.Salary);
                }

                msg.WriteUInt16((ushort)pendingHires.Count);
                foreach (CharacterInfo pendingHire in pendingHires)
                {
                    msg.WriteUInt16(pendingHire.ID);
                    msg.WriteBoolean(pendingHire.BotStatus == BotStatus.PendingHireToReserveBench);
                }

                var crewManager = CrewManager.GetCharacterInfos();
                msg.WriteUInt16((ushort)crewManager.Count());
                foreach (CharacterInfo info in crewManager)
                {
                    info.ServerWrite(msg);
                }
                
                var reserveBench = CrewManager.GetReserveBenchInfos();
                msg.WriteUInt16((ushort)reserveBench.Count());
                foreach (CharacterInfo info in reserveBench)
                {
                    info.ServerWrite(msg);
                }

                bool validRenaming = renamedCrewMember.id > 0 && !string.IsNullOrEmpty(renamedCrewMember.newName);
                msg.WriteBoolean(validRenaming);
                if (validRenaming)
                {
                    msg.WriteUInt16(renamedCrewMember.id);
                    msg.WriteString(renamedCrewMember.newName);
                }

                msg.WriteBoolean(firedCharacter != null);
                if (firedCharacter != null) { msg.WriteUInt16(firedCharacter.ID); }

                GameMain.Server.ServerPeer.Send(msg, client.Connection, DeliveryMethod.Reliable);
            }
        }

        public override bool TryPurchase(Client client, int price)
        {
            //disconnected clients can never purchase anything
            //(can happen e.g. if someone starts a vote to buy something and then disconnects)
            if (client != null && !GameMain.Server.ConnectedClients.Contains(client)) { return false; }

            if (price == 0) { return true; }

            Wallet wallet = GetWallet(client);
            if (!AllowedToManageWallets(client))
            {
                return wallet.TryDeduct(price);
            }

            int balance = wallet.Balance;

            if (balance >= price)
            {
                return wallet.TryDeduct(price);
            }

            if (balance + Bank.Balance >= price)
            {
                int remainder = price - balance;
                if (balance > 0) { wallet.Deduct(balance); }
                Bank.Deduct(remainder);
                return true ;
            }

            return false;
        }

        public override int GetBalance(Client client = null)
        {
            if (client is null) { return 0; }

            Wallet wallet = GetWallet(client);
            if (!AllowedToManageWallets(client))
            {
                return wallet.Balance;
            }

            return wallet.Balance + Bank.Balance;
        }

        /// <summary>
        /// Serializes the campaign and character data to XML.
        /// </summary>
        /// <param name="element">Game session element to save the campaign data to.</param>
        /// <param name="isSavingOnLoading">
        /// Whether the save is being done during loading to ensure the campaign ID matches the one in the save file.
        /// Used to work around some quirks with the backup save system.
        /// See: <see cref="SaveUtil.SaveGame(CampaignDataPath,bool)"/>
        /// </param>
        public override void Save(XElement element, bool isSavingOnLoading)
        {
            element.Add(new XAttribute("campaignid", CampaignID));
            XElement modeElement = new XElement("MultiPlayerCampaign",
                new XAttribute("purchasedlostshuttles", PurchasedLostShuttlesInLatestSave),
                new XAttribute("purchasedhullrepairs", PurchasedHullRepairsInLatestSave),
                new XAttribute("purchaseditemrepairs", PurchasedItemRepairsInLatestSave),
                new XAttribute("cheatsenabled", CheatsEnabled));

            DebugConsole.NewMessage("Saved PurchasedHullRepairs: "+ PurchasedHullRepairs+" (in last save "+PurchasedHullRepairsInLatestSave+")", Color.Magenta);

            modeElement.Add(Settings.Save());
            modeElement.Add(SaveStats());
            if (GameMain.Server?.TraitorManager is TraitorManager traitorManager)
            {
                modeElement.Add(traitorManager.Save());
            }
            modeElement.Add(Bank.Save());

            if (GameMain.GameSession?.EventManager != null)
            {
                modeElement.Add(GameMain.GameSession?.EventManager.Save());
            }

            foreach (Identifier unlockedRecipe in GameMain.GameSession.UnlockedRecipes)
            {
                modeElement.Add(new XElement("unlockedrecipe", new XAttribute("identifier", unlockedRecipe)));
            }

            CampaignMetadata?.Save(modeElement);
            Map.Save(modeElement);
            CargoManager?.SavePurchasedItems(modeElement);
            UpgradeManager?.Save(modeElement);

            if (petsElement != null)
            {
                modeElement.Add(petsElement);
            }

            // save bots
            var crewManagerElement = CrewManager.SaveMultiplayer(modeElement);
            if (ActiveOrdersElement != null)
            {
                crewManagerElement.Add(ActiveOrdersElement);
            }

            XElement savedExperiencePointsElement = new XElement("SavedExperiencePoints");
            foreach (var savedExperiencePoint in savedExperiencePoints)
            {
                savedExperiencePointsElement.Add(new XElement("Point",
                    new XAttribute("accountid", savedExperiencePoint.AccountId.TryUnwrap(out var accountId) ? accountId.StringRepresentation : ""),
                    new XAttribute("address", savedExperiencePoint.Address.StringRepresentation),
                    new XAttribute("points", savedExperiencePoint.ExperiencePoints)));
            }

            element.Add(modeElement);

            // save character data to a separate file

            // When loading a campaign in multiplayer, we save the campaign to ensure the campaign ID that gets assigned
            // matches the one in the save file, this is a problem with the backup save system since this causes the
            // character data to save too, and we don't want to overwrite the main save file's character data.
            // So we instead save over the load path in this case, which in backup saves is the backup file
            // which we don't mind getting overriden since the data should be the same
            string characterDataPath = isSavingOnLoading
                                           ? GetCharacterDataPathForLoading()
                                           : GetCharacterDataPathForSaving();
            XDocument characterDataDoc = new XDocument(new XElement("CharacterData"));
            foreach (CharacterCampaignData cd in characterData)
            {
                characterDataDoc.Root.Add(cd.Save());
            }
            try
            {
                SaveUtil.DeleteIfExists(characterDataPath);
                characterDataDoc.SaveSafe(characterDataPath);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Saving multiplayer campaign characters to \"" + characterDataPath + "\" failed!", e);
            }

            lastSaveID++;
            DebugConsole.Log("Campaign saved, save ID " + lastSaveID);
        }

        /// <summary>
        /// Load the current character save file and add/replace a single character's data with a new version immediately.
        /// </summary>
        /// <param name="newData">New character to insert. If it matches one existing in the save, that will get replaced.</param>
        /// <param name="skipBackup">By default, replaced characters will be temporarily backed up, but that might be unwanted
        /// eg. when using this method to save a character itself restored from the backup.</param>
        public void SaveSingleCharacter(CharacterCampaignData newData, bool skipBackup = false)
        {
            string characterDataPath = GetCharacterDataPathForSaving();
            if (!File.Exists(characterDataPath))
            {
                DebugConsole.ThrowError($"Failed to load the character data for the campaign. Could not find the file \"{characterDataPath}\".");
            }
            else
            {
                var loadedCharacterData = XMLExtensions.TryLoadXml(characterDataPath);
                if (loadedCharacterData?.Root == null) { return; }
                var oldData = loadedCharacterData.Root.Elements()
                    .FirstOrDefault(subElement => new CharacterCampaignData(subElement).IsDuplicate(newData));
                
                if (oldData != null)
                {
                    if (!skipBackup)
                    {
                        replacedCharacterDataBackup.Add(new CharacterCampaignData(oldData));    
                    }
                    oldData.Remove();
                }
                loadedCharacterData.Root.Add(newData.Save());
                
                try
                {
                    loadedCharacterData.SaveSafe(characterDataPath);
                }
                catch (Exception e)
                {
                    DebugConsole.ThrowError("Saving multiplayer campaign characters to \"" + characterDataPath + "\" failed!", e);
                }
            }
        }
        
        public CharacterCampaignData RestoreSingleCharacterFromBackup(Client client)
        {
            if (replacedCharacterDataBackup.Find(cd => cd.MatchesClient(client)) is CharacterCampaignData characterToRestore)
            {
                replacedCharacterDataBackup.Remove(characterToRestore);
                return characterToRestore;
            }
            
            return default;
        }
    }
}
