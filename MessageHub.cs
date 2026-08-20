using System;
using System.Collections.Generic;
using UnityEngine;

// ==========================================================================
// File d'attente de messages partagee entre les plugins (CollectiblesManager,
// CheckpointsManager, etc). Un seul GuiHost pour toute la partie : evite que
// deux messages ("X found!" et "Y found!") s'affichent en meme temps et se
// superposent. Chaque plugin appelle simplement MessageHub.ShowMessage(...),
// et les messages se jouent l'un apres l'autre (fondu inclus).
// ==========================================================================
public static class MessageHub
{
    private static GuiHost _guiHost;
    private static readonly Queue<string> _queue = new Queue<string>();
    private static bool _isShowing = false;

    // Pilote depuis l'exterieur (CheckpointsManager, via ses patches Harmony
    // existants sur PauseMenu) : indique si le jeu est actuellement en
    // pause, pour eviter d'afficher un message par-dessus le menu pause.
    public static bool IsPaused = false;

    public static void ShowMessage(string text)
    {
        EnsureGuiHost();
        _queue.Enqueue(text);
        TryShowNext();
    }

    private static void TryShowNext()
    {
        if (_isShowing || _queue.Count == 0) return;

        string next = _queue.Dequeue();
        _isShowing = true;
        _guiHost.Display(next, OnMessageFinished);
    }

    private static void OnMessageFinished()
    {
        _isShowing = false;
        TryShowNext();
    }

    private static void EnsureGuiHost()
    {
        if (_guiHost != null) return;

        GameObject go = new GameObject("MessageHub_GuiHost");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _guiHost = go.AddComponent<GuiHost>();
    }

    public class GuiHost : MonoBehaviour
    {
        private string _message = null;
        private float _messageTimer = 0f;
        private const float MessageDuration = 3f;
        private const float FadeOutDuration = 0.5f;
        private GUIStyle _style;
        private bool _needsWarmup = true;
        private Action _onFinished;

        private void BuildStyle()
        {
            _style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 32,
                fontStyle = FontStyle.Bold,
                wordWrap = true
            };
            _style.normal.textColor = Color.white;
        }

        public void Display(string text, Action onFinished)
        {
            _message = text;
            _messageTimer = MessageDuration;
            _onFinished = onFinished;
        }

        private void Update()
        {
            if (_messageTimer > 0f)
            {
                // Time.deltaTime vaut 0 quand Time.timeScale = 0 (pause),
                // ce qui figeait le message a l'ecran indefiniment tant que
                // le menu pause restait ouvert. Time.unscaledDeltaTime,
                // lui, continue de s'ecouler independamment du timeScale :
                // le message termine donc normalement son decompte meme
                // pendant la pause, au lieu de rester bloque.
                _messageTimer -= Time.unscaledDeltaTime;
                if (_messageTimer <= 0f)
                {
                    _message = null;
                    var callback = _onFinished;
                    _onFinished = null;
                    callback?.Invoke();
                }
            }
        }

        private void OnGUI()
        {
            GUI.matrix = Matrix4x4.identity;
            GUI.color = Color.white;
            GUI.depth = -1000;

            if (_needsWarmup)
            {
                BuildStyle();

                float warmWidth = 900f;
                float warmHeight = 110f;
                Rect warmRect = new Rect((Screen.width - warmWidth) / 2f, (Screen.height - warmHeight) / 2f, warmWidth, warmHeight);

                Color prevColor = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0f);
                GUI.Label(warmRect,
                    "AaBbCcDdEeFfGgHhIiJjKkLlMmNnOoPpQqRrSsTtUuVvWwXxYyZz / - found! 0123456789",
                    _style);
                GUI.color = prevColor;

                if (Event.current != null && Event.current.type == EventType.Repaint)
                {
                    _needsWarmup = false;
                }
            }

            // N'affiche pas le message par-dessus le menu pause : meme
            // convention que l'overlay "Latest Checkpoint" de
            // CheckpointsManager.
            if (MessageHub.IsPaused) return;

            if (string.IsNullOrEmpty(_message)) return;

            float alpha = Mathf.Clamp01(_messageTimer / FadeOutDuration);

            float width = 900f;
            float height = 110f;
            Rect rect = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);

            GUI.color = new Color(0f, 0f, 0f, alpha * 0.8f);
            GUI.Label(new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height), _message, _style);

            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(rect, _message, _style);
        }
    }
}