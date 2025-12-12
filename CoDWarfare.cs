using Oxide.Game.Rust.Cui;
using Oxide.Core.Plugins;
using Oxide.Core;
using UnityEngine;
using System.Collections.Generic;
using Rust;
using Facepunch;
using System.Linq;
using Newtonsoft.Json;
using System; 

namespace Oxide.Plugins
{
    [Info("CoDWarfare", "YourName", "12.2.2")]
    [Description("Final Stable Build: Full HUD + Store + Cache + Game Logic")]
    public class CoDWarfare : RustPlugin
    {
        [PluginReference] Plugin ImageLibrary;

        // --- UI CONSTANTS ---
        private const string LayerMain = "Overlay"; 
        private const string UI_HUD = "CoD_Right";    
        private const string UI_Center = "CoD_Center"; 
        private const string UI_LobbyBar = "CoD_LobbyBar";
        private const string UI_Store = "CoD_Store";
        private const string UI_Health = "CoD_Health"; 
        private const string HitmarkerUI = "HitmarkerUI";
        private const string UI_Lobby = "CoD_Lobby";

        // --- CONFIGURATION ---
        private PluginConfig config;

        class PluginConfig
        {
            public int MinPlayersToStart = 2;
            public float LobbyDuration = 20f;
            public float KillCardDuration = 5.0f;
            public string ServerBannerUrl = "https://i.imgur.com/3p6fGgD.jpg";
            public string DefaultCardUrl = "https://i.imgur.com/3p6fGgD.jpg"; 
            public string EditorUrl = "http://159.89.137.231:3000/index.html"; 
            public List<CallingCardDefinition> StoreCards = new List<CallingCardDefinition>();
        }

        class CallingCardDefinition
        {
            public string Name;
            public string Url;
            public int Price;
        }

        protected override void LoadDefaultConfig()
        {
            config = new PluginConfig();
            config.StoreCards = new List<CallingCardDefinition>
            {
                new CallingCardDefinition { Name = "Default", Url = "https://i.imgur.com/3p6fGgD.jpg", Price = 0 },
                new CallingCardDefinition { Name = "Red Camo", Url = "https://i.imgur.com/qL0gyFq.png", Price = 50 },
                new CallingCardDefinition { Name = "Gold", Url = "https://i.imgur.com/5tFm0hW.png", Price = 500 },
                new CallingCardDefinition { Name = "Skull Ops", Url = "https://i.imgur.com/4S5vXhM.png", Price = 100 }
            };
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { config = Config.ReadObject<PluginConfig>(); if (config == null) LoadDefaultConfig(); }
            catch { LoadDefaultConfig(); }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        // --- DATA ---
        private enum GameState { Lobby, Match, EndGame }
        private GameState CurrentState = GameState.Lobby;
        
        private Dictionary<string, List<Vector3>> ArenaSpawns = new Dictionary<string, List<Vector3>>();
        private string CurrentMap = "Nuketown"; 
        private List<string> AvailableMaps = new List<string> { "Nuketown", "Shipment", "Rust" };
        private Dictionary<ulong, string> MapVotes = new Dictionary<ulong, string>();
        private const string UI_MapVote = "CoD_MapVote";
        
        private HashSet<ulong> LobbyQueue = new HashSet<ulong>(); 
        private Dictionary<ulong, int> PlayerLevel = new Dictionary<ulong, int>();
        private Dictionary<ulong, float> nextFireTime = new Dictionary<ulong, float>();
        
        // Cached item definitions to avoid repeated lookups
        private ItemDefinition syringeItemDef;
        private ItemDefinition grenadeItemDef;
        
        public class PlayerStoreData
        {
            public string EquippedCardUrl;
            // List stores history of all uploaded cards
            public List<string> SavedEmblems = new List<string>(); 
            public List<string> UnlockedCards = new List<string> { "Default" };
            public int Credits = 0;
        }
        private Dictionary<ulong, PlayerStoreData> StoreData = new Dictionary<ulong, PlayerStoreData>();

        private List<string> WeaponLadder = new List<string>
        {
            "lmg.m249", "rifle.ak", "rifle.lr300", "smg.mp5", "smg.thompson", 
            "shotgun.pump", "shotgun.spas12", "pistol.python", "pistol.revolver",
            "crossbow", "bow.compound", "pistol.eoka", "knife.combat"
        };

        // --- HOOKS ---

        void OnServerInitialized()
        {
            LoadData();
            foreach (var p in BasePlayer.activePlayerList) DestroyAllUI(p); 
            StartLobby();
            
            // Cache item definitions for syringe and grenade
            syringeItemDef = ItemManager.FindItemDefinition("syringe.medical");
            grenadeItemDef = ItemManager.FindItemDefinition("grenade.f1");
            
            // PRE-LOAD ICONS: Add item icons to ImageLibrary from Rust's CDN
            timer.Once(2f, () => {
                Puts("[CoDWarfare] Pre-loading item icons into ImageLibrary...");
                
                // Load weapon icons using item definition to get proper icon URL
                foreach(var weapon in WeaponLadder) 
                {
                    var itemDef = ItemManager.FindItemDefinition(weapon);
                    if (itemDef != null)
                    {
                        // Use Rust's CDN URL pattern for item icons
                        string iconUrl = $"https://rustlabs.com/img/items180/{weapon}.png";
                        ImageLibrary?.Call("AddImage", iconUrl, weapon, 0UL);
                        Puts($"[CoDWarfare] Loading icon for {weapon}");
                    }
                }
                
                // Load tactical and lethal icons
                ImageLibrary?.Call("AddImage", "https://rustlabs.com/img/items180/syringe.medical.png", "syringe.medical", 0UL);
                ImageLibrary?.Call("AddImage", "https://rustlabs.com/img/items180/grenade.f1.png", "grenade.f1", 0UL);
                
                Puts("[CoDWarfare] Item icons pre-load complete. Icons will be available after download.");
            });
        }

        void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList) DestroyAllUI(p);
            SaveData();
        }

