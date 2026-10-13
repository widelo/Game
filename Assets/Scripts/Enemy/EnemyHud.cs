// Tiny top-right OnGUI readout for the first enemy: state, distance, catches, and a CAUGHT flash.
using UnityEngine;

namespace Game.Enemy
{
    public sealed class EnemyHud : MonoBehaviour
    {
        [Tooltip("Found automatically if empty.")]
        public EnemySpawner spawner;
        public float flashSeconds = 1.5f;

        GUIStyle _flash;

        void OnGUI()
        {
            if (spawner == null) spawner = FindAnyObjectByType<EnemySpawner>();
            if (spawner == null || spawner.Enemies.Count == 0) return;
            EnemyAgent e = spawner.Enemies[0];
            if (e == null) return;

            float dist = spawner.player != null ? Vector3.Distance(e.transform.position, spawner.player.position) : -1f;
            var box = new Rect(Screen.width - 228f, 8f, 220f, 66f);
            GUI.Box(box, GUIContent.none);
            GUI.Label(new Rect(box.x + 8f, box.y + 4f, box.width - 16f, box.height - 8f),
                      $"Enemy: {e.StateName}\nDistance: {dist:F1} m\nCaught: {e.CaughtCount}");

            if (Time.time - e.LastCaughtTime < flashSeconds)
            {
                if (_flash == null)
                    _flash = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 72, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = Color.red },
                    };
                GUI.Label(new Rect(0f, 0f, Screen.width, Screen.height), "CAUGHT", _flash);
            }
        }
    }
}
