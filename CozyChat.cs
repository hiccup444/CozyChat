using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;
using ExitGames.Client.Photon;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using System.Text.RegularExpressions;
using UnityEngine.SceneManagement;

namespace TextChatMod
{
    [BepInPlugin("com.hiccup.textchat", "CozyChat", "1.3.0")]
    public class TextChatPlugin : BaseUnityPlugin
    {
        public static TextChatPlugin Instance;
        private static PlayerConnectionLog connectionLog;
        public static bool isTyping = false;
        private static GameObject chatInputUI;
        private static TMP_InputField chatInputField;
        private static List<MonoBehaviour> disabledControllers = new List<MonoBehaviour>();
        private static List<CharacterController> disabledCharacterControllers = new List<CharacterController>();
        private static List<Rigidbody> frozenRigidbodies = new List<Rigidbody>();

        public static void LogInfo(string message)
        {
            Instance?.Logger.LogInfo(message);
        }

        private static ConfigEntry<KeyCode> chatKey;
        private static ConfigEntry<Color> chatMessageColor;
        private static ConfigEntry<float> messageTimeout;
        private static ConfigEntry<int> maxMessageLength;
        private static ConfigEntry<bool> hideDeadMessages;
        private static ConfigEntry<float> chatProximityRange;


        void Awake()
        {
            Instance = this;

            chatKey = Config.Bind("General", "ChatKey", KeyCode.T, "Key to open chat");
            chatMessageColor = Config.Bind("General", "MessageColor", new Color(1f, 1f, 1f, 1f), "Color of chat messages");
            messageTimeout = Config.Bind("General", "MessageTimeout", 10f, "How long messages stay on screen");
            maxMessageLength = Config.Bind("General", "MaxMessageLength", 100, "Maximum length of chat messages");
            hideDeadMessages = Config.Bind("General", "HideDeadMessages", true, "If enabled, alive players will not see chat from dead/unconscious players.");
            chatProximityRange = Config.Bind("General", "ChatProximityRange", 0f, "Maximum distance for receiving chat messages. 0 = unlimited range.");
            SceneManager.activeSceneChanged += OnSceneChanged;

            Harmony harmony = new Harmony("com.yourname.textchat");
            harmony.PatchAll();

            Logger.LogInfo("Text Chat Mod loaded!");
            Logger.LogInfo($"Chat key set to: {chatKey.Value}");

            DontDestroyOnLoad(gameObject);
        }

        void Update()
        {
            if (!connectionLog)
            {
                connectionLog = FindFirstObjectByType<PlayerConnectionLog>();
                if (connectionLog)
                {
                    Logger.LogInfo("Found PlayerConnectionLog!");
                    CreateChatUI();
                }
                else
                {
                    var allLogs = FindObjectsByType<PlayerConnectionLog>(FindObjectsSortMode.None);
                    if (allLogs.Length > 0)
                    {
                        connectionLog = allLogs[0];
                        Logger.LogInfo($"Found PlayerConnectionLog using FindObjectsByType! Count: {allLogs.Length}");
                        CreateChatUI();
                    }
                }
                return;
            }

            if (Input.GetKeyDown(chatKey.Value) && !isTyping)
            {
                Logger.LogInfo("Chat key pressed!");
                OpenChat();
            }
            else if (isTyping)
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    SendMessage();
                }
                else if (Input.GetKeyDown(KeyCode.Escape))
                {
                    CloseChat();
                }

                if (chatInputField != null && !chatInputField.isFocused)
                {
                    chatInputField.Select();
                    chatInputField.ActivateInputField();
                }

