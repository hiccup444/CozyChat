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

namespace TextChatMod
{
    [BepInPlugin("com.hiccup.textchat", "CozyChat", "1.0.0")]
    public class TextChatPlugin : BaseUnityPlugin
    {
        public static TextChatPlugin Instance; // Made public so InputPatches can access it
        private static PlayerConnectionLog connectionLog;
        public static bool isTyping = false; // Made public so InputPatches can access it
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

        void Awake()
        {
            Instance = this;

            chatKey = Config.Bind("General", "ChatKey", KeyCode.T, "Key to open chat");
            chatMessageColor = Config.Bind("General", "MessageColor", new Color(1f, 1f, 1f, 1f), "Color of chat messages");
            messageTimeout = Config.Bind("General", "MessageTimeout", 10f, "How long messages stay on screen");
            maxMessageLength = Config.Bind("General", "MaxMessageLength", 100, "Maximum length of chat messages");

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
                // Try to find the PlayerConnectionLog instance
                connectionLog = FindFirstObjectByType<PlayerConnectionLog>();
                if (connectionLog)
                {
                    Logger.LogInfo("Found PlayerConnectionLog!");
                    CreateChatUI();
                }
                else
                {
                    // Try alternative search methods
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

            // Handle chat input
            if (Input.GetKeyDown(chatKey.Value) && !isTyping)
            {
                Logger.LogInfo("Chat key pressed!");
                OpenChat();
            }
            else if (isTyping)
            {
                // Handle input while typing
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    SendMessage();
                }
                else if (Input.GetKeyDown(KeyCode.Escape))
                {
                    CloseChat();
                }

                // Keep trying to maintain focus
                if (chatInputField != null && !chatInputField.isFocused)
                {
                    chatInputField.Select();
                    chatInputField.ActivateInputField();
                }

                // Manual input capture as fallback
                if (chatInputField != null && !chatInputField.isFocused)
                {
                    // Capture any text input manually
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
                                // Already handled above
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

            // Try to find connection log immediately
            StartCoroutine(FindConnectionLogCoroutine());

            // Register Photon event handler
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
                    // Try with regular FindObjectOfType as fallback
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

        private void CreateChatUI()
        {
            try
            {
                Logger.LogInfo("Creating chat UI…");

                // ───────────────────────────────────────────────────────────
                // 1. Locate parent canvas + the health-bar template
                // ───────────────────────────────────────────────────────────
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

                // ───────────────────────────────────────────────────────────
                // 2. Root container
                // ───────────────────────────────────────────────────────────
                chatInputUI = new GameObject("ChatInputUI");
                chatInputUI.transform.SetParent(parentCanvas.transform, false);

                RectTransform rootRT = chatInputUI.AddComponent<RectTransform>();
                rootRT.anchorMin = new Vector2(0, 0);
                rootRT.anchorMax = new Vector2(1, 1);
                rootRT.offsetMin = Vector2.zero;
                rootRT.offsetMax = Vector2.zero;

                // ───────────────────────────────────────────────────────────
                // 3. Background (clone the bar or make a box)
                // ───────────────────────────────────────────────────────────
                GameObject backgroundObj;

                if (originalBar != null)
                {
                    backgroundObj = Instantiate(originalBar, chatInputUI.transform);
                    backgroundObj.name = "ChatBackground";

                    // Remove StaminaBar component from ChatBackground
                    var staminaBar = backgroundObj.GetComponent<StaminaBar>();
                    if (staminaBar != null)
                    {
                        Destroy(staminaBar);
                    }

                    // Clean up unused HUD children inside ChatBackground
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
                    // Fallback – simple dark box
                    backgroundObj = new GameObject("ChatBackground");
                    backgroundObj.transform.SetParent(chatInputUI.transform, false);
                    var img = backgroundObj.AddComponent<UnityEngine.UI.Image>();
                    img.color = new Color(0, 0, 0, 0.75f);
                }

                RectTransform bgRT = backgroundObj.GetComponent<RectTransform>();
                if (bgRT == null) bgRT = backgroundObj.AddComponent<RectTransform>();
                bgRT.anchorMin = new Vector2(0, 0);
                bgRT.anchorMax = new Vector2(0, 0); // no horizontal stretch
                bgRT.pivot = new Vector2(0, 0);
                bgRT.anchoredPosition = new Vector2(69, 10); // ⬅ push it right
                bgRT.sizeDelta = new Vector2(500, 40);       // same width

                // ───────────────────────────────────────────────────────────
                // 4. Input-field hierarchy
                // ───────────────────────────────────────────────────────────
                GameObject inputGO = new GameObject("ChatInputField");
                inputGO.transform.SetParent(backgroundObj.transform, false);

                RectTransform inputRT = inputGO.AddComponent<RectTransform>();
                inputRT.anchorMin = new Vector2(0, 0);
                inputRT.anchorMax = new Vector2(1, 1);
                inputRT.offsetMin = new Vector2(8, 4);   // padding left / bottom
                inputRT.offsetMax = new Vector2(-8, -4); // padding right / top

                // NOTE:  we intentionally do NOT add an Image here (avoids grey overlay)

                // Viewport (TMP requirement)
                GameObject viewportGO = new GameObject("Viewport");
                viewportGO.transform.SetParent(inputGO.transform, false);

                RectTransform vpRT = viewportGO.AddComponent<RectTransform>();
                vpRT.anchorMin = Vector2.zero;
                vpRT.anchorMax = Vector2.one;
                vpRT.offsetMin = Vector2.zero;
                vpRT.offsetMax = Vector2.zero;
                viewportGO.AddComponent<UnityEngine.UI.RectMask2D>();

                // Text component
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

                // Placeholder
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

                // ───────────────────────────────────────────────────────────
                // 5. Match the font used in the scroll-log
                // ───────────────────────────────────────────────────────────
                TMP_FontAsset logFont = connectionLog.GetComponentInChildren<TextMeshProUGUI>()?.font;
                if (logFont != null)
                {
                    text.font = logFont;
                    placeholder.font = logFont;
                }

                // ───────────────────────────────────────────────────────────
                // 6. Configure TMP_InputField
                // ───────────────────────────────────────────────────────────
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

            // Clear and focus the input field
            chatInputField.text = "";
            chatInputField.interactable = true;
            chatInputField.Select();
            chatInputField.ActivateInputField();
            chatInputField.ForceLabelUpdate();

            // Set as selected game object
            eventSystem.SetSelectedGameObject(chatInputField.gameObject);

            // Force focus
            StartCoroutine(ForceFocusInputField());

            Logger.LogInfo($"Chat UI active: {chatInputUI.activeSelf}, Input field interactable: {chatInputField.interactable}");
            Logger.LogInfo($"Input field selected: {chatInputField.isFocused}");
            Logger.LogInfo($"EventSystem selected object: {eventSystem.currentSelectedGameObject?.name}");

            // Disable movement and jumping
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
            yield return null; // Wait one frame
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

            // Restore movement and jumping
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

        private void SendMessage()
        {
            string message = chatInputField.text.Trim();
            if (string.IsNullOrEmpty(message))
            {
                CloseChat();
                return;
            }

            // Build the Peak N Speak payload
            bool isDead = false;
            try
            {
                // works if the Character class is present in this game
                isDead = Character.localCharacter?.data?.dead ?? false;
            }
            catch { }

            object[] peakPayload = new object[]
            {
        PhotonNetwork.LocalPlayer.NickName,   // [0] username
        message,                              // [1] text
        PhotonNetwork.LocalPlayer.UserId,     // [2] userid
        isDead.ToString()                     // [3] "True"/"False"
            };

            RaiseEventOptions opts = new RaiseEventOptions { Receivers = ReceiverGroup.All };

            // Send in Peak N Speak format so they can read us
            PhotonNetwork.RaiseEvent(CHAT_EVENT_CODE_PEAK, peakPayload, opts, SendOptions.SendReliable);

            // (optional) still send our legacy format for old clients
            /* object[] legacy = new object[] { peakPayload[0], message };
               PhotonNetwork.RaiseEvent(CHAT_EVENT_CODE_LEGACY, legacy, opts, SendOptions.SendReliable); */

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

            // Try to get player's actual skin color
            Color userColor = GetPlayerColorFromCharacter(playerName);

            var addMessageMethod = typeof(PlayerConnectionLog).GetMethod("AddMessage", BindingFlags.NonPublic | BindingFlags.Instance);
            var getColorTagMethod = typeof(PlayerConnectionLog).GetMethod("GetColorTag", BindingFlags.NonPublic | BindingFlags.Instance);

            string nameColorTag = (string)getColorTagMethod.Invoke(connectionLog, new object[] { userColor });
            string msgColorTag = (string)getColorTagMethod.Invoke(connectionLog, new object[] { chatMessageColor.Value });

            string cleanMessage = Regex.Replace(message, @"<.*?>", "", RegexOptions.Singleline);
            string formatted = $"{nameColorTag}{playerName}</color>{msgColorTag}: {cleanMessage}</color>";

            addMessageMethod.Invoke(connectionLog, new object[] { formatted });
        }

        // Photon event handling
        private const byte CHAT_EVENT_CODE_LEGACY = 77; // old event code
        private const byte CHAT_EVENT_CODE_PEAK = 81;   // Peak N Speak
        private static bool eventHandlerRegistered = false;

        void OnDestroy()
        {
            // Unregister event handler
            if (eventHandlerRegistered && PhotonNetwork.NetworkingClient != null)
            {
                PhotonNetwork.NetworkingClient.EventReceived -= OnEvent;
                eventHandlerRegistered = false;
            }
        }

        private static void OnEvent(EventData ev)
        {
            switch (ev.Code)
            {
                case CHAT_EVENT_CODE_PEAK:
                    {
                        // Peak N Speak: [nick, msg, userid, isDead]
                        var data = (object[])ev.CustomData;
                        string nick = data[0]?.ToString() ?? "???";
                        string msg = data[1]?.ToString() ?? "";
                        bool isDead = bool.TryParse(data[3]?.ToString(), out var d) && d;

                        if (isDead) nick = "<color=red>[DEAD]</color> " + nick;
                        DisplayChatMessage(nick, msg);
                        break;
                    }

                case CHAT_EVENT_CODE_LEGACY:
                    {
                        // Your old mod: [nick, msg]
                        var data = (object[])ev.CustomData;
                        DisplayChatMessage(data[0]?.ToString() ?? "???", data[1]?.ToString() ?? "");
                        break;
                    }
            }
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

        // Patch to modify the timeout duration for chat messages
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
                // Wait full timeout duration
                yield return new WaitForSeconds(TextChatPlugin.messageTimeout.Value);

                // Remove first message from the log
                RemoveFirstMessage(instance);
            }
        }
    }

    // Remove or simplify the InputPatches - we don't need to block ALL input
    // Just block specific keys that the game uses for actions
    [HarmonyPatch]
    public static class InputPatches
    {
        public static bool IsTyping => TextChatPlugin.isTyping;

        // Only block specific game controls, not all input
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

        // Block movement axes
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
        // Harmony patch to prevent EmoteWheel from opening while typing
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
    }
}