        // --- COMMANDS ---

        [ChatCommand("emblem")]
        void CmdEmblem(BasePlayer player)
        {
            string url = $"{config.EditorUrl}?id={player.UserIDString}";
            player.ChatMessage($"<color=#ce422b><b>[EMBLEM EDITOR]</b></color>");
            player.ChatMessage($"Click here to open the editor: <color=#4caf50><a href='{url}'><b>[OPEN EDITOR]</b></a></color>");
        }

        [ConsoleCommand("emblem.update")]
        void ConsoleEmblemUpdate(ConsoleSystem.Arg arg)
        {
            // Security Check
            if (arg.Connection != null) return;
            if (arg.Args == null || arg.Args.Length < 2) return;

            string steamIdStr = arg.GetString(0);
            string originalUrl = arg.GetString(1);
            ulong steamId;

            if (ulong.TryParse(steamIdStr, out steamId))
            {
                // Cache Buster: Unique URL prevents game from using old cached image
                string uniqueUrl = $"{originalUrl}?v={DateTime.Now.Ticks}";
                var data = GetPlayerData(steamId);
                
                // Add to history instead of overwriting
                if (!data.SavedEmblems.Contains(uniqueUrl))
                {
                    data.SavedEmblems.Add(uniqueUrl);
                }
                
                // Auto-equip newest
                data.EquippedCardUrl = uniqueUrl; 
                
                // Force Download
                ImageLibrary?.Call("AddImage", uniqueUrl, uniqueUrl, 0UL);
                SaveData();

                var player = BasePlayer.FindByID(steamId);
                if (player != null && player.IsConnected)
                {
                    player.ChatMessage("<color=#ce422b><b>[CoD]</b></color> New emblem saved to 'MY CARDS'!");
                    // Refresh Store UI if open
                    OpenStoreUI(player, "owned");
                }
                Puts($"[CoDWarfare] Emblem updated for {steamId}");
            }
        }

        [ChatCommand("store")]
        void CmdStore(BasePlayer player) => OpenStoreUI(player, "store");

        [ConsoleCommand("cod.storetab")]
        void ConsoleStoreTab(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            string tab = arg.GetString(0);
            OpenStoreUI(player, tab);
        }

        [ConsoleCommand("cod.equipurl")]
        void ConsoleEquipUrl(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            // Get all args and join them (handles URLs with spaces/special chars)
            string url = string.Join(" ", arg.Args ?? new string[0]);
            if (string.IsNullOrEmpty(url)) return;
            
            var data = GetPlayerData(player.userID);
            data.EquippedCardUrl = url;
            SaveData();
            player.ChatMessage("Card Equipped!");
            OpenStoreUI(player, "owned");
        }

        // New command: Equip by index for custom emblems (avoids URL parsing issues)
        [ConsoleCommand("cod.equipemblem")]
        void ConsoleEquipEmblem(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            int index = arg.GetInt(0, -1);
            if (index < 0) return;
            
            var data = GetPlayerData(player.userID);
            if (index < data.SavedEmblems.Count)
            {
                data.EquippedCardUrl = data.SavedEmblems[index];
                SaveData();
                player.ChatMessage("Custom Emblem Equipped!");
                OpenStoreUI(player, "owned");
            }
        }

        // New command: Equip store card by name
        [ConsoleCommand("cod.equipcard")]
        void ConsoleEquipCard(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            string cardName = arg.GetString(0);
            if (string.IsNullOrEmpty(cardName)) return;
            
            var data = GetPlayerData(player.userID);
            var card = config.StoreCards.FirstOrDefault(x => x.Name == cardName);
            
            if (card != null && data.UnlockedCards.Contains(cardName))
            {
                data.EquippedCardUrl = card.Url;
                SaveData();
                player.ChatMessage($"Equipped '{cardName}'!");
                OpenStoreUI(player, "owned");
            }
        }

        [ConsoleCommand("cod.buycard")]
        void ConsoleBuyCard(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            string cardName = arg.GetString(0);
            var data = GetPlayerData(player.userID);
            var card = config.StoreCards.FirstOrDefault(x => x.Name == cardName);
            if (card == null) return;

            if (data.UnlockedCards.Contains(cardName))
            {
                data.EquippedCardUrl = card.Url;
                player.ChatMessage($"Equipped '{cardName}'!");
                SaveData();
                OpenStoreUI(player, "store");
                return;
            }

            if (data.Credits >= card.Price)
            {
                data.Credits -= card.Price;
                data.UnlockedCards.Add(cardName);
                data.EquippedCardUrl = card.Url; 
                player.ChatMessage($"Purchased '{cardName}'!");
                SaveData();
                OpenStoreUI(player, "store");
            }
            else player.ChatMessage($"Not enough credits! Need {card.Price}.");
        }

        [ChatCommand("join")]
        void CmdJoin(BasePlayer player)
        {
            if (CurrentState == GameState.Match) { player.ChatMessage("Match in progress!"); return; }
            if (!LobbyQueue.Contains(player.userID))
            {
                LobbyQueue.Add(player.userID);
                player.ChatMessage("Joined Queue! Waiting for players...");
                DrawLobbyBar(player);
                CheckLobbyStart();
            }
        }