                if (chatInputField != null && !chatInputField.isFocused)
                {
                    string inputString = Input.inputString;
                    if (!string.IsNullOrEmpty(inputString))
                    {
                        foreach (char c in inputString)
                        {
                            if (c == '\b') // Backspace
                            {
                                if (chatInputField.text.Length > 0)
                                {
                                    chatInputField.text = chatInputField.text.Substring(0, chatInputField.text.Length - 1);
                                }
                            }
                            else if (c == '\n' || c == '\r') // Enter
                            {
                            }
                            else if (c != '\0' && !char.IsControl(c))
                            {
                                chatInputField.text += c;
                            }
                        }
                    }
                }
            }
            // Freeze/unfreeze player movement every frame based on typing state
            try
            {
                var ch = Character.localCharacter;
                if (ch != null && ch.refs != null && ch.data != null)
                {
                    if (isTyping)
                    {
                        ch.refs.movement.movementModifier = 0f;
                        ch.data.jumpsRemaining = 0;
                    }
                    else
                    {
                        ch.refs.movement.movementModifier = 1f;
                        if (ch.data.jumpsRemaining == 0)
                            ch.data.jumpsRemaining = 1;
                    }
                }
            }
            catch { /* Ignore errors if Character is not ready */ }
        }

        void Start()
        {
            Logger.LogInfo("TextChatPlugin Start() called");

            StartCoroutine(FindConnectionLogCoroutine());

            if (!eventHandlerRegistered && PhotonNetwork.NetworkingClient != null)
            {
                PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
                eventHandlerRegistered = true;
                Logger.LogInfo("Registered Photon event handler");
            }
        }

        private System.Collections.IEnumerator FindConnectionLogCoroutine()
        {
            float searchTime = 0f;
            while (!connectionLog && searchTime < 30f)
            {
                connectionLog = FindFirstObjectByType<PlayerConnectionLog>();
                if (!connectionLog)
                {
                    var go = GameObject.Find("PlayerConnectionLog");
                    if (go) connectionLog = go.GetComponent<PlayerConnectionLog>();
                }

                if (connectionLog)
                {
                    Logger.LogInfo("Found PlayerConnectionLog via coroutine!");
                    CreateChatUI();
                    yield break;
                }

                searchTime += 0.5f;
                yield return new WaitForSeconds(0.5f);
            }

            if (!connectionLog)
            {
                Logger.LogWarning("Could not find PlayerConnectionLog after 30 seconds!");
            }
        }

        private static bool startupMessageShown = false;

        private void OnSceneChanged(Scene oldScene, Scene newScene)
        {
            if (startupMessageShown) return;

            if (newScene.name.ToLower().Contains("airport"))
            {
                startupMessageShown = true;
                Logger.LogInfo("Airport scene detected, showing CozyChat startup message...");

                StartCoroutine(ShowStartupMessage());
            }
        }

        private System.Collections.IEnumerator ShowStartupMessage()
        {
            yield return new WaitForSeconds(0.25f); // Give time for UI to appear

            DisplayChatMessage("[CozyChat]", $"CozyChat v{Info.Metadata.Version} successfully loaded.");
            yield return new WaitForSeconds(0.25f);
            DisplayChatMessage("[CozyChat]", $"Use /help or /commands to change options.");
        }

        private void CreateChatUI()
        {
            try
            {
                Logger.LogInfo("Creating chat UI…");

                Canvas parentCanvas = connectionLog.GetComponentInParent<Canvas>();
                if (!parentCanvas)
                {
                    Logger.LogError("ChatUI: Parent canvas not found!");
                    return;
                }

                GameObject originalBar = GameObject.Find("/GAME/GUIManager/Canvas_HUD/BarGroup/Bar");
                if (originalBar == null)
                {
                    Logger.LogWarning("ChatUI: Health bar not found, falling back to plain box.");
                }

                chatInputUI = new GameObject("ChatInputUI");
                chatInputUI.transform.SetParent(parentCanvas.transform, false);

                RectTransform rootRT = chatInputUI.AddComponent<RectTransform>();
                rootRT.anchorMin = new Vector2(0, 0);
                rootRT.anchorMax = new Vector2(1, 1);
                rootRT.offsetMin = Vector2.zero;
                rootRT.offsetMax = Vector2.zero;

                GameObject backgroundObj;

                if (originalBar != null)
                {
                    backgroundObj = Instantiate(originalBar, chatInputUI.transform);
                    backgroundObj.name = "ChatBackground";

                    var staminaBar = backgroundObj.GetComponent<StaminaBar>();
                    if (staminaBar != null)
                    {
                        Destroy(staminaBar);
                    }

                    string[] nestedPathsToDelete = {
                        "MoraleBoost",
                        "FullBar",
                        "OutlineOverflowLine",
                        "LayoutGroup"
                    };

                    foreach (string path in nestedPathsToDelete)
                    {
                        var child = backgroundObj.transform.Find(path);
                        if (child != null)
                        {
                            Destroy(child.gameObject);
                        }
                    }
                }
                else
                {
                    backgroundObj = new GameObject("ChatBackground");
                    backgroundObj.transform.SetParent(chatInputUI.transform, false);
                    var img = backgroundObj.AddComponent<UnityEngine.UI.Image>();
                    img.color = new Color(0, 0, 0, 0.75f);
                }

                RectTransform bgRT = backgroundObj.GetComponent<RectTransform>();
                if (bgRT == null) bgRT = backgroundObj.AddComponent<RectTransform>();
                bgRT.anchorMin = new Vector2(0, 0);
                bgRT.anchorMax = new Vector2(0, 0);
                bgRT.pivot = new Vector2(0, 0);
                bgRT.anchoredPosition = new Vector2(69, 10);
                bgRT.sizeDelta = new Vector2(500, 40);

                GameObject inputGO = new GameObject("ChatInputField");
                inputGO.transform.SetParent(backgroundObj.transform, false);

                RectTransform inputRT = inputGO.AddComponent<RectTransform>();
                inputRT.anchorMin = new Vector2(0, 0);
                inputRT.anchorMax = new Vector2(1, 1);
                inputRT.offsetMin = new Vector2(8, 4);
                inputRT.offsetMax = new Vector2(-8, -4);

                GameObject viewportGO = new GameObject("Viewport");
                viewportGO.transform.SetParent(inputGO.transform, false);

                RectTransform vpRT = viewportGO.AddComponent<RectTransform>();
                vpRT.anchorMin = Vector2.zero;
                vpRT.anchorMax = Vector2.one;
                vpRT.offsetMin = Vector2.zero;
                vpRT.offsetMax = Vector2.zero;
                viewportGO.AddComponent<UnityEngine.UI.RectMask2D>();

                GameObject textGO = new GameObject("Text");
                textGO.transform.SetParent(viewportGO.transform, false);

                RectTransform textRT = textGO.AddComponent<RectTransform>();
                textRT.anchorMin = Vector2.zero;
                textRT.anchorMax = Vector2.one;
                textRT.offsetMin = Vector2.zero;
                textRT.offsetMax = Vector2.zero;

                var text = textGO.AddComponent<TextMeshProUGUI>();
                text.text = string.Empty;
                text.fontSize = 20;
                text.color = Color.white;
                text.alignment = TextAlignmentOptions.TopLeft;

                GameObject placeholderGO = new GameObject("Placeholder");
                placeholderGO.transform.SetParent(viewportGO.transform, false);

                RectTransform phRT = placeholderGO.AddComponent<RectTransform>();
                phRT.anchorMin = Vector2.zero;
                phRT.anchorMax = Vector2.one;
                phRT.offsetMin = Vector2.zero;
                phRT.offsetMax = Vector2.zero;

                var placeholder = placeholderGO.AddComponent<TextMeshProUGUI>();
                placeholder.text = "Type a message and press Enter to send...";
                placeholder.fontSize = 20;
                placeholder.alignment = TextAlignmentOptions.TopLeft;
                placeholder.fontStyle = FontStyles.Italic;
                placeholder.color = new Color(1, 1, 1, 0.55f);

                TMP_FontAsset logFont = connectionLog.GetComponentInChildren<TextMeshProUGUI>()?.font;
                if (logFont != null)
                {
                    text.font = logFont;
                    placeholder.font = logFont;
                }

                chatInputField = inputGO.AddComponent<TMP_InputField>();
                chatInputField.textViewport = vpRT;
                chatInputField.textComponent = text;
                chatInputField.placeholder = placeholder;
                chatInputField.characterLimit = maxMessageLength.Value;
                chatInputField.contentType = TMP_InputField.ContentType.Standard;
                chatInputField.lineType = TMP_InputField.LineType.SingleLine;
                chatInputField.richText = false;
                chatInputField.selectionColor = new Color(0.5f, 0.5f, 1f, 0.5f);
                chatInputField.caretColor = Color.white;
                chatInputField.caretWidth = 2;
                chatInputField.caretBlinkRate = 0.85f;

                chatInputUI.SetActive(false);

                Logger.LogInfo("Chat UI created successfully!");
            }
            catch (Exception e)
            {
                Logger.LogError($"Error creating chat UI: {e}");
            }
        }


        private TMP_Text CreatePlaceholder(Transform parent)
        {
            var placeholderObj = new GameObject("Placeholder");
            placeholderObj.transform.SetParent(parent, false);
            var placeholder = placeholderObj.AddComponent<TextMeshProUGUI>();
            placeholder.text = "Press Enter to send...";
            placeholder.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            placeholder.fontSize = 14;

            var rect = placeholderObj.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(5, 5);
            rect.offsetMax = new Vector2(-5, -5);

            return placeholder;
        }

        private void OpenChat()
        {
            if (!chatInputUI || !chatInputField) return;

            Logger.LogInfo("Opening chat UI...");

            isTyping = true;
            chatInputUI.SetActive(true);

            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                var eventSystemObj = new GameObject("EventSystem");
                eventSystem = eventSystemObj.AddComponent<EventSystem>();
                eventSystemObj.AddComponent<StandaloneInputModule>();
                Logger.LogInfo("Created EventSystem");
            }

            chatInputField.text = "";
            chatInputField.interactable = true;
            chatInputField.Select();
            chatInputField.ActivateInputField();
            chatInputField.ForceLabelUpdate();

            eventSystem.SetSelectedGameObject(chatInputField.gameObject);

            StartCoroutine(ForceFocusInputField());

            Logger.LogInfo($"Chat UI active: {chatInputUI.activeSelf}, Input field interactable: {chatInputField.interactable}");
            Logger.LogInfo($"Input field selected: {chatInputField.isFocused}");
            Logger.LogInfo($"EventSystem selected object: {eventSystem.currentSelectedGameObject?.name}");

            try
            {
                var ch = Character.localCharacter;
                if (ch != null && ch.refs != null && ch.data != null)
                {
                    ch.refs.movement.movementModifier = 0f;
                    ch.data.jumpsRemaining = 0;
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Failed to disable movement: {e.Message}");
            }
        }


        private System.Collections.IEnumerator ForceFocusInputField()
        {
            yield return null;
            if (chatInputField != null && isTyping)
            {
                chatInputField.Select();
                chatInputField.ActivateInputField();
            }
        }

        private void CloseChat()
        {
            if (!chatInputUI) return;

            Logger.LogInfo("Closing chat UI...");

            isTyping = false;
            chatInputUI.SetActive(false);

            chatInputField.DeactivateInputField();

            try
            {
                var ch = Character.localCharacter;
                if (ch != null && ch.refs != null && ch.data != null)
                {
                    ch.refs.movement.movementModifier = 1f;
                    ch.data.jumpsRemaining = 1;
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Failed to restore movement: {e.Message}");
            }
        }

        private static bool HandleChatCommand(string message)
        {
            if (!message.StartsWith("/"))
                return false;

            var args = message.Trim().Split(' ');

            switch (args[0].ToLower())
            {
                case "/help":
                case "/commands":
                    DisplayChatMessage("[ChatMod]", "Available commands:\n" +
                        "/help or /commands - Show this list\n" +
                        "/config - Show current config values\n" +
                        "/chatProximityRange value - Set chat proximity range (0 = unlimited)\n" +
                        "/hideDeadMessages true/false - Show or hide dead/unconscious player chat\n" +
                        "/reset - Reset all configs to default values");
                    return true;

                case "/config":
                    DisplayChatMessage("[ChatMod]", $"Current config:\n" +
                        $"- ChatProximityRange: {chatProximityRange.Value}\n" +
                        $"- HideDeadMessages: {hideDeadMessages.Value}");
                    return true;

                case "/chatproximityrange":
                    if (args.Length >= 2 && float.TryParse(args[1], out float range))
                    {
                        chatProximityRange.Value = range;
                        TextChatPlugin.Instance.Config.Save();
                        DisplayChatMessage("[ChatMod]", $"Set chat proximity range to {range}");
                    }
                    else
                    {
                        DisplayChatMessage("[ChatMod]", "Usage: /chatProximityRange value");
                    }
                    return true;

                case "/hidedeadmessages":
                    if (args.Length >= 2 && bool.TryParse(args[1], out bool hideDead))
                    {
                        hideDeadMessages.Value = hideDead;
                        TextChatPlugin.Instance.Config.Save();
                        DisplayChatMessage("[ChatMod]", $"Set hide dead messages to {hideDead}");
                    }
                    else
                    {
                        DisplayChatMessage("[ChatMod]", "Usage: /hideDeadMessages true/false");
                    }
                    return true;

                case "/reset":
                    chatProximityRange.Value = (float)chatProximityRange.DefaultValue;
                    hideDeadMessages.Value = (bool)hideDeadMessages.DefaultValue;
                    TextChatPlugin.Instance.Config.Save();
                    DisplayChatMessage("[ChatMod]", "Config reset to default values.");
                    return true;

                default:
                    DisplayChatMessage("[ChatMod]", $"Unknown command: {args[0]}");
                    return true;
            }
        }



        private void SendMessage()
        {
            string message = chatInputField.text.Trim();
            if (string.IsNullOrEmpty(message))
            {
                CloseChat();
                return;
            }

            if (HandleChatCommand(message))
            {
                CloseChat();
                return;
            }

            bool isDead = false;
            try
            {
                isDead = Character.localCharacter?.data?.dead ?? false;
            }
            catch { }

            object[] peakPayload = new object[]
            {
        PhotonNetwork.LocalPlayer.NickName,
        message,
        PhotonNetwork.LocalPlayer.UserId,
        isDead.ToString()
            };

            RaiseEventOptions opts = new RaiseEventOptions { Receivers = ReceiverGroup.All };

            PhotonNetwork.RaiseEvent(CHAT_EVENT_CODE_PEAK, peakPayload, opts, SendOptions.SendReliable);

            CloseChat();
        }


        private static Color GetPlayerColorFromCharacter(string playerName)
        {
            try
            {
                foreach (var ch in GameObject.FindObjectsByType<Character>(FindObjectsSortMode.None))
                {
                    var nick = ch.photonView?.Owner?.NickName;
                    if (!string.IsNullOrEmpty(nick) && nick == playerName)
                    {
                        return ch.refs.customization.PlayerColor;
                    }
                }
            }
            catch (Exception e)
            {
                TextChatPlugin.Instance.Logger.LogWarning($"Failed to find player color for {playerName}: {e.Message}");
            }

            return Color.white;
        }


        public static void DisplayChatMessage(string playerName, string message)
        {
            if (!connectionLog) return;

            Color userColor = GetPlayerColorFromCharacter(playerName);

            var addMessageMethod = typeof(PlayerConnectionLog).GetMethod("AddMessage", BindingFlags.NonPublic | BindingFlags.Instance);
            var getColorTagMethod = typeof(PlayerConnectionLog).GetMethod("GetColorTag", BindingFlags.NonPublic | BindingFlags.Instance);

            string nameColorTag = (string)getColorTagMethod.Invoke(connectionLog, new object[] { userColor });
            string msgColorTag = (string)getColorTagMethod.Invoke(connectionLog, new object[] { chatMessageColor.Value });

            string cleanMessage = Regex.Replace(message, @"<.*?>", "", RegexOptions.Singleline);
            string formatted = $"{nameColorTag}{playerName}</color>{msgColorTag}: {cleanMessage}</color>";

            addMessageMethod.Invoke(connectionLog, new object[] { formatted });
        }

        private const byte CHAT_EVENT_CODE_LEGACY = 77;
        private const byte CHAT_EVENT_CODE_PEAK = 81;
        private static bool eventHandlerRegistered = false;

        void OnDestroy()
        {
            if (eventHandlerRegistered && PhotonNetwork.NetworkingClient != null)
            {
                PhotonNetwork.NetworkingClient.EventReceived -= OnEvent;
                eventHandlerRegistered = false;
            }
        }

        private static Vector3 GetCharacterPosition(Character character)
        {
            try
            {
                if (character.Head != null)
                    return character.Head;
            }
            catch { }

            try
            {
                return character.Center;
            }
            catch { }

            return character.transform.position;
        }

        private static void OnEvent(EventData ev)
        {
            switch (ev.Code)
            {
                case CHAT_EVENT_CODE_PEAK:
                    {
                        var data = (object[])ev.CustomData;
                        string nick = data[0]?.ToString() ?? "???";
                        string msg = data[1]?.ToString() ?? "";
                        string userId = data[2]?.ToString() ?? "";
                        bool isDead = bool.TryParse(data[3]?.ToString(), out var d) && d;

                        Instance.Logger.LogInfo($"[ChatMod] Received message from '{nick}' (UserId: {userId}), dead={isDead}, message={msg}");

                        if (isDead)
                            nick = "<color=red>[DEAD]</color> " + nick;

                        try
                        {
                            var local = Character.localCharacter;
                            if (local != null && !local.data.dead && !local.data.fullyPassedOut)
                            {
                                if (hideDeadMessages.Value && isDead)
                                {
                                    Instance.Logger.LogInfo($"[ChatMod] Hiding dead message from '{nick}'");
                                    return;
                                }

                                if (chatProximityRange.Value > 0f)
                                {
                                    Character sender = FindCharacterByUserId(userId);
                                    if (sender == null)
                                    {
                                        Instance.Logger.LogWarning($"[ChatMod] Could not find Character for UserId '{userId}'");
                                    }
                                    else
                                    {
                                        float distance = Vector3.Distance(GetCharacterPosition(local), GetCharacterPosition(sender));
                                        Instance.Logger.LogInfo($"[ChatMod] Distance to '{nick}': {distance} (limit {chatProximityRange.Value})");

                                        if (sender != local && distance > chatProximityRange.Value)
                                        {
                                            Instance.Logger.LogInfo($"[ChatMod] Hiding message from '{nick}' due to distance");
                                            return;
                                        }
                                    }

                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Instance.Logger.LogWarning($"[ChatMod] Error filtering chat message: {e}");
                        }

                        DisplayChatMessage(nick, msg);
                        break;
                    }

                case CHAT_EVENT_CODE_LEGACY:
                    {
                        var data = (object[])ev.CustomData;
                        DisplayChatMessage(data[0]?.ToString() ?? "???", data[1]?.ToString() ?? "");
                        break;
                    }
            }
        }


        private static Character FindCharacterByUserId(string userId)
        {
            foreach (var ch in GameObject.FindObjectsByType<Character>(FindObjectsSortMode.None))
            {
                if (ch.photonView?.Owner?.UserId == userId)
                    return ch;
            }
            return null;
        }


        private static Character FindCharacterByName(string name)
        {
            foreach (var ch in GameObject.FindObjectsByType<Character>(FindObjectsSortMode.None))
            {
                if (ch.photonView != null && ch.photonView.Owner != null && ch.photonView.Owner.NickName == name)
                    return ch;
            }
            return null;
        }

        private static void RemoveFirstMessage(PlayerConnectionLog instance)
        {
            var currentLogField = typeof(PlayerConnectionLog).GetField("currentLog", BindingFlags.NonPublic | BindingFlags.Instance);
            var currentLog = (List<string>)currentLogField.GetValue(instance);

            if (currentLog.Count > 0)
            {
                currentLog.RemoveAt(0);
                var rebuildMethod = typeof(PlayerConnectionLog).GetMethod("RebuildString", BindingFlags.NonPublic | BindingFlags.Instance);
                rebuildMethod.Invoke(instance, null);
            }
        }

        [HarmonyPatch(typeof(PlayerConnectionLog), "TimeoutMessageRoutine")]
        class TimeoutMessagePatch
        {
            static bool Prefix(PlayerConnectionLog __instance, ref System.Collections.IEnumerator __result)
            {
                __result = CustomTimeoutRoutine(__instance);
                return false;
            }

            static System.Collections.IEnumerator CustomTimeoutRoutine(PlayerConnectionLog instance)
            {
                yield return new WaitForSeconds(TextChatPlugin.messageTimeout.Value);

                RemoveFirstMessage(instance);
            }
        }
    }

    [HarmonyPatch]
    public static class InputPatches
    {
        public static bool IsTyping => TextChatPlugin.isTyping;

        [HarmonyPatch(typeof(Input), "GetButton", typeof(string))]
        [HarmonyPrefix]
        static bool GetButtonPrefix(string buttonName, ref bool __result)
        {
            if (IsTyping && (buttonName.Contains("Fire") || buttonName.Contains("Jump") || buttonName.Contains("Crouch")))
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetButtonDown", typeof(string))]
        [HarmonyPrefix]
        static bool GetButtonDownPrefix(string buttonName, ref bool __result)
        {
            if (IsTyping && (buttonName.Contains("Fire") || buttonName.Contains("Jump") || buttonName.Contains("Crouch")))
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetAxis", typeof(string))]
        [HarmonyPrefix]
        static bool GetAxisPrefix(string axisName, ref float __result)
        {
            if (IsTyping && (axisName == "Horizontal" || axisName == "Vertical" || axisName.Contains("Mouse")))
            {
                __result = 0f;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), "GetAxisRaw", typeof(string))]
        [HarmonyPrefix]
        static bool GetAxisRawPrefix(string axisName, ref float __result)
        {
            if (IsTyping && (axisName == "Horizontal" || axisName == "Vertical" || axisName.Contains("Mouse")))
            {
                __result = 0f;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(EmoteWheel), "OnEnable")]
        public static class EmoteWheel_OnEnable_Patch
        {
            static bool Prefix(EmoteWheel __instance)
            {
                if (TextChatPlugin.isTyping)
                {
                    __instance.gameObject.SetActive(false);
                    return false;
                }

                return true;
            }
        }
        [HarmonyPatch]
        public static class CharacterInteractible_CanBeCarried_Patch
        {
            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(CharacterInteractible), "CanBeCarried");
            }

            static void Postfix(ref bool __result)
            {
                if (TextChatMod.TextChatPlugin.isTyping)
                {
                    __result = false;
                }
            }
        }
    }
}
