// Small top-centre OnGUI readout for the core loop: "KEYS 1/3", the current goal, and a big
// "ESCAPED - FLOOR n" flash after walking out. Legacy OnGUI to match the other debug HUDs (no Canvas).
using UnityEngine;

namespace Game.Objectives
{
    [AddComponentMenu("Game/Objective HUD")]
    public sealed class ObjectiveHud : MonoBehaviour
    {
        [Tooltip("Found automatically if empty.")]
        public ObjectiveTracker tracker;
        public float escapeFlashSeconds = 2.5f;

        GUIStyle _line, _flash;

        void OnGUI()
        {
            if (tracker == null) tracker = FindAnyObjectByType<ObjectiveTracker>();
            if (tracker == null) return;

            if (_line == null)
            {
                _line = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 14 };
                _flash = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 56, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = new Color(0.3f, 1f, 0.4f) },
                };
            }

            string goal = tracker.Escaped ? "ESCAPED"
                        : tracker.AllKeysCollected ? (tracker.ExitIsDedicatedRoom ? "EXIT UNLOCKED - GO" : "RETURN TO SPAWN - GO")
                        : "FIND THE EXIT";
            string text = $"FLOOR {tracker.Floor}   KEYS {tracker.KeysCollected}/{tracker.KeysRequired}\n{goal}";

            const float w = 300f, h = 46f;
            var box = new Rect((Screen.width - w) * 0.5f, 8f, w, h);
            GUI.Box(box, GUIContent.none);
            _line.normal.textColor = tracker.AllKeysCollected ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.85f, 0.3f);
            GUI.Label(box, text, _line);

            if (Time.time - tracker.LastEscapeTime < escapeFlashSeconds)
                GUI.Label(new Rect(0f, 0f, Screen.width, Screen.height), $"ESCAPED - FLOOR {tracker.Floor}", _flash);
        }
    }
}