        [ChatCommand("leave")]
        void CmdLeave(BasePlayer player)
        {
            if (LobbyQueue.Contains(player.userID))
            {
                LobbyQueue.Remove(player.userID);
                player.ChatMessage("Left Queue.");
                DestroyAllUI(player);
            }
        }

        [ChatCommand("cod.addspawn")]
        void CmdAddSpawn(BasePlayer player, string cmd, string[] args) 
        { 
            if (player.IsAdmin && args.Length > 0) 
            { 
                string map = args[0]; 
                if (!ArenaSpawns.ContainsKey(map)) ArenaSpawns[map] = new List<Vector3>(); 
                ArenaSpawns[map].Add(player.transform.position); 
                SaveData(); 
                player.ChatMessage($"Spawn added to {map}"); 
            } 
        }

        // --- MAP VOTE SYSTEM ---
        [ChatCommand("mapvote")]
        void CmdMapVote(BasePlayer player)
        {
            if (CurrentState != GameState.Lobby)
            {
                player.ChatMessage("Map voting is only available in the lobby!");
                return;
            }
            ShowMapVoteUI(player);
        }

        [ConsoleCommand("cod.votemap")]
        void ConsoleVoteMap(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            string mapName = arg.GetString(0);
            if (string.IsNullOrEmpty(mapName)) return;
            
            if (!AvailableMaps.Contains(mapName))
            {
                player.ChatMessage($"Invalid map: {mapName}");
                return;
            }
            
            MapVotes[player.userID] = mapName;
            player.ChatMessage($"<color=#ce422b>[CoD]</color> You voted for <color=#FFD700>{mapName}</color>!");
            
            // Refresh UI for all players in lobby
            foreach (var uid in LobbyQueue)
            {
                var p = BasePlayer.FindByID(uid);
                if (p != null) ShowMapVoteUI(p);
            }
        }

