using System.Linq;
using System.Text;
using LevelGen.Core;
using LevelGen.Unity;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Editor
{
    public static class NavProbe2
    {
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var builder = Object.FindAnyObjectByType<LevelBuilder>();
            builder.Build(4);
            var layout = builder.CurrentLayout;
            var room = layout.Rooms[1];
            var sb = new StringBuilder();
            sb.AppendLine($"[NavProbe2] room1 {room.Shape} {room.Type} centre={room.Center} size={room.SizeX:F1}x{room.SizeZ:F1} mouths=" +
                string.Join(",", room.CorridorIds.Select(cid => { var c = layout.Corridors[cid]; return (c.RoomA == room.Id ? c.DirectionFromA : c.DirectionFromA.Opposite()).ToString(); })));
            foreach (var p in room.Props)
            {
                p.WorldHalfExtents(out var hx, out var hz);
                sb.AppendLine($"  CORE {p.Type} pos={p.Position} rot={p.RotationDeg} aabb=[{p.Position.X - hx:F2},{p.Position.Z - hz:F2}]..[{p.Position.X + hx:F2},{p.Position.Z + hz:F2}] h={p.Height:F2}");
            }
            float minX = (float)(room.Center.X - room.HalfX) - 1, maxX = (float)(room.Center.X + room.HalfX) + 1;
            float minZ = (float)(room.Center.Z - room.HalfZ) - 1, maxZ = (float)(room.Center.Z + room.HalfZ) + 1;
            foreach (var col in builder.GetComponentsInChildren<Collider>())
            {
                var b = col.bounds;
                if (b.center.x < minX || b.center.x > maxX || b.center.z < minZ || b.center.z > maxZ) continue;
                if (col is MeshCollider) continue; // floors/walls
                var mod = col.GetComponentInParent<NavMeshModifier>();
                sb.AppendLine($"  UNITY {col.gameObject.name} ({col.GetType().Name}) bounds=[{b.min.x:F2},{b.min.y:F2},{b.min.z:F2}]..[{b.max.x:F2},{b.max.y:F2},{b.max.z:F2}] modifier={(mod != null ? mod.area.ToString() : "none")}");
            }
            UnityEngine.Debug.Log(sb.ToString());
            EditorApplication.Exit(0);
        }
    }
}
