using System;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("EmblemEditor", "KillaDome", "1.0.0")]
    [Description("Standalone emblem editor plugin with web-based editor and in-game display")]
    public class EmblemEditor : RustPlugin
    {
        #region Configuration
        
        private Configuration config;
        
        private class Configuration
        {
            [JsonProperty("Editor URL (Your VPS URL)")]
            public string EditorUrl { get; set; } = "http://YOUR_VPS_IP:3000";
            
            [JsonProperty("Emblem Width in HUD")]
            public int EmblemWidth { get; set; } = 100;
            
            [JsonProperty("Emblem Height in HUD")]
            public int EmblemHeight { get; set; } = 20;
            
            [JsonProperty("Max Emblems Per Player")]
            public int MaxEmblemsPerPlayer { get; set; } = 50;
        }
        
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception();
            }
            catch
            {
                PrintWarning("Invalid configuration file, creating default...");
                config = new Configuration();
            }
            SaveConfig();
        }
        
        protected override void SaveConfig() => Config.WriteObject(config);
        
        protected override void LoadDefaultConfig() => config = new Configuration();
        
        #endregion
        
        #region Data Storage
        
        private Dictionary<ulong, PlayerEmblemData> PlayerData = new Dictionary<ulong, PlayerEmblemData>();
        private const string DataFileName = "EmblemEditor_Data";
        
        private class PlayerEmblemData
        {
            public List<string> SavedEmblems = new List<string>();
            public string EquippedEmblem = "";
            public int Credits = 0;
        }
        
        private void LoadData()
        {
            try
            {
                PlayerData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerEmblemData>>(DataFileName);
                if (PlayerData == null)
                    PlayerData = new Dictionary<ulong, PlayerEmblemData>();
                
                // Initialize null lists
                foreach (var kvp in PlayerData)
                {
                    if (kvp.Value.SavedEmblems == null)
                        kvp.Value.SavedEmblems = new List<string>();
                }
                
                int totalEmblems = 0;
                foreach (var kvp in PlayerData)
                    totalEmblems += kvp.Value.SavedEmblems.Count;
                    
                Puts($"[EmblemEditor] Data loaded - {PlayerData.Count} players with {totalEmblems} total emblems");
            }
            catch (Exception ex)
            {
                PrintError($"Failed to load data: {ex.Message}");
                PlayerData = new Dictionary<ulong, PlayerEmblemData>();
            }
        }
        
        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, PlayerData);
            Puts("[EmblemEditor] Data saved.");
        }
        
        private PlayerEmblemData GetPlayerData(ulong steamId)
        {
            if (!PlayerData.ContainsKey(steamId))
                PlayerData[steamId] = new PlayerEmblemData();
            return PlayerData[steamId];
        }
        
        #endregion
        
        #region Hooks
        
        private void Init()
        {
            LoadData();
        }
        
        private void OnServerSave() => SaveData();
        
        private void Unload() => SaveData();
        
        #endregion
        
        #region Chat Commands
        
        [ChatCommand("emblem")]
        private void CmdEmblem(BasePlayer player)
        {
            string editorUrl = $"{config.EditorUrl}/emblem-editor.html?id={player.UserIDString}";
            
            // Create a note item with the emblem editor link
            var noteItem = ItemManager.CreateByName("note", 1);
            if (noteItem != null)
            {
                noteItem.text = $"=== EMBLEM EDITOR LINK ===\n\nCopy and paste this URL into your web browser:\n\n{editorUrl}\n\n=== INSTRUCTIONS ===\n1. Open your web browser\n2. Paste the link above\n3. Create your emblem\n4. Click SAVE to upload\n5. Use /myemblems to see saved emblems";
                noteItem.name = "Emblem Editor Link";
                
                if (!player.inventory.GiveItem(noteItem))
                {
                    noteItem.Drop(player.eyes.position, player.eyes.BodyForward() * 2f);
                    player.ChatMessage("<color=#ce422b>[Emblem]</color> Your inventory is full! Note dropped at your feet.");
                }
                else
                {
                    player.ChatMessage("<color=#ce422b>[Emblem]</color> <color=#4caf50>Emblem editor link given as a note!</color> Check your inventory.");
                }
            }
            else
            {
                // Fallback to chat message
                player.ChatMessage($"<color=#ce422b>[Emblem]</color> Editor: {editorUrl}");
            }
        }
        
        [ChatCommand("myemblems")]
        private void CmdMyEmblems(BasePlayer player, string cmd, string[] args)
        {
            var data = GetPlayerData(player.userID);
            player.ChatMessage($"<color=#ce422b>[Emblem]</color> <color=#FFD700>Your Emblems:</color> {data.SavedEmblems.Count} saved");
            
            int i = 0;
            foreach (var emblem in data.SavedEmblems)
            {
                i++;
                string shortUrl = emblem.Length > 50 ? emblem.Substring(0, 50) + "..." : emblem;
                string equipped = (data.EquippedEmblem == emblem) ? " <color=#4caf50>[EQUIPPED]</color>" : "";
                player.ChatMessage($"  #{i}: {shortUrl}{equipped}");
            }
            
            if (data.SavedEmblems.Count == 0)
                player.ChatMessage("  No emblems saved yet. Use /emblem to create one!");
        }
        
        [ChatCommand("equipemblem")]
        private void CmdEquipEmblem(BasePlayer player, string cmd, string[] args)
        {
            if (args.Length < 1)
            {
                player.ChatMessage("<color=#ce422b>[Emblem]</color> Usage: /equipemblem <number>");
                return;
            }
            
            if (!int.TryParse(args[0], out int index) || index < 1)
            {
                player.ChatMessage("<color=#ce422b>[Emblem]</color> Invalid number. Use /myemblems to see your emblems.");
                return;
            }
            
            var data = GetPlayerData(player.userID);
            index--; // Convert to 0-based
            
            if (index >= data.SavedEmblems.Count)
            {
                player.ChatMessage($"<color=#ce422b>[Emblem]</color> You don't have emblem #{index + 1}. Use /myemblems to see your list.");
                return;
            }
            
            data.EquippedEmblem = data.SavedEmblems[index];
            SaveData();
            player.ChatMessage($"<color=#ce422b>[Emblem]</color> <color=#4caf50>Emblem #{index + 1} equipped!</color>");
        }
        
        [ChatCommand("emblems")]
        private void CmdEmblemsUI(BasePlayer player, string cmd, string[] args)
        {
            ShowEmblemGalleryUI(player);
        }
        
        #endregion
        
        #region Console Commands
        
        [ConsoleCommand("emblem.update")]
        private void ConsoleEmblemUpdate(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length < 2)
            {
                Puts("[EmblemEditor] emblem.update requires: steamId url");
                return;
            }
            
            string steamIdStr = arg.Args[0];
            string url = arg.Args[1];
            
            if (!ulong.TryParse(steamIdStr, out ulong steamId))
            {
                Puts($"[EmblemEditor] Invalid SteamID: {steamIdStr}");
                return;
            }
            
            var data = GetPlayerData(steamId);
            
            // Add cache buster to URL
            string uniqueUrl = url.Contains("?") ? $"{url}&v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}" : $"{url}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            
            // Check if already saved (without cache buster)
            string baseUrl = url.Split('?')[0];
            bool alreadyExists = false;
            for (int i = 0; i < data.SavedEmblems.Count; i++)
            {
                if (data.SavedEmblems[i].Split('?')[0] == baseUrl)
                {
                    data.SavedEmblems[i] = uniqueUrl; // Update existing
                    alreadyExists = true;
                    break;
                }
            }
            
            if (!alreadyExists)
            {
                if (data.SavedEmblems.Count >= config.MaxEmblemsPerPlayer)
                {
                    data.SavedEmblems.RemoveAt(0); // Remove oldest
                }
                data.SavedEmblems.Add(uniqueUrl);
            }
            
            // Auto-equip if first emblem
            if (string.IsNullOrEmpty(data.EquippedEmblem))
                data.EquippedEmblem = uniqueUrl;
            
            SaveData();
            
            // Notify player if online
            var player = BasePlayer.FindByID(steamId);
            if (player != null)
            {
                player.ChatMessage("<color=#ce422b>[Emblem]</color> <color=#4caf50>New emblem saved!</color> Use /myemblems to see your collection.");
            }
            
            Puts($"[EmblemEditor] Emblem updated for {steamId}");
        }
        
        [ConsoleCommand("emblem.equip")]
        private void ConsoleEmblemEquip(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            if (arg.Args == null || arg.Args.Length < 1)
            {
                player.ChatMessage("<color=#ce422b>[Emblem]</color> Invalid command.");
                return;
            }
            
            if (!int.TryParse(arg.Args[0], out int index))
            {
                player.ChatMessage("<color=#ce422b>[Emblem]</color> Invalid index.");
                return;
            }
            
            var data = GetPlayerData(player.userID);
            
            if (index < 0 || index >= data.SavedEmblems.Count)
            {
                player.ChatMessage("<color=#ce422b>[Emblem]</color> Emblem not found.");
                return;
            }
            
            data.EquippedEmblem = data.SavedEmblems[index];
            SaveData();
            
            player.ChatMessage("<color=#ce422b>[Emblem]</color> <color=#4caf50>Emblem equipped!</color>");
            ShowEmblemGalleryUI(player); // Refresh UI
        }
        
        [ConsoleCommand("emblem.delete")]
        private void ConsoleEmblemDelete(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            if (arg.Args == null || arg.Args.Length < 1)
            {
                player.ChatMessage("<color=#ce422b>[Emblem]</color> Invalid command.");
                return;
            }
            
            if (!int.TryParse(arg.Args[0], out int index))
            {
                player.ChatMessage("<color=#ce422b>[Emblem]</color> Invalid index.");
                return;
            }
            
            var data = GetPlayerData(player.userID);
            
            if (index < 0 || index >= data.SavedEmblems.Count)
            {
                player.ChatMessage("<color=#ce422b>[Emblem]</color> Emblem not found.");
                return;
            }
            
            string removedUrl = data.SavedEmblems[index];
            data.SavedEmblems.RemoveAt(index);
            
            // Update equipped if deleted
            if (data.EquippedEmblem == removedUrl)
            {
                data.EquippedEmblem = data.SavedEmblems.Count > 0 ? data.SavedEmblems[0] : "";
            }
            
            SaveData();
            player.ChatMessage("<color=#ce422b>[Emblem]</color> <color=#ff6600>Emblem deleted.</color>");
            ShowEmblemGalleryUI(player); // Refresh UI
        }
        
        [ConsoleCommand("emblem.close")]
        private void ConsoleEmblemClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, "EmblemGalleryUI");
        }
        
        #endregion
        
        #region UI
        
        private const string EmblemGalleryUIName = "EmblemGalleryUI";
        
        private void ShowEmblemGalleryUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, EmblemGalleryUIName);
            
            var data = GetPlayerData(player.userID);
            var container = new CuiElementContainer();
            
            // Background overlay
            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.95" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", EmblemGalleryUIName);
            
            // Main panel
            container.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 1" },
                RectTransform = { AnchorMin = "0.15 0.1", AnchorMax = "0.85 0.9" }
            }, EmblemGalleryUIName, "MainPanel");
            
            // Header
            container.Add(new CuiPanel
            {
                Image = { Color = "0.8 0.25 0.15 1" },
                RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" }
            }, "MainPanel", "Header");
            
            container.Add(new CuiLabel
            {
                Text = { Text = "🎨 MY EMBLEMS", FontSize = 20, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, "Header");
            
            // Close button
            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.2 0.2 1", Command = "emblem.close" },
                RectTransform = { AnchorMin = "0.95 0.92", AnchorMax = "0.99 0.98" },
                Text = { Text = "✕", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "MainPanel");
            
            // Content area
            container.Add(new CuiPanel
            {
                Image = { Color = "0.12 0.12 0.12 1" },
                RectTransform = { AnchorMin = "0.02 0.08", AnchorMax = "0.98 0.9" }
            }, "MainPanel", "Content");
            
            if (data.SavedEmblems.Count == 0)
            {
                // Empty state
                container.Add(new CuiLabel
                {
                    Text = { Text = "No emblems saved yet!\n\nUse /emblem to get an editor link\nand create your first emblem.", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "0.5 0.5 0.5 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, "Content");
            }
            else
            {
                // Grid of emblems (4 columns)
                int columns = 4;
                float itemWidth = 0.23f;
                float itemHeight = 0.28f;
                float padding = 0.02f;
                float startX = 0.02f;
                float startY = 0.68f;
                
                for (int i = 0; i < data.SavedEmblems.Count && i < 12; i++)
                {
                    int col = i % columns;
                    int row = i / columns;
                    
                    float xMin = startX + col * (itemWidth + padding);
                    float xMax = xMin + itemWidth;
                    float yMax = startY - row * (itemHeight + padding);
                    float yMin = yMax - itemHeight;
                    
                    string emblemUrl = data.SavedEmblems[i];
                    bool isEquipped = data.EquippedEmblem == emblemUrl;
                    
                    // Card background
                    string cardColor = isEquipped ? "0.15 0.3 0.15 1" : "0.15 0.15 0.18 1";
                    string borderColor = isEquipped ? "0.3 0.7 0.3 1" : "0.25 0.25 0.3 1";
                    
                    container.Add(new CuiPanel
                    {
                        Image = { Color = borderColor },
                        RectTransform = { AnchorMin = $"{xMin} {yMin}", AnchorMax = $"{xMax} {yMax}" }
                    }, "Content", $"Card{i}");
                    
                    container.Add(new CuiPanel
                    {
                        Image = { Color = cardColor },
                        RectTransform = { AnchorMin = "0.02 0.02", AnchorMax = "0.98 0.98" }
                    }, $"Card{i}", $"CardInner{i}");
                    
                    // Emblem preview image
                    container.Add(new CuiElement
                    {
                        Parent = $"CardInner{i}",
                        Components =
                        {
                            new CuiRawImageComponent { Url = emblemUrl, Color = "1 1 1 1" },
                            new CuiRectTransformComponent { AnchorMin = "0.05 0.35", AnchorMax = "0.95 0.95" }
                        }
                    });
                    
                    // Index label
                    container.Add(new CuiLabel
                    {
                        Text = { Text = $"#{i + 1}", FontSize = 10, Align = TextAnchor.UpperLeft, Color = "0.6 0.6 0.6 1" },
                        RectTransform = { AnchorMin = "0.05 0.85", AnchorMax = "0.3 0.95" }
                    }, $"CardInner{i}");
                    
                    // Equipped badge
                    if (isEquipped)
                    {
                        container.Add(new CuiLabel
                        {
                            Text = { Text = "✓ EQUIPPED", FontSize = 8, Align = TextAnchor.UpperRight, Color = "0.4 0.9 0.4 1" },
                            RectTransform = { AnchorMin = "0.5 0.85", AnchorMax = "0.95 0.95" }
                        }, $"CardInner{i}");
                    }
                    
                    // Buttons
                    if (!isEquipped)
                    {
                        container.Add(new CuiButton
                        {
                            Button = { Color = "0.2 0.5 0.2 1", Command = $"emblem.equip {i}" },
                            RectTransform = { AnchorMin = "0.05 0.05", AnchorMax = "0.55 0.25" },
                            Text = { Text = "EQUIP", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                        }, $"CardInner{i}");
                    }
                    else
                    {
                        container.Add(new CuiPanel
                        {
                            Image = { Color = "0.3 0.3 0.3 1" },
                            RectTransform = { AnchorMin = "0.05 0.05", AnchorMax = "0.55 0.25" }
                        }, $"CardInner{i}", $"EquippedBtn{i}");
                        
                        container.Add(new CuiLabel
                        {
                            Text = { Text = "✓ ACTIVE", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "0.6 0.6 0.6 1" },
                            RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                        }, $"EquippedBtn{i}");
                    }
                    
                    container.Add(new CuiButton
                    {
                        Button = { Color = "0.5 0.2 0.2 1", Command = $"emblem.delete {i}" },
                        RectTransform = { AnchorMin = "0.6 0.05", AnchorMax = "0.95 0.25" },
                        Text = { Text = "🗑", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                    }, $"CardInner{i}");
                }
            }
            
            // Footer
            container.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.08 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.06" }
            }, "MainPanel", "Footer");
            
            container.Add(new CuiLabel
            {
                Text = { Text = $"Total: {data.SavedEmblems.Count} emblems  •  Use /emblem to create new", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.5 0.5 0.5 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, "Footer");
            
            CuiHelper.AddUi(player, container);
        }
        
        #endregion
        
        #region API (for other plugins)
        
        // Get equipped emblem URL for a player
        private string GetEquippedEmblem(ulong steamId)
        {
            var data = GetPlayerData(steamId);
            return data.EquippedEmblem;
        }
        
        // Get all emblems for a player
        private List<string> GetAllEmblems(ulong steamId)
        {
            var data = GetPlayerData(steamId);
            return new List<string>(data.SavedEmblems);
        }
        
        // Check if player has any emblems
        private bool HasEmblems(ulong steamId)
        {
            var data = GetPlayerData(steamId);
            return data.SavedEmblems.Count > 0;
        }
        
        #endregion
    }
}