        [ConsoleCommand("cod.closemapvote")]
        void ConsoleCloseMapVote(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) CuiHelper.DestroyUi(player, UI_MapVote);
        }

        void ShowMapVoteUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UI_MapVote);
            var container = new CuiElementContainer();

            // Main panel
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0.1 0.1 0.1 0.95" }, 
                RectTransform = { AnchorMin = "0.3 0.3", AnchorMax = "0.7 0.7" }, 
                CursorEnabled = true 
            }, LayerMain, UI_MapVote);

            // Title
            container.Add(new CuiLabel 
            { 
                Text = { Text = "MAP VOTE", FontSize = 24, Align = TextAnchor.MiddleCenter, Color = "1 0.8 0 1" }, 
                RectTransform = { AnchorMin = "0 0.85", AnchorMax = "1 0.98" } 
            }, UI_MapVote);

            // Close button
            container.Add(new CuiButton 
            { 
                Button = { Command = "cod.closemapvote", Color = "0.8 0.2 0.2 1" }, 
                RectTransform = { AnchorMin = "0.9 0.88", AnchorMax = "0.98 0.98" }, 
                Text = { Text = "X", FontSize = 16, Align = TextAnchor.MiddleCenter } 
            }, UI_MapVote);

            // Count votes for each map
            Dictionary<string, int> voteCounts = new Dictionary<string, int>();
            foreach (var map in AvailableMaps) voteCounts[map] = 0;
            foreach (var vote in MapVotes.Values)
            {
                if (voteCounts.ContainsKey(vote)) voteCounts[vote]++;
            }

            // Display maps
            float buttonWidth = 0.28f;
            float startX = 0.05f;
            float gap = 0.05f;
            
            for (int i = 0; i < AvailableMaps.Count && i < 3; i++)
            {
                string mapName = AvailableMaps[i];
                float xMin = startX + (i * (buttonWidth + gap));
                int votes = voteCounts[mapName];
                bool hasVoted = MapVotes.ContainsKey(player.userID) && MapVotes[player.userID] == mapName;
                string btnColor = hasVoted ? "0.2 0.8 0.2 1" : "0.4 0.4 0.4 1";
                
                // Map name button
                container.Add(new CuiButton 
                { 
                    Button = { Command = $"cod.votemap {mapName}", Color = btnColor }, 
                    RectTransform = { AnchorMin = $"{xMin} 0.4", AnchorMax = $"{xMin + buttonWidth} 0.75" }, 
                    Text = { Text = $"{mapName.ToUpper()}\n\n<size=14>Votes: {votes}</size>", FontSize = 16, Align = TextAnchor.MiddleCenter } 
                }, UI_MapVote);
            }

            // Current map indicator
            container.Add(new CuiLabel 
            { 
                Text = { Text = $"Current Map: <color=#FFD700>{CurrentMap}</color>", FontSize = 14, Align = TextAnchor.MiddleCenter }, 
                RectTransform = { AnchorMin = "0 0.1", AnchorMax = "1 0.25" } 
            }, UI_MapVote);

            CuiHelper.AddUi(player, container);
        }

        string GetWinningMap()
        {
            if (MapVotes.Count == 0) return CurrentMap;
            
            Dictionary<string, int> voteCounts = new Dictionary<string, int>();
            foreach (var map in AvailableMaps) voteCounts[map] = 0;
            foreach (var vote in MapVotes.Values)
            {
                if (voteCounts.ContainsKey(vote)) voteCounts[vote]++;
            }
            
            string winner = CurrentMap;
            int maxVotes = 0;
            foreach (var kvp in voteCounts)
            {
                if (kvp.Value > maxVotes)
                {
                    maxVotes = kvp.Value;
                    winner = kvp.Key;
                }
            }
            return winner;
        }

        // --- NEW STORE UI ---
        void OpenStoreUI(BasePlayer player, string currentTab)
        {
            CuiHelper.DestroyUi(player, UI_Store);
            var container = new CuiElementContainer();
            var data = GetPlayerData(player.userID);

            container.Add(new CuiPanel { Image = { Color = "0.1 0.1 0.1 0.98" }, RectTransform = { AnchorMin = "0.1 0.1", AnchorMax = "0.9 0.9" }, CursorEnabled = true }, LayerMain, UI_Store);
            container.Add(new CuiLabel { Text = { Text = $"STORE | CREDITS: {data.Credits}", FontSize = 24, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.05 0.9", AnchorMax = "0.6 0.98" } }, UI_Store);
            container.Add(new CuiButton { Button = { Close = UI_Store, Color = "0.8 0.2 0.2 1" }, RectTransform = { AnchorMin = "0.92 0.92", AnchorMax = "0.98 0.98" }, Text = { Text = "X", FontSize = 18, Align = TextAnchor.MiddleCenter } }, UI_Store);

            string colStore = currentTab == "store" ? "0.8 0.4 0.2 1" : "0.3 0.3 0.3 1";
            string colOwned = currentTab == "owned" ? "0.8 0.4 0.2 1" : "0.3 0.3 0.3 1";
            
            container.Add(new CuiButton { Button = { Command = "cod.storetab store", Color = colStore }, RectTransform = { AnchorMin = "0.3 0.82", AnchorMax = "0.45 0.88" }, Text = { Text = "BROWSE STORE", FontSize = 14, Align = TextAnchor.MiddleCenter } }, UI_Store);
            container.Add(new CuiButton { Button = { Command = "cod.storetab owned", Color = colOwned }, RectTransform = { AnchorMin = "0.46 0.82", AnchorMax = "0.61 0.88" }, Text = { Text = "MY CARDS", FontSize = 14, Align = TextAnchor.MiddleCenter } }, UI_Store);

            int columns = 3;
            float width = 0.28f; float height = 0.2f;
            float startX = 0.05f; float startY = 0.75f; 
            float gapX = 0.02f; float gapY = 0.05f;

            // Build display list with metadata for command generation
            List<(string Name, string Url, int Price, bool IsCustom, int CustomIndex)> displayItems = new List<(string, string, int, bool, int)>();
            
            if (currentTab == "store") 
            {
                foreach(var card in config.StoreCards)
                {
                    displayItems.Add((card.Name, card.Url, card.Price, false, -1));
                }
            }
            else 
            {
                // Reverse loop for newest uploaded first - track original index
                for(int k = data.SavedEmblems.Count - 1; k >= 0; k--)
                {
                    displayItems.Add(($"CUSTOM #{k+1}", data.SavedEmblems[k], 0, true, k));
                }
                // Add unlocked store cards
                foreach(var name in data.UnlockedCards)
                {
                    var confItem = config.StoreCards.FirstOrDefault(x => x.Name == name);
                    if (confItem != null) displayItems.Add((confItem.Name, confItem.Url, confItem.Price, false, -1));
                }
            }

            for (int i = 0; i < displayItems.Count; i++)
            {
                var card = displayItems[i];
                int row = i / columns; int col = i % columns;
                float xMin = startX + (col * (width + gapX));
                float yMax = startY - (row * (height + gapY));
                
                if (yMax < 0.1) break; 

                string imgId = (string)ImageLibrary?.Call("GetImage", card.Url);
                var imgComp = new CuiRawImageComponent();
                if (!string.IsNullOrEmpty(imgId)) imgComp.Png = imgId; else imgComp.Url = card.Url; 

                container.Add(new CuiElement { Parent = UI_Store, Components = { imgComp, new CuiRectTransformComponent { AnchorMin = $"{xMin} {yMax - height}", AnchorMax = $"{xMin + width} {yMax}" } } });

                bool equipped = data.EquippedCardUrl == card.Url;
                string btnColor = equipped ? "0.2 0.8 0.2 1" : "0.5 0.5 0.5 1";
                string btnText = equipped ? "EQUIPPED" : "EQUIP";
                string cmd = "";
                
                if (currentTab == "store")
                {
                    bool unlocked = data.UnlockedCards.Contains(card.Name);
                    if (!unlocked) 
                    { 
                        btnColor = "0.8 0.4 0.2 1"; 
                        btnText = $"BUY {card.Price}"; 
                        cmd = $"cod.buycard {card.Name}"; 
                    }
                    else 
                    {
                        // Use equipcard command with card name
                        cmd = $"cod.equipcard {card.Name}";
                    }
                }
                else 
                {
                    // MY CARDS tab - use index-based commands
                    if (card.IsCustom)
                    {
                        // Custom emblem - use index
                        cmd = $"cod.equipemblem {card.CustomIndex}";
                    }
                    else
                    {
                        // Store card in owned list - use card name
                        cmd = $"cod.equipcard {card.Name}";
                    }
                }

                container.Add(new CuiButton { Button = { Command = cmd, Color = btnColor }, RectTransform = { AnchorMin = $"{xMin} {yMax - height - 0.05f}", AnchorMax = $"{xMin + width} {yMax - height}" }, Text = { Text = btnText, FontSize = 12, Align = TextAnchor.MiddleCenter } }, UI_Store);
            }
            CuiHelper.AddUi(player, container);
        }

        // --- GAME LOGIC ---

        void StartLobby()
        {
            CurrentState = GameState.Lobby;
            LobbyQueue.Clear();
            MapVotes.Clear();
            PrintToChat("LOBBY IS OPEN! Type /join to enter the queue.");
            PrintToChat("Type /mapvote to vote for the next map!");
        }

        void CheckLobbyStart()
        {
            if (CurrentState == GameState.Lobby && LobbyQueue.Count >= config.MinPlayersToStart)
            {
                timer.Once(5f, StartMatch);
                foreach (var uid in LobbyQueue)
                {
                    var p = BasePlayer.FindByID(uid);
                    if (p != null) p.ChatMessage("<color=green>MATCH STARTING IN 5 SECONDS!</color>");
                }
            }
        }

        void StartMatch()
        {
            CurrentState = GameState.Match;
            
            // Apply map vote result
            CurrentMap = GetWinningMap();
            PrintToChat($"<color=#ce422b>[CoD]</color> Map selected: <color=#FFD700>{CurrentMap}</color>");
            MapVotes.Clear(); // Reset votes for next round
            
            foreach (var uid in LobbyQueue)
            {
                var p = BasePlayer.FindByID(uid);
                if (p != null)
                {
                    CuiHelper.DestroyUi(p, UI_LobbyBar);
                    CuiHelper.DestroyUi(p, UI_MapVote);
                    PlayerLevel[p.userID] = 0;
                    RespawnPlayer(p);
                }
            }
        }

        void EndMatch(BasePlayer winner)
        {
            CurrentState = GameState.EndGame;
            var data = GetPlayerData(winner.userID);
            data.Credits += 100;
            SaveData();
            foreach (var uid in LobbyQueue)
            {
                var p = BasePlayer.FindByID(uid);
                if (p != null)
                {
                    p.inventory.Strip();
                    DestroyAllUI(p);
                    DrawLobbyUI(p, $"WINNER: {winner.displayName.ToUpper()}");
                }
            }
            LobbyQueue.Clear();
            timer.Once(10f, () => { foreach(var p in BasePlayer.activePlayerList) DestroyAllUI(p); StartLobby(); });
        }

        void RespawnPlayer(BasePlayer player)
        {
            if (ArenaSpawns.ContainsKey(CurrentMap) && ArenaSpawns[CurrentMap].Count > 0)
            {
                var spawns = ArenaSpawns[CurrentMap];
                TeleportTo(player, spawns[UnityEngine.Random.Range(0, spawns.Count)]);
            }
            player.Heal(100);
            player.metabolism.calories.value = 500;
            GiveCurrentWeapon(player);
            DrawCenterBanner(player);
            DrawGameHUD(player);
        }

        void GiveCurrentWeapon(BasePlayer player)
        {
            player.inventory.Strip();
            int level = PlayerLevel.ContainsKey(player.userID) ? PlayerLevel[player.userID] : 0;
            if (level >= WeaponLadder.Count) level = WeaponLadder.Count - 1;

            string gunShortname = WeaponLadder[level];
            Item gun = ItemManager.CreateByName(gunShortname, 1);
            BaseProjectile projectile = gun.GetHeldEntity() as BaseProjectile;

            if (IsHitscanWeapon(gunShortname))
            {
                if (projectile != null) projectile.primaryMagazine.contents = 0;
            }
            else
            {
                if (projectile != null)
                {
                    projectile.primaryMagazine.contents = projectile.primaryMagazine.capacity;
                    player.inventory.GiveItem(ItemManager.CreateByName(projectile.primaryMagazine.ammoType.shortname, 60));
                }
            }
            
            player.inventory.GiveItem(gun, player.inventory.containerBelt);
            player.inventory.GiveItem(ItemManager.CreateByName("syringe.medical", 1), player.inventory.containerBelt);
            player.inventory.GiveItem(ItemManager.CreateByName("grenade.f1", 1), player.inventory.containerBelt);

            NextTick(() => DrawGameHUD(player));
        }

        void OnWeaponFired(BaseProjectile projectile, BasePlayer player, ItemModProjectile mod, ProtoBuf.ProjectileShoot projectiles)
        {
            if (CurrentState == GameState.Match) NextTick(() => DrawGameHUD(player));
        }
        
        void OnReloadWeapon(BasePlayer player, BaseProjectile projectile)
        {
             if (CurrentState == GameState.Match) timer.Once(projectile.reloadTime + 0.1f, () => DrawGameHUD(player));
        }

        // Update HUD when player switches active item (weapon switching)
        void OnPlayerActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (CurrentState == GameState.Match && player != null)
            {
                NextTick(() => DrawGameHUD(player));
            }
        }

        // Ensure HUD updates when items are used (consumables/throwables)
        void OnItemUse(Item item, int amountToUse)
        {
            var player = item.GetOwnerPlayer();
            if (player != null && CurrentState == GameState.Match) NextTick(() => DrawGameHUD(player));
        }

        void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (CurrentState != GameState.Match) return;
            BasePlayer victim = entity as BasePlayer;
            if (victim == null) return;
            BasePlayer killer = info?.Initiator as BasePlayer;

            if (killer != null)
            {
                string weaponName = killer.GetActiveItem()?.info.displayName.translated ?? "UNKNOWN";
                ShowKillCard(victim, killer, weaponName);
            }
            timer.Once(config.KillCardDuration, () => { if (victim != null && !victim.IsConnected) return; victim.Respawn(); RespawnPlayer(victim); });

            if (killer != null && killer != victim)
            {
                int killerLevel = PlayerLevel[killer.userID];
                if (killerLevel >= WeaponLadder.Count - 1) { EndMatch(killer); return; }
                PlayerLevel[killer.userID]++;
                var kData = GetPlayerData(killer.userID);
                kData.Credits += 10;
                Effect.server.Run("assets/bundled/prefabs/fx/minigames/chippy/chippy_payout.prefab", killer.transform.position); 
                GiveCurrentWeapon(killer); 
                DrawGameHUD(killer);
            }
        }

        // --- PROFESSIONAL HUD (With Dynamic Icons) ---
        void DrawGameHUD(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UI_HUD);
            var container = new CuiElementContainer();

            // 1. Black Background
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0 0 0 0.85" }, 
                RectTransform = { AnchorMin = "0.78 0.02", AnchorMax = "0.99 0.13" } 
            }, LayerMain, UI_HUD);
            
            // Get the PRIMARY weapon from belt slot 0 (where weapon is always placed)
            // This ensures we always show the correct weapon icon regardless of what player is holding
            var weaponItem = player.inventory.containerBelt?.GetSlot(0);
            var activeItem = player.GetActiveItem();
            
            // Use weapon item for icon/name, but check active item for ammo display when holding it
            string weaponShortname = weaponItem?.info.shortname ?? "rifle.ak";
            string displayName = weaponItem?.info.displayName.translated ?? "AK47";
            
            // Get the currently held item for ammo calculation (might be weapon, syringe, or grenade)
            Item itemForAmmo = activeItem;
            if (itemForAmmo == null || !WeaponLadder.Contains(itemForAmmo.info.shortname))
            {
                itemForAmmo = weaponItem; // Fall back to primary weapon
            }
            
            string ammoText = "-- | --";
            if (itemForAmmo != null && WeaponLadder.Contains(itemForAmmo.info.shortname))
            {
                string ammoItemShortname = itemForAmmo.info.shortname;
                var proj = itemForAmmo.GetHeldEntity() as BaseProjectile;
                
                // Check if this is a hitscan weapon (infinite ammo mode)
                if (IsHitscanWeapon(ammoItemShortname))
                {
                    ammoText = "<color=#FFD700>∞</color> <size=14>| ∞</size>";
                }
                else if (proj != null && proj.primaryMagazine != null)
                {
                    int clip = proj.primaryMagazine.contents;
                    int reserve = player.inventory.GetAmount(proj.primaryMagazine.ammoType.itemid);
                    ammoText = $"{clip} <size=14>| {reserve}</size>";
                }
                else
                {
                    // Non-projectile weapons (melee, bow, etc.)
                    var melee = itemForAmmo.GetHeldEntity() as BaseMelee;
                    if (melee != null)
                    {
                        ammoText = "<color=#FFD700>MELEE</color>";
                    }
                    else if (ammoItemShortname.Contains("bow") || ammoItemShortname.Contains("crossbow"))
                    {
                        // Check for bow/crossbow - they have their own ammo system
                        int arrows = player.inventory.GetAmount(ItemManager.FindItemDefinition("arrow.wooden")?.itemid ?? 0);
                        int arrowsHV = player.inventory.GetAmount(ItemManager.FindItemDefinition("arrow.hv")?.itemid ?? 0);
                        int arrowsBone = player.inventory.GetAmount(ItemManager.FindItemDefinition("arrow.bone")?.itemid ?? 0);
                        ammoText = $"{arrows + arrowsHV + arrowsBone} <size=14>arrows</size>";
                    }
                }
            }

            // 2. Weapon Icon - Always show primary weapon from belt slot 0
            string gunIconId = (string)ImageLibrary?.Call("GetImage", weaponShortname, 0UL);
            if (string.IsNullOrEmpty(gunIconId))
            {
                gunIconId = (string)ImageLibrary?.Call("GetImage", weaponShortname);
            }
            
            if (!string.IsNullOrEmpty(gunIconId)) 
            {
                // ImageLibrary has the icon - use PNG
                container.Add(new CuiElement 
                { 
                    Parent = UI_HUD, 
                    Components = { 
                        new CuiRawImageComponent { Png = gunIconId }, 
                        new CuiRectTransformComponent { AnchorMin = "0.05 0.2", AnchorMax = "0.35 0.9" } 
                    } 
                });
            }
            else
            {
                // Fallback: Use direct URL to Rust item icons
                string iconUrl = $"https://rustlabs.com/img/items180/{weaponShortname}.png";
                container.Add(new CuiElement 
                { 
                    Parent = UI_HUD, 
                    Components = { 
                        new CuiRawImageComponent { Url = iconUrl }, 
                        new CuiRectTransformComponent { AnchorMin = "0.05 0.2", AnchorMax = "0.35 0.9" } 
                    } 
                });
            }

            // 3. Ammo Count
            container.Add(new CuiLabel 
            { 
                Text = { Text = ammoText, FontSize = 28, Align = TextAnchor.MiddleRight, Font="robotocondensed-bold.ttf" }, 
                RectTransform = { AnchorMin = "0.4 0.4", AnchorMax = "0.95 0.9" } 
            }, UI_HUD);

            // 4. Weapon Name
            container.Add(new CuiLabel 
            { 
                Text = { Text = displayName.ToUpper(), FontSize = 10, Align = TextAnchor.MiddleRight, Color = "0.7 0.7 0.7 1" }, 
                RectTransform = { AnchorMin = "0.4 0.35", AnchorMax = "0.95 0.5" } 
            }, UI_HUD);

            // 5. Tactical Icon (Syringe) - Dynamic based on inventory
            // Use cached item definition to avoid repeated lookups
            int syringeCount = syringeItemDef != null ? player.inventory.GetAmount(syringeItemDef.itemid) : 0;
            if (syringeCount > 0) 
            {
                string tacId = (string)ImageLibrary?.Call("GetImage", "syringe.medical", 0UL);
                if (string.IsNullOrEmpty(tacId)) tacId = (string)ImageLibrary?.Call("GetImage", "syringe.medical");
                
                if (!string.IsNullOrEmpty(tacId))
                {
                    container.Add(new CuiElement { Parent = UI_HUD, Components = { new CuiRawImageComponent { Png = tacId, Color = "1 1 1 0.8" }, new CuiRectTransformComponent { AnchorMin = "0.40 0.05", AnchorMax = "0.50 0.35" } } });
                }
                else
                {
                    // Fallback to URL
                    container.Add(new CuiElement { Parent = UI_HUD, Components = { new CuiRawImageComponent { Url = "https://rustlabs.com/img/items180/syringe.medical.png", Color = "1 1 1 0.8" }, new CuiRectTransformComponent { AnchorMin = "0.40 0.05", AnchorMax = "0.50 0.35" } } });
                }
            }
            
            // 6. Lethal Icon (Grenade) - Dynamic based on inventory
            // Use cached item definition to avoid repeated lookups
            int grenadeCount = grenadeItemDef != null ? player.inventory.GetAmount(grenadeItemDef.itemid) : 0;
            if (grenadeCount > 0) 
            {
                string letId = (string)ImageLibrary?.Call("GetImage", "grenade.f1", 0UL);
                if (string.IsNullOrEmpty(letId)) letId = (string)ImageLibrary?.Call("GetImage", "grenade.f1");
                
                if (!string.IsNullOrEmpty(letId))
                {
                    container.Add(new CuiElement { Parent = UI_HUD, Components = { new CuiRawImageComponent { Png = letId, Color = "1 1 1 0.8" }, new CuiRectTransformComponent { AnchorMin = "0.52 0.05", AnchorMax = "0.62 0.35" } } });
                }
                else
                {
                    // Fallback to URL
                    container.Add(new CuiElement { Parent = UI_HUD, Components = { new CuiRawImageComponent { Url = "https://rustlabs.com/img/items180/grenade.f1.png", Color = "1 1 1 0.8" }, new CuiRectTransformComponent { AnchorMin = "0.52 0.05", AnchorMax = "0.62 0.35" } } });
                }
            }

            // 7. Level Counter
            int level = PlayerLevel.ContainsKey(player.userID) ? PlayerLevel[player.userID] + 1 : 1;
            int max = WeaponLadder.Count;
            container.Add(new CuiLabel 
            { 
                Text = { Text = $"LVL {level}/{max}", FontSize = 10, Align = TextAnchor.MiddleRight, Color = "1 0.8 0 1" }, 
                RectTransform = { AnchorMin = "0.7 0.05", AnchorMax = "0.95 0.3" } 
            }, UI_HUD);

            CuiHelper.AddUi(player, container);
        }

        // (Other Helpers Unchanged)
        void ShowKillCard(BasePlayer victim, BasePlayer killer, string weaponName)
        {
            CuiHelper.DestroyUi(victim, UI_Center);
            var container = new CuiElementContainer();
            
            var killerData = GetPlayerData(killer.userID);
            string bgUrl = string.IsNullOrEmpty(killerData.EquippedCardUrl) ? config.DefaultCardUrl : killerData.EquippedCardUrl;

            container.Add(new CuiPanel { Image = { Color = "0 0 0 1.0" }, RectTransform = { AnchorMin = "0.28 0.0", AnchorMax = "0.72 0.14" } }, LayerMain, UI_Center);
            
            string imgId = (string)ImageLibrary?.Call("GetImage", bgUrl);
            var imgComp = new CuiRawImageComponent();
            if (!string.IsNullOrEmpty(imgId)) imgComp.Png = imgId; else imgComp.Url = bgUrl;

            container.Add(new CuiElement { Parent = UI_Center, Components = { imgComp, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" } } });
            
            string avatarUrl = $"https://avatars.rust.link/avatar/{killer.UserIDString}";
            container.Add(new CuiElement { Parent = UI_Center, Components = { new CuiRawImageComponent { Url = avatarUrl }, new CuiRectTransformComponent { AnchorMin = "0.02 0.1", AnchorMax = "0.15 0.9" } } });

            container.Add(new CuiLabel { Text = { Text = $"KILLED BY: <color=#FFD700>{killer.displayName.ToUpper()}</color>", FontSize = 18, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.18 0.5", AnchorMax = "0.8 0.9" } }, UI_Center);
            container.Add(new CuiLabel { Text = { Text = $"WEAPON: {weaponName.ToUpper()}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.8 0.8 0.8 1" }, RectTransform = { AnchorMin = "0.18 0.1", AnchorMax = "0.8 0.5" } }, UI_Center);
            
            CuiHelper.AddUi(victim, container);
            timer.Once(config.KillCardDuration, () => { if (victim != null) DrawCenterBanner(victim); });
        }

        void DestroyAllUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UI_HUD);
            CuiHelper.DestroyUi(player, UI_Center);
            CuiHelper.DestroyUi(player, UI_LobbyBar);
            CuiHelper.DestroyUi(player, UI_Store);
            CuiHelper.DestroyUi(player, UI_Health);
            CuiHelper.DestroyUi(player, HitmarkerUI);
            CuiHelper.DestroyUi(player, UI_Lobby);
            CuiHelper.DestroyUi(player, UI_MapVote);
        }

        void DrawCenterBanner(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UI_Center);
            var container = new CuiElementContainer();
            container.Add(new CuiPanel { Image = { Color = "0 0 0 1.0" }, RectTransform = { AnchorMin = "0.28 0.0", AnchorMax = "0.72 0.14" }, CursorEnabled = false }, LayerMain, UI_Center);
            container.Add(new CuiElement { Parent = UI_Center, Components = { new CuiRawImageComponent { Url = config.ServerBannerUrl }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" } } });
            CuiHelper.AddUi(player, container);
        }

        void DrawLobbyBar(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UI_LobbyBar);
            var container = new CuiElementContainer();
            container.Add(new CuiPanel { Image = { Color = "0 0.5 0 0.8" }, RectTransform = { AnchorMin = "0.3 0.95", AnchorMax = "0.7 0.99" }, CursorEnabled = false }, LayerMain, UI_LobbyBar);
            container.Add(new CuiLabel { Text = { Text = "IN QUEUE - MOVING FREELY - WAITING FOR PLAYERS...", FontSize = 14, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" } }, UI_LobbyBar);
            CuiHelper.AddUi(player, container);
        }

        void DrawLobbyUI(BasePlayer player, string statusText)
        {
            CuiHelper.DestroyUi(player, UI_Lobby);
            var container = new CuiElementContainer();
            container.Add(new CuiPanel { Image = { Color = "0 0 0 0.8" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, CursorEnabled = true }, LayerMain, UI_Lobby);
            container.Add(new CuiLabel { Text = { Text = statusText, FontSize = 40, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0 0.5", AnchorMax = "1 0.6" } }, UI_Lobby);
            CuiHelper.AddUi(player, container);
        }
        
        void ShowHitmarker(BasePlayer player)
        {
            string hitUI = "HitmarkerUI";
            CuiHelper.DestroyUi(player, hitUI);
            var container = new CuiElementContainer();
            container.Add(new CuiLabel { Text = { Text = "X", FontSize = 24, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.8" }, RectTransform = { AnchorMin = "0.49 0.49", AnchorMax = "0.51 0.51" }, FadeOut = 0.1f }, LayerMain, hitUI);
            CuiHelper.AddUi(player, container);
            timer.Once(0.15f, () => CuiHelper.DestroyUi(player, hitUI));
        }

        PlayerStoreData GetPlayerData(ulong uid)
        {
            if (!StoreData.ContainsKey(uid)) {
                StoreData[uid] = new PlayerStoreData { EquippedCardUrl = config.DefaultCardUrl };
            }
            return StoreData[uid];
        }

        bool IsHitscanWeapon(string shortname)
        {
            if (shortname.Contains("rifle") || shortname.Contains("lmg") || shortname.Contains("smg")) return true;
            if (shortname.Contains("pistol") && shortname != "pistol.eoka") return true;
            return false;
        }

        void TeleportTo(BasePlayer player, Vector3 pos)
        {
            if (player.IsConnected && !player.IsDead())
            {
                player.MovePosition(pos);
                player.ClientRPCPlayer(null, player, "ForcePositionTo", pos);
                player.SendNetworkUpdate();
            }
        }

        void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (CurrentState != GameState.Match) return;
            if (input.IsDown(BUTTON.FIRE_PRIMARY))
            {
                var gun = player.GetActiveItem()?.GetHeldEntity() as BaseProjectile;
                if (gun != null && IsHitscanWeapon(gun.GetItem().info.shortname))
                {
                    if (nextFireTime.ContainsKey(player.userID) && Time.time < nextFireTime[player.userID]) return;
                    nextFireTime[player.userID] = Time.time + gun.repeatDelay;
                    Effect.server.Run("assets/bundled/prefabs/fx/muzzleflash/assaultrifle.prefab", gun, StringPool.Get("muzzle"), Vector3.zero, Vector3.forward);
                    Ray ray = player.eyes.HeadRay();
                    RaycastHit hit;
                    if (Physics.Raycast(ray, out hit, 300f, LayerMask.GetMask("Construction", "Terrain", "Player (Server)", "World")))
                    {
                        var victim = hit.GetEntity() as BasePlayer;
                        if (victim != null)
                        {
                            float dmg = 30f; if (gun.ShortPrefabName.Contains("sniper")) dmg = 100f;
                            victim.OnAttacked(new HitInfo(player, victim, Rust.DamageType.Bullet, dmg, hit.point));
                            Effect.server.Run("assets/bundled/prefabs/fx/minigames/chippy/chippy_encounter.prefab", player.transform.position);
                            ShowHitmarker(player);
                        }
                        else Effect.server.Run("assets/bundled/prefabs/fx/impacts/concrete/concrete-impact-bullet-1.prefab", hit.point);
                    }
                }
            }
        }

        class StoredData 
        { 
            public Dictionary<string, List<Vector3>> Arenas = new Dictionary<string, List<Vector3>>(); 
            public Dictionary<ulong, PlayerStoreData> Players = new Dictionary<ulong, PlayerStoreData>();
        }
        void SaveData() { Interface.Oxide.DataFileSystem.WriteObject("CoDWarfare", new StoredData { Arenas = ArenaSpawns, Players = StoreData }); }
        void LoadData() { var data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>("CoDWarfare"); if (data != null) { ArenaSpawns = data.Arenas; StoreData = data.Players; } }
    }
}