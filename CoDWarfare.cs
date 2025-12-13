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
    [Info("CoDWarfare", "YourName", "12.3.0")]
    [Description("Optimized Build: Pooled UI, Batched Updates, Scoreboard, Pagination")]
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
        private const string UI_Scoreboard = "CoD_Scoreboard";

        // --- UI LAYOUT CONSTANTS (extracted magic numbers) ---
        private const int STORE_COLUMNS = 4;
        private const int STORE_ROWS = 2;
        private const int STORE_ITEMS_PER_PAGE = STORE_COLUMNS * STORE_ROWS;
        private const float STORE_CARD_WIDTH = 0.22f;
        private const float STORE_CARD_HEIGHT = 0.32f;
        private const float STORE_GRID_START_X = 0.02f;
        private const float STORE_GRID_START_Y = 0.76f;
        private const float STORE_GRID_GAP_X = 0.02f;
        private const float STORE_GRID_GAP_Y = 0.03f;
        
        private const float HUD_UPDATE_BATCH_DELAY = 0.1f;
        private const float KILL_CREDITS = 10f;
        private const float WIN_CREDITS = 100f;
        private const float HITSCAN_DAMAGE = 30f;
        private const float SNIPER_DAMAGE = 100f;
        private const float HITSCAN_RANGE = 300f;

        // --- PERFORMANCE: UI Update Batching ---
        private Dictionary<ulong, Timer> pendingHUDUpdates = new Dictionary<ulong, Timer>();
        
        // --- PERFORMANCE: Player cache to avoid FindByID calls ---
        private Dictionary<ulong, BasePlayer> playerCache = new Dictionary<ulong, BasePlayer>();

        // --- CONFIGURATION ---
        private PluginConfig config;

        class PluginConfig
        {
            public int MinPlayersToStart = 2;
            public int MaxPlayersInQueue = 10;
            public float MapVoteDuration = 30f;
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
        private const string UI_Leaderboard = "CoD_Leaderboard";
        private bool MapVoteActive = false;
        private Timer mapVoteTimer = null;
        private Vector3? LobbySpawn = null;
        
        private HashSet<ulong> LobbyQueue = new HashSet<ulong>(); 
        private Dictionary<ulong, int> PlayerLevel = new Dictionary<ulong, int>();
        private Dictionary<ulong, float> nextFireTime = new Dictionary<ulong, float>();
        
        // Cached item definitions to avoid repeated lookups
        private ItemDefinition syringeItemDef;
        private ItemDefinition grenadeItemDef;
        private ItemDefinition arrowWoodenDef;
        private ItemDefinition arrowHVDef;
        private ItemDefinition arrowBoneDef;
        
        public class PlayerStoreData
        {
            public string EquippedCardUrl;
            // List stores history of all uploaded cards
            public List<string> SavedEmblems = new List<string>(); 
            public List<string> UnlockedCards = new List<string> { "Default" };
            public int Credits = 0;
            // Match statistics
            public int Kills = 0;
            public int Deaths = 0;
        }
        private Dictionary<ulong, PlayerStoreData> StoreData = new Dictionary<ulong, PlayerStoreData>();
        
        // Store pagination state
        private Dictionary<ulong, int> storePageIndex = new Dictionary<ulong, int>();

        private List<string> WeaponLadder = new List<string>
        {
            "lmg.m249", "rifle.ak", "rifle.lr300", "smg.mp5", "smg.thompson", 
            "shotgun.pump", "shotgun.spas12", "pistol.python", "pistol.revolver",
            "crossbow", "bow.compound", "pistol.eoka", "knife.combat"
        };
        
        // Per-weapon muzzle flash effects (excludes shotguns, bows, eoka - they use native effects)
        private Dictionary<string, string> WeaponMuzzleFlash = new Dictionary<string, string>
        {
            {"lmg.m249", "assets/bundled/prefabs/fx/muzzleflash/lmg.prefab"},
            {"rifle.ak", "assets/bundled/prefabs/fx/muzzleflash/assaultrifle.prefab"},
            {"rifle.lr300", "assets/bundled/prefabs/fx/muzzleflash/assaultrifle.prefab"},
            {"smg.mp5", "assets/bundled/prefabs/fx/muzzleflash/smg.prefab"},
            {"smg.thompson", "assets/bundled/prefabs/fx/muzzleflash/smg.prefab"},
            {"pistol.python", "assets/bundled/prefabs/fx/muzzleflash/pistol.prefab"},
            {"pistol.revolver", "assets/bundled/prefabs/fx/muzzleflash/pistol.prefab"}
        };
        
        // Per-weapon gun fire sounds (excludes shotguns, bows, eoka - they use native sounds)
        private Dictionary<string, string> WeaponFireSound = new Dictionary<string, string>
        {
            {"lmg.m249", "assets/prefabs/weapons/m249/effects/attack.prefab"},
            {"rifle.ak", "assets/prefabs/weapons/ak47u/effects/attack.prefab"},
            {"rifle.lr300", "assets/prefabs/weapons/lr300/effects/attack.prefab"},
            {"smg.mp5", "assets/prefabs/weapons/mp5/effects/attack.prefab"},
            {"smg.thompson", "assets/prefabs/weapons/thompson/effects/attack.prefab"},
            {"pistol.python", "assets/prefabs/weapons/python/effects/attack.prefab"},
            {"pistol.revolver", "assets/prefabs/weapons/revolver/effects/attack.prefab"}
        };
        
        // Cached bone ID for muzzle flash (performance optimization)
        private static uint MuzzleFlashBoneId;

        // --- HOOKS ---

        void OnServerInitialized()
        {
            LoadData();
            foreach (var p in BasePlayer.activePlayerList) 
            {
                DestroyAllUI(p);
                // Populate player cache
                playerCache[p.userID] = p;
            }
            StartLobby();
            
            // Cache item definitions for tactical, lethal, and arrows
            syringeItemDef = ItemManager.FindItemDefinition("syringe.medical");
            grenadeItemDef = ItemManager.FindItemDefinition("grenade.f1");
            arrowWoodenDef = ItemManager.FindItemDefinition("arrow.wooden");
            arrowHVDef = ItemManager.FindItemDefinition("arrow.hv");
            arrowBoneDef = ItemManager.FindItemDefinition("arrow.bone");
            
            // PRE-LOAD ICONS: Add item icons to ImageLibrary from Rust's CDN
            timer.Once(2f, () => {
                if (ImageLibrary == null)
                {
                    PrintWarning("[CoDWarfare] ImageLibrary not found! Icons will use URL fallback.");
                    return;
                }
                
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
        
        // Player connection hooks for cache management
        void OnPlayerConnected(BasePlayer player)
        {
            if (player != null)
                playerCache[player.userID] = player;
        }
        
        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player != null)
            {
                playerCache.Remove(player.userID);
                pendingHUDUpdates.Remove(player.userID);
                storePageIndex.Remove(player.userID);
            }
        }
        
        // Helper to get cached player (avoids expensive FindByID calls)
        BasePlayer GetCachedPlayer(ulong uid)
        {
            BasePlayer player;
            if (playerCache.TryGetValue(uid, out player) && player != null && player.IsConnected)
                return player;
            
            // Fallback to FindByID and update cache
            player = BasePlayer.FindByID(uid);
            if (player != null)
                playerCache[uid] = player;
            return player;
        }

        void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList) DestroyAllUI(p);
            
            // Clean up pending timers
            foreach (var kvp in pendingHUDUpdates)
            {
                kvp.Value?.Destroy();
            }
            pendingHUDUpdates.Clear();
            playerCache.Clear();
            
            SaveData();
        }

        // --- COMMANDS ---

        [ChatCommand("emblem")]
        void CmdEmblem(BasePlayer player)
        {
            string url = $"{config.EditorUrl}?id={player.UserIDString}";
            
            // Create a note item with the emblem editor link
            Item note = ItemManager.CreateByName("note", 1);
            if (note != null)
            {
                // Set the note text to the emblem editor URL
                note.text = $"=== EMBLEM EDITOR ===\n\nOpen this link in your browser:\n\n{url}\n\n(Copy and paste into your web browser)";
                note.name = "Emblem Editor Link";
                
                // Give the note to the player
                if (!player.inventory.GiveItem(note))
                {
                    // If inventory is full, drop it at their feet
                    note.Drop(player.transform.position, Vector3.up);
                    player.ChatMessage("<color=#ce422b>[CoD]</color> Inventory full! Note dropped at your feet.");
                }
                else
                {
                    player.ChatMessage("<color=#ce422b>[CoD]</color> <color=#4caf50>Emblem editor link given as a note!</color> Check your inventory and read the note for the URL.");
                }
            }
            else
            {
                // Fallback to chat message if note creation fails
                player.ChatMessage($"<color=#ce422b><b>[EMBLEM EDITOR]</b></color>");
                player.ChatMessage($"Click here to open the editor: <color=#4caf50><a href='{url}'><b>[OPEN EDITOR]</b></a></color>");
            }
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
            if (CurrentState == GameState.Match) 
            { 
                player.ChatMessage("<color=#ce422b>[CoD]</color> Match in progress! Wait for next round."); 
                return; 
            }
            
            if (LobbyQueue.Contains(player.userID))
            {
                player.ChatMessage("<color=#ce422b>[CoD]</color> You are already in the queue!");
                return;
            }
            
            if (LobbyQueue.Count >= config.MaxPlayersInQueue)
            {
                player.ChatMessage("<color=#ce422b>[CoD]</color> Queue is full!");
                return;
            }
            
            LobbyQueue.Add(player.userID);
            PrintToChat($"<color=#ce422b>[CoD]</color> <color=#FFD700>{player.displayName}</color> joined the queue! ({LobbyQueue.Count}/{config.MaxPlayersInQueue})");
            DrawLobbyBar(player);
            UpdateAllLobbyBars();
            CheckLobbyStart();
        }

        [ChatCommand("leave")]
        void CmdLeave(BasePlayer player)
        {
            if (LobbyQueue.Contains(player.userID))
            {
                LobbyQueue.Remove(player.userID);
                PrintToChat($"<color=#ce422b>[CoD]</color> <color=#FFD700>{player.displayName}</color> left the queue. ({LobbyQueue.Count}/{config.MaxPlayersInQueue})");
                DestroyAllUI(player);
                UpdateAllLobbyBars();
                
                // Teleport to lobby spawn if set
                if (LobbySpawn.HasValue)
                {
                    TeleportTo(player, LobbySpawn.Value);
                    player.ChatMessage("<color=#ce422b>[CoD]</color> Returned to lobby.");
                }
            }
        }
        
        [ChatCommand("cod.setlobbyspawn")]
        void CmdSetLobbySpawn(BasePlayer player, string cmd, string[] args)
        {
            if (!player.IsAdmin) return;
            LobbySpawn = player.transform.position;
            SaveData();
            player.ChatMessage($"<color=#ce422b>[CoD]</color> Lobby spawn set at your position!");
        }
        
        void UpdateAllLobbyBars()
        {
            foreach (var uid in LobbyQueue)
            {
                var p = GetCachedPlayer(uid);
                if (p != null) DrawLobbyBar(p);
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
        
        // --- SCOREBOARD SYSTEM ---
        [ChatCommand("scoreboard")]
        void CmdScoreboard(BasePlayer player)
        {
            if (CurrentState != GameState.Match)
            {
                player.ChatMessage("<color=#ce422b>[CoD]</color> Scoreboard is only available during a match!");
                return;
            }
            ShowScoreboard(player);
        }
        
        [ConsoleCommand("cod.closescoreboard")]
        void ConsoleCloseScoreboard(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) CuiHelper.DestroyUi(player, UI_Scoreboard);
        }
        
        void ShowScoreboard(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UI_Scoreboard);
            var container = new CuiElementContainer();
            
            // Main panel
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0.05 0.05 0.08 0.95" }, 
                RectTransform = { AnchorMin = "0.25 0.2", AnchorMax = "0.75 0.8" }, 
                CursorEnabled = true 
            }, LayerMain, UI_Scoreboard);
            
            // Header bar
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0.8 0.15 0.15 1" }, 
                RectTransform = { AnchorMin = "0 0.9", AnchorMax = "1 1" } 
            }, UI_Scoreboard, "ScoreHeader");
            
            container.Add(new CuiLabel 
            { 
                Text = { Text = $"🎮 SCOREBOARD - {CurrentMap.ToUpper()}", FontSize = 18, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }, 
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.9 1" } 
            }, "ScoreHeader");
            
            // Close button
            container.Add(new CuiButton 
            { 
                Button = { Command = "cod.closescoreboard", Color = "0.6 0.1 0.1 1" }, 
                RectTransform = { AnchorMin = "0.92 0.15", AnchorMax = "0.98 0.85" }, 
                Text = { Text = "✕", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" } 
            }, "ScoreHeader");
            
            // Column headers
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0.1 0.1 0.12 1" }, 
                RectTransform = { AnchorMin = "0 0.82", AnchorMax = "1 0.9" } 
            }, UI_Scoreboard, "ColHeaders");
            
            container.Add(new CuiLabel 
            { 
                Text = { Text = "PLAYER", FontSize = 12, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "0.7 0.7 0.7 1" }, 
                RectTransform = { AnchorMin = "0.05 0", AnchorMax = "0.4 1" } 
            }, "ColHeaders");
            
            container.Add(new CuiLabel 
            { 
                Text = { Text = "LVL", FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "0.7 0.7 0.7 1" }, 
                RectTransform = { AnchorMin = "0.4 0", AnchorMax = "0.52 1" } 
            }, "ColHeaders");
            
            container.Add(new CuiLabel 
            { 
                Text = { Text = "KILLS", FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "0.7 0.7 0.7 1" }, 
                RectTransform = { AnchorMin = "0.52 0", AnchorMax = "0.64 1" } 
            }, "ColHeaders");
            
            container.Add(new CuiLabel 
            { 
                Text = { Text = "DEATHS", FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "0.7 0.7 0.7 1" }, 
                RectTransform = { AnchorMin = "0.64 0", AnchorMax = "0.76 1" } 
            }, "ColHeaders");
            
            container.Add(new CuiLabel 
            { 
                Text = { Text = "K/D", FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "0.7 0.7 0.7 1" }, 
                RectTransform = { AnchorMin = "0.76 0", AnchorMax = "0.88 1" } 
            }, "ColHeaders");
            
            container.Add(new CuiLabel 
            { 
                Text = { Text = "SCORE", FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "0.7 0.7 0.7 1" }, 
                RectTransform = { AnchorMin = "0.88 0", AnchorMax = "1 1" } 
            }, "ColHeaders");
            
            // Build sorted player list using shared helper
            var playerStats = GetSortedPlayerStats();
            
            // Draw player rows
            float rowHeight = 0.08f;
            float startY = 0.8f;
            int maxRows = 10;
            
            for (int i = 0; i < playerStats.Count && i < maxRows; i++)
            {
                var ps = playerStats[i];
                float yMax = startY - (i * rowHeight);
                float yMin = yMax - rowHeight + 0.01f;
                
                string rowColor = ps.uid == player.userID ? "0.15 0.25 0.35 0.8" : (i % 2 == 0 ? "0.08 0.08 0.1 0.6" : "0.1 0.1 0.12 0.6");
                string rowPanel = $"Row_{i}";
                
                container.Add(new CuiPanel 
                { 
                    Image = { Color = rowColor }, 
                    RectTransform = { AnchorMin = $"0 {yMin}", AnchorMax = $"1 {yMax}" } 
                }, UI_Scoreboard, rowPanel);
                
                // Player name (with rank indicator)
                string rankIcon = i == 0 ? "🥇 " : (i == 1 ? "🥈 " : (i == 2 ? "🥉 " : ""));
                container.Add(new CuiLabel 
                { 
                    Text = { Text = $"{rankIcon}{ps.name}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" }, 
                    RectTransform = { AnchorMin = "0.05 0", AnchorMax = "0.4 1" } 
                }, rowPanel);
                
                // Level
                container.Add(new CuiLabel 
                { 
                    Text = { Text = $"{ps.level + 1}", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 0.8 0 1", Font = "robotocondensed-bold.ttf" }, 
                    RectTransform = { AnchorMin = "0.4 0", AnchorMax = "0.52 1" } 
                }, rowPanel);
                
                // Kills
                container.Add(new CuiLabel 
                { 
                    Text = { Text = $"{ps.kills}", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.4 0.9 0.4 1" }, 
                    RectTransform = { AnchorMin = "0.52 0", AnchorMax = "0.64 1" } 
                }, rowPanel);
                
                // Deaths
                container.Add(new CuiLabel 
                { 
                    Text = { Text = $"{ps.deaths}", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.9 0.4 0.4 1" }, 
                    RectTransform = { AnchorMin = "0.64 0", AnchorMax = "0.76 1" } 
                }, rowPanel);
                
                // K/D Ratio
                float kd = ps.deaths > 0 ? (float)ps.kills / ps.deaths : ps.kills;
                string kdColor = kd >= 1.0f ? "0.4 0.9 0.4 1" : "0.9 0.5 0.3 1";
                container.Add(new CuiLabel 
                { 
                    Text = { Text = $"{kd:F2}", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = kdColor }, 
                    RectTransform = { AnchorMin = "0.76 0", AnchorMax = "0.88 1" } 
                }, rowPanel);
                
                // Score (kills * 100)
                container.Add(new CuiLabel 
                { 
                    Text = { Text = $"{ps.kills * 100}", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 0.85 0.2 1", Font = "robotocondensed-bold.ttf" }, 
                    RectTransform = { AnchorMin = "0.88 0", AnchorMax = "1 1" } 
                }, rowPanel);
            }
            
            // Footer
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0.08 0.08 0.1 1" }, 
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.06" } 
            }, UI_Scoreboard, "ScoreFooter");
            
            container.Add(new CuiLabel 
            { 
                Text = { Text = $"Players: {LobbyQueue.Count}  •  Press TAB or /scoreboard to toggle", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.5 0.5 0.55 1" }, 
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" } 
            }, "ScoreFooter");
            
            CuiHelper.AddUi(player, container);
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
                var p = GetCachedPlayer(uid);
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

            // Main panel with professional styling
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0.05 0.05 0.08 0.98" }, 
                RectTransform = { AnchorMin = "0.25 0.25", AnchorMax = "0.75 0.75" }, 
                CursorEnabled = true 
            }, LayerMain, UI_MapVote);
            
            // Header bar
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0.8 0.4 0.15 1" }, 
                RectTransform = { AnchorMin = "0 0.85", AnchorMax = "1 1" } 
            }, UI_MapVote, "VoteHeader");

            // Title with icon
            container.Add(new CuiLabel 
            { 
                Text = { Text = "🗳️  MAP VOTE", FontSize = 22, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }, 
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.85 1" } 
            }, "VoteHeader");

            // Close button
            container.Add(new CuiButton 
            { 
                Button = { Command = "cod.closemapvote", Color = "0.6 0.25 0.2 1" }, 
                RectTransform = { AnchorMin = "0.88 0.15", AnchorMax = "0.98 0.85" }, 
                Text = { Text = "✕", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" } 
            }, "VoteHeader");

            // Count votes for each map (using shared helper)
            var voteCounts = GetVoteCounts();

            // Display maps in a row
            float mapWidth = 0.28f;
            float startX = 0.06f;
            float gap = 0.05f;
            
            for (int i = 0; i < AvailableMaps.Count && i < 3; i++)
            {
                string mapName = AvailableMaps[i];
                float xMin = startX + (i * (mapWidth + gap));
                int votes = voteCounts[mapName];
                bool hasVoted = MapVotes.ContainsKey(player.userID) && MapVotes[player.userID] == mapName;
                
                string mapPanel = $"Map_{i}";
                string borderColor = hasVoted ? "0.2 0.8 0.3 0.9" : "0.2 0.2 0.25 1";
                string btnColor = hasVoted ? "0.15 0.6 0.25 1" : "0.3 0.35 0.4 1";
                
                // Map card
                container.Add(new CuiPanel 
                { 
                    Image = { Color = borderColor }, 
                    RectTransform = { AnchorMin = $"{xMin} 0.2", AnchorMax = $"{xMin + mapWidth} 0.8" } 
                }, UI_MapVote, mapPanel);
                
                // Map preview placeholder (dark background)
                container.Add(new CuiPanel 
                { 
                    Image = { Color = "0.1 0.1 0.12 1" }, 
                    RectTransform = { AnchorMin = "0.04 0.35", AnchorMax = "0.96 0.96" } 
                }, mapPanel, $"MapPreview_{i}");
                
                // Map name
                container.Add(new CuiLabel 
                { 
                    Text = { Text = mapName.ToUpper(), FontSize = 16, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }, 
                    RectTransform = { AnchorMin = "0 0.5", AnchorMax = "1 0.9" } 
                }, $"MapPreview_{i}");
                
                // Vote count badge
                string voteColor = votes > 0 ? "0.8 0.5 0.15 1" : "0.3 0.3 0.35 1";
                container.Add(new CuiPanel 
                { 
                    Image = { Color = voteColor }, 
                    RectTransform = { AnchorMin = "0.7 0.7", AnchorMax = "0.95 0.95" } 
                }, $"MapPreview_{i}", $"VoteBadge_{i}");
                
                container.Add(new CuiLabel 
                { 
                    Text = { Text = $"{votes}", FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }, 
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" } 
                }, $"VoteBadge_{i}");
                
                // Vote button
                string btnText = hasVoted ? "✓ VOTED" : "VOTE";
                container.Add(new CuiButton 
                { 
                    Button = { Command = $"cod.votemap {mapName}", Color = btnColor }, 
                    RectTransform = { AnchorMin = "0.08 0.05", AnchorMax = "0.92 0.28" }, 
                    Text = { Text = btnText, FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" } 
                }, mapPanel);
            }

            // Footer info
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0.08 0.08 0.1 1" }, 
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.12" } 
            }, UI_MapVote, "VoteFooter");
            
            int totalVotes = MapVotes.Count;
            int totalPlayers = LobbyQueue.Count;
            container.Add(new CuiLabel 
            { 
                Text = { Text = $"Votes: {totalVotes}/{totalPlayers}  •  Current Map: {CurrentMap}", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.6 0.6 0.65 1" }, 
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" } 
            }, "VoteFooter");

            CuiHelper.AddUi(player, container);
        }

        // --- Helper: Get vote counts for all maps ---
        Dictionary<string, int> GetVoteCounts()
        {
            var voteCounts = new Dictionary<string, int>();
            foreach (var map in AvailableMaps) voteCounts[map] = 0;
            foreach (var vote in MapVotes.Values)
            {
                if (voteCounts.ContainsKey(vote)) voteCounts[vote]++;
            }
            return voteCounts;
        }

        string GetWinningMap()
        {
            if (MapVotes.Count == 0) return CurrentMap;
            
            var voteCounts = GetVoteCounts();
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

        // --- PROFESSIONAL STORE UI WITH PAGINATION ---
        void OpenStoreUI(BasePlayer player, string currentTab, int page = 0)
        {
            CuiHelper.DestroyUi(player, UI_Store);
            var container = new CuiElementContainer();
            var data = GetPlayerData(player.userID);
            
            // Store current page
            storePageIndex[player.userID] = page;

            // Main panel with gradient-style background
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0.05 0.05 0.08 0.98" }, 
                RectTransform = { AnchorMin = "0.15 0.12", AnchorMax = "0.85 0.88" }, 
                CursorEnabled = true 
            }, LayerMain, UI_Store);
            
            // Header bar
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0.12 0.12 0.15 1" }, 
                RectTransform = { AnchorMin = "0 0.88", AnchorMax = "1 1" } 
            }, UI_Store, "StoreHeader");
            
            // Title
            container.Add(new CuiLabel 
            { 
                Text = { Text = "CALLING CARD STORE", FontSize = 22, Align = TextAnchor.MiddleLeft, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }, 
                RectTransform = { AnchorMin = "0.03 0", AnchorMax = "0.4 1" } 
            }, "StoreHeader");
            
            // Credits display with icon
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0.2 0.2 0.25 1" }, 
                RectTransform = { AnchorMin = "0.55 0.2", AnchorMax = "0.78 0.8" } 
            }, "StoreHeader", "CreditsBox");
            
            container.Add(new CuiLabel 
            { 
                Text = { Text = "◆", FontSize = 18, Align = TextAnchor.MiddleLeft, Color = "1 0.8 0 1" }, 
                RectTransform = { AnchorMin = "0.08 0", AnchorMax = "0.25 1" } 
            }, "CreditsBox");
            
            container.Add(new CuiLabel 
            { 
                Text = { Text = $"{data.Credits:N0}", FontSize = 16, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 0.85 0.2 1" }, 
                RectTransform = { AnchorMin = "0.25 0", AnchorMax = "0.95 1" } 
            }, "CreditsBox");
            
            // Close button
            container.Add(new CuiButton 
            { 
                Button = { Close = UI_Store, Color = "0.7 0.2 0.2 1" }, 
                RectTransform = { AnchorMin = "0.92 0.15", AnchorMax = "0.98 0.85" }, 
                Text = { Text = "✕", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" } 
            }, "StoreHeader");

            // Tab bar
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0.08 0.08 0.1 1" }, 
                RectTransform = { AnchorMin = "0 0.78", AnchorMax = "1 0.87" } 
            }, UI_Store, "TabBar");
            
            string storeTabColor = currentTab == "store" ? "0.8 0.4 0.15 1" : "0.25 0.25 0.3 1";
            string ownedTabColor = currentTab == "owned" ? "0.8 0.4 0.15 1" : "0.25 0.25 0.3 1";
            string storeTextColor = currentTab == "store" ? "1 1 1 1" : "0.6 0.6 0.6 1";
            string ownedTextColor = currentTab == "owned" ? "1 1 1 1" : "0.6 0.6 0.6 1";
            
            container.Add(new CuiButton 
            { 
                Button = { Command = "cod.storetab store", Color = storeTabColor }, 
                RectTransform = { AnchorMin = "0.02 0.15", AnchorMax = "0.25 0.85" }, 
                Text = { Text = "🛒 BROWSE STORE", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = storeTextColor, Font = "robotocondensed-bold.ttf" } 
            }, "TabBar");
            
            container.Add(new CuiButton 
            { 
                Button = { Command = "cod.storetab owned", Color = ownedTabColor }, 
                RectTransform = { AnchorMin = "0.26 0.15", AnchorMax = "0.49 0.85" }, 
                Text = { Text = "📁 MY CARDS", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ownedTextColor, Font = "robotocondensed-bold.ttf" } 
            }, "TabBar");

            // Build display list
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
                // Custom emblems first (newest first)
                for(int k = data.SavedEmblems.Count - 1; k >= 0; k--)
                {
                    displayItems.Add(($"Custom #{k+1}", data.SavedEmblems[k], 0, true, k));
                }
                // Unlocked store cards
                foreach(var name in data.UnlockedCards)
                {
                    var confItem = config.StoreCards.FirstOrDefault(x => x.Name == name);
                    if (confItem != null) displayItems.Add((confItem.Name, confItem.Url, confItem.Price, false, -1));
                }
            }
            
            // Pagination calculations
            int totalItems = displayItems.Count;
            int totalPages = (int)Math.Ceiling((double)totalItems / STORE_ITEMS_PER_PAGE);
            if (totalPages == 0) totalPages = 1;
            if (page >= totalPages) page = totalPages - 1;
            if (page < 0) page = 0;
            
            int startIndex = page * STORE_ITEMS_PER_PAGE;
            int endIndex = Math.Min(startIndex + STORE_ITEMS_PER_PAGE, totalItems);
            
            // Item count and page display
            string pageInfo = totalPages > 1 ? $"Page {page + 1}/{totalPages} • {totalItems} items" : $"{totalItems} items";
            container.Add(new CuiLabel 
            { 
                Text = { Text = pageInfo, FontSize = 11, Align = TextAnchor.MiddleRight, Color = "0.5 0.5 0.55 1" }, 
                RectTransform = { AnchorMin = "0.55 0.15", AnchorMax = "0.98 0.85" } 
            }, "TabBar");

            // Draw cards for current page
            for (int i = startIndex; i < endIndex; i++)
            {
                var card = displayItems[i];
                int gridIndex = i - startIndex;
                int row = gridIndex / STORE_COLUMNS;
                int col = gridIndex % STORE_COLUMNS;
                float xMin = STORE_GRID_START_X + (col * (STORE_CARD_WIDTH + STORE_GRID_GAP_X));
                float yMax = STORE_GRID_START_Y - (row * (STORE_CARD_HEIGHT + STORE_GRID_GAP_Y));
                
                // Card container with border effect
                string cardPanel = $"Card_{gridIndex}";
                bool equipped = data.EquippedCardUrl == card.Url;
                string borderColor = equipped ? "0.2 0.8 0.3 0.8" : "0.2 0.2 0.25 1";
                
                container.Add(new CuiPanel 
                { 
                    Image = { Color = borderColor }, 
                    RectTransform = { AnchorMin = $"{xMin} {yMax - STORE_CARD_HEIGHT}", AnchorMax = $"{xMin + STORE_CARD_WIDTH} {yMax}" } 
                }, UI_Store, cardPanel);
                
                // Card image - null check for ImageLibrary
                var imgComp = new CuiRawImageComponent();
                if (ImageLibrary != null)
                {
                    string imgId = (string)ImageLibrary.Call("GetImage", card.Url);
                    if (!string.IsNullOrEmpty(imgId)) imgComp.Png = imgId; 
                    else imgComp.Url = card.Url;
                }
                else
                {
                    imgComp.Url = card.Url;
                }
                
                container.Add(new CuiElement 
                { 
                    Parent = cardPanel, 
                    Components = { 
                        imgComp, 
                        new CuiRectTransformComponent { AnchorMin = "0.03 0.35", AnchorMax = "0.97 0.97" } 
                    } 
                });
                
                // Card name
                container.Add(new CuiLabel 
                { 
                    Text = { Text = card.Name.ToUpper(), FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "0.9 0.9 0.9 1", Font = "robotocondensed-bold.ttf" }, 
                    RectTransform = { AnchorMin = "0.02 0.2", AnchorMax = "0.98 0.35" } 
                }, cardPanel);
                
                // Action button
                string btnColor, btnText, cmd;
                
                if (currentTab == "store")
                {
                    bool unlocked = data.UnlockedCards.Contains(card.Name);
                    if (equipped)
                    {
                        btnColor = "0.15 0.6 0.25 1";
                        btnText = "✓ EQUIPPED";
                        cmd = "";
                    }
                    else if (unlocked)
                    {
                        btnColor = "0.3 0.5 0.7 1";
                        btnText = "EQUIP";
                        cmd = $"cod.equipcard {card.Name}";
                    }
                    else
                    {
                        btnColor = "0.7 0.5 0.15 1";
                        btnText = $"◆ {card.Price}";
                        cmd = $"cod.buycard {card.Name}";
                    }
                }
                else
                {
                    if (equipped)
                    {
                        btnColor = "0.15 0.6 0.25 1";
                        btnText = "✓ EQUIPPED";
                        cmd = "";
                    }
                    else
                    {
                        btnColor = "0.3 0.5 0.7 1";
                        btnText = "EQUIP";
                        cmd = card.IsCustom ? $"cod.equipemblem {card.CustomIndex}" : $"cod.equipcard {card.Name}";
                    }
                }
                
                container.Add(new CuiButton 
                { 
                    Button = { Command = cmd, Color = btnColor }, 
                    RectTransform = { AnchorMin = "0.05 0.03", AnchorMax = "0.95 0.18" }, 
                    Text = { Text = btnText, FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1", Font = "robotocondensed-bold.ttf" } 
                }, cardPanel);
            }
            
            // Empty state message if no items
            if (displayItems.Count == 0)
            {
                container.Add(new CuiLabel 
                { 
                    Text = { Text = currentTab == "owned" ? "No cards yet!\nVisit the store to purchase cards." : "No cards available.", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "0.5 0.5 0.55 1" }, 
                    RectTransform = { AnchorMin = "0.2 0.3", AnchorMax = "0.8 0.6" } 
                }, UI_Store);
            }
            
            // Footer with pagination and emblem editor link
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0.08 0.08 0.1 1" }, 
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.08" } 
            }, UI_Store, "StoreFooter");
            
            // Pagination buttons (if needed)
            if (totalPages > 1)
            {
                // Previous button
                string prevColor = page > 0 ? "0.3 0.35 0.4 1" : "0.15 0.15 0.18 1";
                string prevCmd = page > 0 ? $"cod.storepage {currentTab} {page - 1}" : "";
                container.Add(new CuiButton 
                { 
                    Button = { Command = prevCmd, Color = prevColor }, 
                    RectTransform = { AnchorMin = "0.02 0.15", AnchorMax = "0.12 0.85" }, 
                    Text = { Text = "◀ PREV", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = page > 0 ? "1 1 1 1" : "0.4 0.4 0.4 1", Font = "robotocondensed-bold.ttf" } 
                }, "StoreFooter");
                
                // Next button
                string nextColor = page < totalPages - 1 ? "0.3 0.35 0.4 1" : "0.15 0.15 0.18 1";
                string nextCmd = page < totalPages - 1 ? $"cod.storepage {currentTab} {page + 1}" : "";
                container.Add(new CuiButton 
                { 
                    Button = { Command = nextCmd, Color = nextColor }, 
                    RectTransform = { AnchorMin = "0.88 0.15", AnchorMax = "0.98 0.85" }, 
                    Text = { Text = "NEXT ▶", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = page < totalPages - 1 ? "1 1 1 1" : "0.4 0.4 0.4 1", Font = "robotocondensed-bold.ttf" } 
                }, "StoreFooter");
                
                // Center tip
                container.Add(new CuiLabel 
                { 
                    Text = { Text = "Create custom emblems with /emblem", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.5 0.5 0.55 1" }, 
                    RectTransform = { AnchorMin = "0.15 0", AnchorMax = "0.85 1" } 
                }, "StoreFooter");
            }
            else
            {
                container.Add(new CuiLabel 
                { 
                    Text = { Text = "Create custom emblems with /emblem command", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.5 0.5 0.55 1" }, 
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" } 
                }, "StoreFooter");
            }
            
            CuiHelper.AddUi(player, container);
        }
        
        // Console command for pagination
        [ConsoleCommand("cod.storepage")]
        void ConsoleStorePage(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            string tab = arg.GetString(0, "store");
            int page = arg.GetInt(1, 0);
            OpenStoreUI(player, tab, page);
        }

        // --- GAME LOGIC ---

        void StartLobby()
        {
            CurrentState = GameState.Lobby;
            LobbyQueue.Clear();
            MapVotes.Clear();
            MapVoteActive = false;
            if (mapVoteTimer != null) { mapVoteTimer.Destroy(); mapVoteTimer = null; }
            PrintToChat("<color=#ce422b>[CoD]</color> <color=#FFD700>LOBBY IS OPEN!</color> Type /join to enter the queue.");
        }

        void CheckLobbyStart()
        {
            // Start map vote when minimum players reached or queue is full
            if (CurrentState == GameState.Lobby && !MapVoteActive)
            {
                if (LobbyQueue.Count >= config.MaxPlayersInQueue || LobbyQueue.Count >= config.MinPlayersToStart)
                {
                    StartMapVotePhase();
                }
            }
        }
        
        void StartMapVotePhase()
        {
            MapVoteActive = true;
            PrintToChat($"<color=#ce422b>[CoD]</color> <color=#FFD700>MAP VOTE STARTED!</color> ({config.MapVoteDuration}s) - Vote with /mapvote!");
            
            // Show map vote UI to all players in queue
            foreach (var uid in LobbyQueue)
            {
                var p = GetCachedPlayer(uid);
                if (p != null) ShowMapVoteUI(p);
            }
            
            // Update lobby bars
            UpdateAllLobbyBars();
            
            // Start countdown timer
            mapVoteTimer = timer.Once(config.MapVoteDuration, EndMapVoteAndStart);
        }
        
        void EndMapVoteAndStart()
        {
            MapVoteActive = false;
            
            // Close map vote UI
            foreach (var uid in LobbyQueue)
            {
                var p = GetCachedPlayer(uid);
                if (p != null) CuiHelper.DestroyUi(p, UI_MapVote);
            }
            
            // Determine winning map and start match
            CurrentMap = GetWinningMap();
            PrintToChat($"<color=#ce422b>[CoD]</color> Map selected: <color=#FFD700>{CurrentMap}</color>");
            PrintToChat("<color=#ce422b>[CoD]</color> <color=green>MATCH STARTING NOW!</color>");
            MapVotes.Clear();
            StartMatch();
        }

        void StartMatch()
        {
            CurrentState = GameState.Match;
            
            // Reset match stats for all players
            foreach (var uid in LobbyQueue)
            {
                var data = GetPlayerData(uid);
                data.Kills = 0;
                data.Deaths = 0;
            }
            
            foreach (var uid in LobbyQueue)
            {
                var p = GetCachedPlayer(uid);
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
            data.Credits += (int)WIN_CREDITS;
            SaveData();
            
            // Store players for next match
            var previousPlayers = new HashSet<ulong>(LobbyQueue);
            
            foreach (var uid in LobbyQueue)
            {
                var p = GetCachedPlayer(uid);
                if (p != null)
                {
                    p.inventory.Strip();
                    DestroyAllUI(p);
                    DrawLobbyUI(p, $"WINNER: {winner.displayName.ToUpper()}");
                }
            }
            
            // After 10 seconds, start next game loop with map vote
            timer.Once(10f, () => { 
                foreach(var p in BasePlayer.activePlayerList) DestroyAllUI(p); 
                
                // Reset to lobby state
                CurrentState = GameState.Lobby;
                LobbyQueue.Clear();
                MapVotes.Clear();
                MapVoteActive = false;
                
                // Re-add previous players to queue
                foreach (var uid in previousPlayers)
                {
                    var p = GetCachedPlayer(uid);
                    if (p != null && p.IsConnected)
                    {
                        LobbyQueue.Add(uid);
                        PlayerLevel[uid] = 0;
                        
                        // Reset match stats for next game
                        var pData = GetPlayerData(uid);
                        pData.Kills = 0;
                        pData.Deaths = 0;
                    }
                }
                
                if (LobbyQueue.Count >= config.MinPlayersToStart)
                {
                    // Enough players - start map vote immediately
                    PrintToChat("<color=#ce422b>[CoD]</color> <color=#FFD700>NEW MATCH STARTING!</color> Map vote beginning...");
                    StartMapVotePhase();
                }
                else
                {
                    // Not enough players - back to lobby
                    PrintToChat("<color=#ce422b>[CoD]</color> <color=#FFD700>LOBBY IS OPEN!</color> Type /join to enter the queue.");
                    foreach (var uid in LobbyQueue)
                    {
                        var p = GetCachedPlayer(uid);
                        if (p != null) DrawLobbyBar(p);
                    }
                }
            });
        }

        // --- Helper: Setup player at spawn (used by RespawnPlayer and OnPlayerRespawned) ---
        void SetupPlayerAtSpawn(BasePlayer player, Vector3 spawnPos)
        {
            TeleportTo(player, spawnPos);
            player.Heal(100);
            player.metabolism.calories.value = 500;
            GiveCurrentWeapon(player);
            DrawCenterBanner(player);
            BatchedHUDUpdate(player);
        }
        
        // --- Helper: Get random spawn position for current map ---
        Vector3? GetRandomSpawnPos()
        {
            if (!ArenaSpawns.ContainsKey(CurrentMap) || ArenaSpawns[CurrentMap].Count == 0)
                return null;
            var spawns = ArenaSpawns[CurrentMap];
            return spawns[UnityEngine.Random.Range(0, spawns.Count)];
        }

        void RespawnPlayer(BasePlayer player)
        {
            var spawnPos = GetRandomSpawnPos();
            if (!spawnPos.HasValue)
            {
                PrintWarning($"[CoDWarfare] No spawns configured for map '{CurrentMap}'! Using player's current position.");
                // Still setup player but don't teleport
                player.Heal(100);
                player.metabolism.calories.value = 500;
                GiveCurrentWeapon(player);
                DrawCenterBanner(player);
                BatchedHUDUpdate(player);
            }
            else
            {
                SetupPlayerAtSpawn(player, spawnPos.Value);
            }
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

            NextTick(() => BatchedHUDUpdate(player));
        }

        void OnWeaponFired(BaseProjectile projectile, BasePlayer player, ItemModProjectile mod, ProtoBuf.ProjectileShoot projectiles)
        {
            if (CurrentState == GameState.Match) BatchedHUDUpdate(player);
        }
        
        void OnReloadWeapon(BasePlayer player, BaseProjectile projectile)
        {
             if (CurrentState == GameState.Match) timer.Once(projectile.reloadTime + 0.1f, () => BatchedHUDUpdate(player));
        }

        // Update HUD when player switches active item (weapon switching)
        void OnPlayerActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (CurrentState == GameState.Match && player != null)
            {
                BatchedHUDUpdate(player);
            }
        }

        // Ensure HUD updates when items are used (consumables like syringes)
        void OnItemUse(Item item, int amountToUse)
        {
            var player = item.GetOwnerPlayer();
            if (player != null && CurrentState == GameState.Match) BatchedHUDUpdate(player);
        }
        
        // Update HUD when grenade/throwable is thrown
        void OnExplosiveThrown(BasePlayer player, BaseEntity entity, ThrownWeapon item)
        {
            if (CurrentState == GameState.Match && player != null)
            {
                // Delay to allow inventory to update
                timer.Once(0.1f, () => BatchedHUDUpdate(player));
            }
        }
        
        // Also hook into RocketLauncher and other throwables
        void OnRocketLaunched(BasePlayer player, BaseEntity entity)
        {
            if (CurrentState == GameState.Match && player != null)
            {
                timer.Once(0.1f, () => BatchedHUDUpdate(player));
            }
        }
        
        // Hook for when consumables are consumed (medical items)
        void OnItemAction(Item item, string action, BasePlayer player)
        {
            if (CurrentState == GameState.Match && player != null)
            {
                // Small delay to allow inventory changes to complete
                timer.Once(0.2f, () => BatchedHUDUpdate(player));
            }
        }
        
        // Hook for when medical items finish being used
        void OnHealingItemUse(MedicalTool tool, BasePlayer player)
        {
            if (CurrentState == GameState.Match && player != null)
            {
                timer.Once(0.5f, () => BatchedHUDUpdate(player));
            }
        }
        
        // Generic item removal hook - catches when items are removed from inventory
        void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            if (CurrentState != GameState.Match) return;
            var player = container?.playerOwner;
            if (player != null && LobbyQueue.Contains(player.userID))
            {
                timer.Once(0.1f, () => {
                    if (player != null && player.IsConnected) BatchedHUDUpdate(player);
                });
            }
        }

        void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (CurrentState != GameState.Match) return;
            BasePlayer victim = entity as BasePlayer;
            if (victim == null) return;
            BasePlayer killer = info?.Initiator as BasePlayer;
            
            // Track deaths
            if (LobbyQueue.Contains(victim.userID))
            {
                var victimData = GetPlayerData(victim.userID);
                victimData.Deaths++;
                
                // Clean up death entities - remove corpse, loot bag, dropped weapons
                CleanupDeathEntities(victim);
            }

            if (killer != null)
            {
                string weaponName = killer.GetActiveItem()?.info.displayName.translated ?? "UNKNOWN";
                ShowKillCard(victim, killer, weaponName);
                
                // Track kills
                if (LobbyQueue.Contains(killer.userID))
                {
                    var killerData = GetPlayerData(killer.userID);
                    killerData.Kills++;
                }
            }
            // Instant respawn - skip death screen completely
            NextTick(() => {
                if (victim != null && victim.IsConnected && victim.IsDead())
                {
                    victim.Respawn();
                }
            });

            if (killer != null && killer != victim)
            {
                int killerLevel = PlayerLevel[killer.userID];
                if (killerLevel >= WeaponLadder.Count - 1) { EndMatch(killer); return; }
                PlayerLevel[killer.userID]++;
                var kData = GetPlayerData(killer.userID);
                kData.Credits += (int)KILL_CREDITS;
                Effect.server.Run("assets/bundled/prefabs/fx/minigames/chippy/chippy_payout.prefab", killer.transform.position); 
                GiveCurrentWeapon(killer); 
                BatchedHUDUpdate(killer);
            }
        }
        
        // Clean up corpse, loot bags, and dropped items when player dies in match
        void CleanupDeathEntities(BasePlayer victim)
        {
            if (victim == null) return;
            Vector3 deathPos = victim.transform.position;
            
            // Small delay to let the death entities spawn first
            timer.Once(0.5f, () => {
                // Find and destroy corpse
                var corpses = Pool.GetList<PlayerCorpse>();
                Vis.Entities(deathPos, 5f, corpses);
                foreach (var corpse in corpses)
                {
                    if (corpse != null && corpse.playerSteamID == victim.userID)
                    {
                        corpse.Kill();
                    }
                }
                Pool.FreeList(ref corpses);
                
                // Find and destroy dropped items (weapons, etc)
                var droppedItems = Pool.GetList<DroppedItem>();
                Vis.Entities(deathPos, 5f, droppedItems);
                foreach (var dropped in droppedItems)
                {
                    if (dropped != null && !dropped.IsDestroyed)
                    {
                        dropped.Kill();
                    }
                }
                Pool.FreeList(ref droppedItems);
                
                // Find and destroy item containers (backpacks/loot bags)
                var containers = Pool.GetList<DroppedItemContainer>();
                Vis.Entities(deathPos, 5f, containers);
                foreach (var container in containers)
                {
                    if (container != null && !container.IsDestroyed)
                    {
                        container.Kill();
                    }
                }
                Pool.FreeList(ref containers);
            });
        }
        
        // Prevent death screen from showing by handling player wound state
        object OnPlayerWound(BasePlayer player)
        {
            // In CoD matches, skip wounded state - go straight to death
            if (CurrentState == GameState.Match && LobbyQueue.Contains(player.userID))
            {
                return false; // Prevent wounded state, player dies instantly
            }
            return null;
        }
        
        // Hook to intercept player respawn and teleport to arena spawn
        void OnPlayerRespawned(BasePlayer player)
        {
            if (CurrentState != GameState.Match) return;
            if (!LobbyQueue.Contains(player.userID)) return;
            
            var spawnPos = GetRandomSpawnPos();
            if (spawnPos.HasValue)
            {
                // Use NextTick to ensure player is fully spawned before teleporting
                NextTick(() => {
                    if (player != null && player.IsConnected)
                    {
                        SetupPlayerAtSpawn(player, spawnPos.Value);
                    }
                });
            }
            else
            {
                PrintWarning($"[CoDWarfare] No spawns for map '{CurrentMap}'! Add spawns with /cod.addspawn {CurrentMap}");
            }
        }
        
        // --- PERFORMANCE: Batched HUD Updates ---
        // Prevents rapid UI rebuilds by coalescing multiple update requests
        void BatchedHUDUpdate(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            
            ulong uid = player.userID;
            
            // If there's already a pending update, don't schedule another
            if (pendingHUDUpdates.ContainsKey(uid) && pendingHUDUpdates[uid] != null)
            {
                return;
            }
            
            // Schedule the actual update with a small delay to batch multiple requests
            pendingHUDUpdates[uid] = timer.Once(HUD_UPDATE_BATCH_DELAY, () => {
                pendingHUDUpdates.Remove(uid);
                if (player != null && player.IsConnected && CurrentState == GameState.Match)
                {
                    DrawGameHUD(player);
                }
            });
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
            
            // Get the player's current weapon from weapon ladder (ensures correct weapon based on level)
            int playerLevel = PlayerLevel.ContainsKey(player.userID) ? PlayerLevel[player.userID] : 0;
            if (playerLevel >= WeaponLadder.Count) playerLevel = WeaponLadder.Count - 1;
            string currentWeaponShortname = WeaponLadder[playerLevel];
            
            // Get display name from item definition
            var weaponDef = ItemManager.FindItemDefinition(currentWeaponShortname);
            string displayName = weaponDef?.displayName.translated ?? currentWeaponShortname.ToUpper();
            
            // Calculate ammo based on weapon type
            string ammoText;
            
            // Check if current weapon is hitscan (infinite ammo)
            if (IsHitscanWeapon(currentWeaponShortname))
            {
                ammoText = "<color=#FFD700>∞</color> <size=14>| ∞</size>";
            }
            else if (currentWeaponShortname == "knife.combat")
            {
                ammoText = "<color=#FFD700>MELEE</color>";
            }
            else if (currentWeaponShortname.Contains("bow") || currentWeaponShortname.Contains("crossbow"))
            {
                // Bow/crossbow - show arrow count using cached definitions
                int arrows = arrowWoodenDef != null ? player.inventory.GetAmount(arrowWoodenDef.itemid) : 0;
                int arrowsHV = arrowHVDef != null ? player.inventory.GetAmount(arrowHVDef.itemid) : 0;
                int arrowsBone = arrowBoneDef != null ? player.inventory.GetAmount(arrowBoneDef.itemid) : 0;
                ammoText = $"{arrows + arrowsHV + arrowsBone} <size=14>arrows</size>";
            }
            else
            {
                // Regular weapon - try to get from belt slot 0 for accurate ammo
                ammoText = "-- | --"; // Default
                var weaponItem = player.inventory.containerBelt?.GetSlot(0);
                if (weaponItem != null)
                {
                    var proj = weaponItem.GetHeldEntity() as BaseProjectile;
                    if (proj != null && proj.primaryMagazine != null)
                    {
                        int clip = proj.primaryMagazine.contents;
                        int reserve = player.inventory.GetAmount(proj.primaryMagazine.ammoType.itemid);
                        ammoText = $"{clip} <size=14>| {reserve}</size>";
                    }
                }
            }

            // 2. Weapon Icon - Use weapon from ladder (guaranteed correct)
            string iconUrl = $"https://rustlabs.com/img/items180/{currentWeaponShortname}.png";
            container.Add(new CuiElement 
            { 
                Parent = UI_HUD, 
                Components = { 
                    new CuiRawImageComponent { Url = iconUrl }, 
                    new CuiRectTransformComponent { AnchorMin = "0.05 0.2", AnchorMax = "0.35 0.9" } 
                } 
            });

            // 3. Ammo Count
            container.Add(new CuiLabel 
            { 
                Text = { Text = ammoText, FontSize = 28, Align = TextAnchor.MiddleRight, Font = "robotocondensed-bold.ttf" }, 
                RectTransform = { AnchorMin = "0.4 0.4", AnchorMax = "0.95 0.9" } 
            }, UI_HUD);

            // 4. Weapon Name
            container.Add(new CuiLabel 
            { 
                Text = { Text = displayName.ToUpper(), FontSize = 10, Align = TextAnchor.MiddleRight, Color = "0.7 0.7 0.7 1" }, 
                RectTransform = { AnchorMin = "0.4 0.35", AnchorMax = "0.95 0.5" } 
            }, UI_HUD);

            // 5. Tactical Icon (Syringe) - Dynamic based on inventory
            int syringeCount = syringeItemDef != null ? player.inventory.GetAmount(syringeItemDef.itemid) : 0;
            if (syringeCount > 0) 
            {
                container.Add(new CuiElement 
                { 
                    Parent = UI_HUD, 
                    Components = { 
                        new CuiRawImageComponent { Url = "https://rustlabs.com/img/items180/syringe.medical.png", Color = "1 1 1 0.8" }, 
                        new CuiRectTransformComponent { AnchorMin = "0.40 0.05", AnchorMax = "0.50 0.35" } 
                    } 
                });
            }
            
            // 6. Lethal Icon (Grenade) - Dynamic based on inventory
            int grenadeCount = grenadeItemDef != null ? player.inventory.GetAmount(grenadeItemDef.itemid) : 0;
            if (grenadeCount > 0) 
            {
                container.Add(new CuiElement 
                { 
                    Parent = UI_HUD, 
                    Components = { 
                        new CuiRawImageComponent { Url = "https://rustlabs.com/img/items180/grenade.f1.png", Color = "1 1 1 0.8" }, 
                        new CuiRectTransformComponent { AnchorMin = "0.52 0.05", AnchorMax = "0.62 0.35" } 
                    } 
                });
            }

            // 7. Level Counter
            int level = playerLevel + 1;
            int max = WeaponLadder.Count;
            container.Add(new CuiLabel 
            { 
                Text = { Text = $"LVL {level}/{max}", FontSize = 10, Align = TextAnchor.MiddleRight, Color = "1 0.8 0 1" }, 
                RectTransform = { AnchorMin = "0.7 0.05", AnchorMax = "0.95 0.3" } 
            }, UI_HUD);

            CuiHelper.AddUi(player, container);
            
            // Draw live leaderboard (top-left)
            DrawLiveLeaderboard(player);
        }
        
        // Live leaderboard during match (top-left)
        void DrawLiveLeaderboard(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UI_Leaderboard);
            var container = new CuiElementContainer();
            
            // Main panel
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0 0 0 0.75" }, 
                RectTransform = { AnchorMin = "0.01 0.75", AnchorMax = "0.18 0.98" } 
            }, LayerMain, UI_Leaderboard);
            
            // Header
            container.Add(new CuiLabel 
            { 
                Text = { Text = "📊 LEADERBOARD", FontSize = 10, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 0.8 0 1" }, 
                RectTransform = { AnchorMin = "0 0.85", AnchorMax = "1 1" } 
            }, UI_Leaderboard);
            
            // Build sorted player list using shared helper
            var playerStats = GetSortedPlayerStats();
            
            // Show top 5
            float rowHeight = 0.16f;
            float startY = 0.82f;
            int maxRows = 5;
            
            for (int i = 0; i < playerStats.Count && i < maxRows; i++)
            {
                var ps = playerStats[i];
                float yMax = startY - (i * rowHeight);
                float yMin = yMax - rowHeight + 0.02f;
                
                string nameColor = ps.uid == player.userID ? "1 0.8 0 1" : "1 1 1 0.9";
                string rankIcon = i == 0 ? "🥇" : (i == 1 ? "🥈" : (i == 2 ? "🥉" : $"#{i+1}"));
                
                // Truncate name if too long
                string displayName = ps.name.Length > 10 ? ps.name.Substring(0, 10) + ".." : ps.name;
                
                container.Add(new CuiLabel 
                { 
                    Text = { Text = $"{rankIcon} {displayName}", FontSize = 9, Align = TextAnchor.MiddleLeft, Color = nameColor }, 
                    RectTransform = { AnchorMin = $"0.05 {yMin}", AnchorMax = $"0.7 {yMax}" } 
                }, UI_Leaderboard);
                
                container.Add(new CuiLabel 
                { 
                    Text = { Text = $"L{ps.level + 1} K{ps.kills}", FontSize = 8, Align = TextAnchor.MiddleRight, Color = "0.6 0.9 0.6 1" }, 
                    RectTransform = { AnchorMin = $"0.6 {yMin}", AnchorMax = $"0.95 {yMax}" } 
                }, UI_Leaderboard);
            }
            
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
            CuiHelper.DestroyUi(player, UI_Scoreboard);
            CuiHelper.DestroyUi(player, UI_Leaderboard);
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
            
            string status;
            string bgColor;
            int playersNeeded = Math.Max(0, config.MinPlayersToStart - LobbyQueue.Count);
            
            if (MapVoteActive)
            {
                status = $"🗳️ MAP VOTE IN PROGRESS • {LobbyQueue.Count} Players Ready • Use /mapvote";
                bgColor = "0.7 0.4 0.1 0.9";
            }
            else if (LobbyQueue.Count >= config.MinPlayersToStart)
            {
                status = $"✓ QUEUE READY ({LobbyQueue.Count}/{config.MaxPlayersInQueue}) • Map vote starting soon...";
                bgColor = "0.2 0.6 0.3 0.9";
            }
            else
            {
                status = $"⏳ IN QUEUE ({LobbyQueue.Count}/{config.MaxPlayersInQueue}) • Need {playersNeeded} more player(s)";
                bgColor = "0.2 0.4 0.6 0.9";
            }
            
            container.Add(new CuiPanel 
            { 
                Image = { Color = bgColor }, 
                RectTransform = { AnchorMin = "0.25 0.94", AnchorMax = "0.75 0.99" }, 
                CursorEnabled = false 
            }, LayerMain, UI_LobbyBar);
            
            container.Add(new CuiLabel 
            { 
                Text = { Text = status, FontSize = 12, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", Color = "1 1 1 1" }, 
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" } 
            }, UI_LobbyBar);
            
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
        
        // --- Helper: Get sorted player stats for leaderboard/scoreboard ---
        List<(ulong uid, string name, int level, int kills, int deaths)> GetSortedPlayerStats()
        {
            var playerStats = new List<(ulong uid, string name, int level, int kills, int deaths)>();
            foreach (var uid in LobbyQueue)
            {
                var p = GetCachedPlayer(uid);
                if (p != null)
                {
                    var data = GetPlayerData(uid);
                    int level = PlayerLevel.ContainsKey(uid) ? PlayerLevel[uid] : 0;
                    playerStats.Add((uid, p.displayName, level, data.Kills, data.Deaths));
                }
            }
            return playerStats.OrderByDescending(x => x.level).ThenByDescending(x => x.kills).ToList();
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
                    
                    string weaponShortname = gun.GetItem().info.shortname;
                    
                    // Per-weapon muzzle flash - properly parented to gun's muzzle_flash bone
                    string muzzleEffect = WeaponMuzzleFlash.ContainsKey(weaponShortname) 
                        ? WeaponMuzzleFlash[weaponShortname] 
                        : "assets/bundled/prefabs/fx/muzzleflash/assaultrifle.prefab";
                    
                    // Create effect and parent it to the gun entity at the muzzle_flash bone
                    var effect = new Effect(muzzleEffect, gun, StringPool.Get("muzzle_flash"), Vector3.zero, Vector3.forward);
                    EffectNetwork.Send(effect);
                    
                    // Per-weapon gun fire sound - also parented to gun's muzzle bone
                    if (WeaponFireSound.ContainsKey(weaponShortname))
                    {
                        var soundEffect = new Effect(WeaponFireSound[weaponShortname], gun, StringPool.Get("muzzle_flash"), Vector3.zero, Vector3.forward);
                        EffectNetwork.Send(soundEffect);
                    }
                    
                    Ray ray = player.eyes.HeadRay();
                    RaycastHit hit;
                    if (Physics.Raycast(ray, out hit, HITSCAN_RANGE, LayerMask.GetMask("Construction", "Terrain", "Player (Server)", "World")))
                    {
                        var victim = hit.GetEntity() as BasePlayer;
                        if (victim != null)
                        {
                            float dmg = HITSCAN_DAMAGE; 
                            if (gun.ShortPrefabName.Contains("sniper")) dmg = SNIPER_DAMAGE;
                            victim.OnAttacked(new HitInfo(player, victim, Rust.DamageType.Bullet, dmg, hit.point));
                            Effect.server.Run("assets/bundled/prefabs/fx/minigames/chippy/chippy_encounter.prefab", player.transform.position);
                            ShowHitmarker(player);
                        }
                        else Effect.server.Run("assets/bundled/prefabs/fx/impacts/concrete/concrete-impact-bullet-1.prefab", hit.point);
                    }
                }
            }
        }

        class SpawnPoint
        {
            public float x;
            public float y;
            public float z;
            
            public SpawnPoint() { }
            public SpawnPoint(Vector3 v) { x = v.x; y = v.y; z = v.z; }
            public Vector3 ToVector3() => new Vector3(x, y, z);
        }
        
        class StoredData 
        { 
            public Dictionary<string, List<SpawnPoint>> Arenas = new Dictionary<string, List<SpawnPoint>>(); 
            public Dictionary<ulong, PlayerStoreData> Players = new Dictionary<ulong, PlayerStoreData>();
            public SpawnPoint LobbySpawnPoint = null;
        }
        void SaveData() 
        { 
            // Convert Vector3 to SpawnPoint for proper JSON serialization
            var serializableArenas = new Dictionary<string, List<SpawnPoint>>();
            foreach (var kvp in ArenaSpawns)
            {
                serializableArenas[kvp.Key] = kvp.Value.Select(v => new SpawnPoint(v)).ToList();
            }
            
            var storedData = new StoredData { 
                Arenas = serializableArenas, 
                Players = StoreData,
                LobbySpawnPoint = LobbySpawn.HasValue ? new SpawnPoint(LobbySpawn.Value) : null
            };
            
            Interface.Oxide.DataFileSystem.WriteObject("CoDWarfare", storedData); 
            Puts($"[CoDWarfare] Data saved - {StoreData.Count} players, {ArenaSpawns.Values.Sum(x => x.Count)} total spawns");
        }
        
        void LoadData() 
        { 
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>("CoDWarfare"); 
                if (data != null) 
                { 
                    // Convert SpawnPoint back to Vector3
                    ArenaSpawns = new Dictionary<string, List<Vector3>>();
                    if (data.Arenas != null)
                    {
                        foreach (var kvp in data.Arenas)
                        {
                            ArenaSpawns[kvp.Key] = kvp.Value?.Select(sp => sp.ToVector3()).ToList() ?? new List<Vector3>();
                        }
                    }
                    StoreData = data.Players ?? new Dictionary<ulong, PlayerStoreData>(); 
                    
                    // Load lobby spawn
                    if (data.LobbySpawnPoint != null)
                        LobbySpawn = data.LobbySpawnPoint.ToVector3();
                }
            }
            catch (Exception ex)
            {
                Puts($"[CoDWarfare] Error loading data: {ex.Message}");
                ArenaSpawns = new Dictionary<string, List<Vector3>>();
                StoreData = new Dictionary<ulong, PlayerStoreData>();
            }
            
            // Initialize empty map spawn lists if needed
            foreach (var map in AvailableMaps)
            {
                if (!ArenaSpawns.ContainsKey(map))
                    ArenaSpawns[map] = new List<Vector3>();
            }
            
            // Ensure all player data has properly initialized lists (fix for null lists after deserialization)
            int totalEmblems = 0;
            foreach (var kvp in StoreData)
            {
                if (kvp.Value.SavedEmblems == null)
                    kvp.Value.SavedEmblems = new List<string>();
                if (kvp.Value.UnlockedCards == null)
                    kvp.Value.UnlockedCards = new List<string> { "Default" };
                totalEmblems += kvp.Value.SavedEmblems.Count;
            }
            
            Puts($"[CoDWarfare] Data loaded - {StoreData.Count} players with {totalEmblems} total emblems");
            Puts($"[CoDWarfare] Spawns - Nuketown: {ArenaSpawns["Nuketown"].Count}, Rust: {ArenaSpawns["Rust"].Count}, Shipment: {ArenaSpawns["Shipment"].Count}");
            Puts($"[CoDWarfare] Lobby spawn: {(LobbySpawn.HasValue ? "Set" : "Not set")}");
        }
        
        [ChatCommand("cod.listspawns")]
        void CmdListSpawns(BasePlayer player, string cmd, string[] args)
        {
            if (!player.IsAdmin) return;
            player.ChatMessage("<color=#ce422b>[CoD]</color> <color=#FFD700>Spawn Points:</color>");
            foreach (var kvp in ArenaSpawns)
            {
                player.ChatMessage($"<color=#00ff00>{kvp.Key}</color>: {kvp.Value.Count} spawns");
                int i = 1;
                foreach (var spawn in kvp.Value)
                {
                    player.ChatMessage($"  #{i}: {spawn.x:F1}, {spawn.y:F1}, {spawn.z:F1}");
                    i++;
                }
            }
        }
        
        [ChatCommand("cod.clearspawns")]
        void CmdClearSpawns(BasePlayer player, string cmd, string[] args)
        {
            if (!player.IsAdmin || args.Length == 0) return;
            string map = args[0];
            if (ArenaSpawns.ContainsKey(map))
            {
                ArenaSpawns[map].Clear();
                SaveData();
                player.ChatMessage($"<color=#ce422b>[CoD]</color> Cleared all spawns for {map}");
            }
        }
        
        [ChatCommand("cod.gotospawn")]
        void CmdGotoSpawn(BasePlayer player, string cmd, string[] args)
        {
            if (!player.IsAdmin || args.Length < 2) 
            {
                player.ChatMessage("<color=#ce422b>[CoD]</color> Usage: /cod.gotospawn <mapname> <index>");
                return;
            }
            string map = args[0];
            int index = 0;
            if (!int.TryParse(args[1], out index)) return;
            
            if (ArenaSpawns.ContainsKey(map) && index > 0 && index <= ArenaSpawns[map].Count)
            {
                TeleportTo(player, ArenaSpawns[map][index - 1]);
                player.ChatMessage($"<color=#ce422b>[CoD]</color> Teleported to {map} spawn #{index}");
            }
            else
            {
                player.ChatMessage($"<color=#ce422b>[CoD]</color> Invalid map or spawn index");
            }
        }
        
        [ChatCommand("cod.myemblems")]
        void CmdMyEmblems(BasePlayer player, string cmd, string[] args)
        {
            var data = GetPlayerData(player.userID);
            player.ChatMessage($"<color=#ce422b>[CoD]</color> <color=#FFD700>Your Emblems:</color> {data.SavedEmblems.Count} saved");
            int i = 1;
            foreach (var emblem in data.SavedEmblems)
            {
                // Show truncated URL
                string shortUrl = emblem.Length > 50 ? emblem.Substring(0, 50) + "..." : emblem;
                player.ChatMessage($"  #{i}: {shortUrl}");
                i++;
            }
            player.ChatMessage($"<color=#FFD700>Equipped:</color> {(string.IsNullOrEmpty(data.EquippedCardUrl) ? "None" : "Custom")}");
        }
        
        [ChatCommand("cod.forcesave")]
        void CmdForceSave(BasePlayer player, string cmd, string[] args)
        {
            if (!player.IsAdmin) return;
            SaveData();
            player.ChatMessage("<color=#ce422b>[CoD]</color> Data force saved!");
        }
    }
}