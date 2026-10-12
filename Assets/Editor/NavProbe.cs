using System.Collections.Generic;
using System.Linq;
using LevelGen.Core;
using LevelGen.Unity;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Editor
{
    public static class NavProbe
    {
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var builder = Object.FindAnyObjectByType<LevelBuilder>();
            foreach (int seed in new[] { 4, 3, 1 })
            {
                builder.Build(seed);
                Report(builder, seed, "full");
                // strip props and re-bake
                var props = builder.transform.Find("Level/Props");
                if (props != null) Object.DestroyImmediate(props.gameObject);
                var surf = builder.transform.GetComponentInChildren<NavMeshSurface>();
                surf.RemoveData(); surf.BuildNavMesh();
                Report(builder, seed, "noProps");
            }
            EditorApplication.Exit(0);
        }

        static void Report(LevelBuilder builder, int seed, string tag)
        {
            var layout = builder.CurrentLayout;
            var broken = new List<string>();
            foreach (var c in layout.Corridors)
            {
                var a = layout.Rooms[c.RoomA]; var b = layout.Rooms[c.RoomB];
                var dirA = c.DirectionFromA; var dirB = dirA.Opposite();
                Vector3 pa = Inside(a, dirA); Vector3 pb = Inside(b, dirB);
                string st = PathStatus(pa, pb);
                if (st != "PathComplete")
                    broken.Add($"c{c.Id} {a.Shape}{a.Id}->{b.Shape}{b.Id} len={c.Length:F1} props={c.Props.Count} {st}");
            }
            foreach (var r in layout.Rooms)
            {
                var mouths = new List<Vector3>();
                foreach (var cid in r.CorridorIds)
                {
                    var c = layout.Corridors[cid];
                    var dir = c.RoomA == r.Id ? c.DirectionFromA : c.DirectionFromA.Opposite();
                    mouths.Add(Inside(r, dir));
                }
                mouths.Add(LevelBuilder.ToWorld(r.Center));
                for (int i = 0; i < mouths.Count; i++)
                    for (int j = i + 1; j < mouths.Count; j++)
                    {
                        string st = PathStatus(mouths[i], mouths[j]);
                        if (st != "PathComplete")
                            broken.Add($"ROOM {r.Id} {r.Shape} {r.Type} {r.SizeX:F0}x{r.SizeZ:F0} props={r.Props.Count} pts {i}-{j} (last=centre) {st}");
                    }
            }
            UnityEngine.Debug.Log($"[NavProbe] seed={seed} {tag} brokenCorridors={broken.Count}/{layout.Corridors.Count}\n" + string.Join("\n", broken));
        }

        // point 1.5 m inside the room from the mouth
        static Vector3 Inside(Room r, Cardinal dir)
        {
            var bp = r.BoundaryPoint(dir);
            var toCentre = r.Center - bp; double len = toCentre.Length;
            var p = bp + toCentre * (1.5 / len);
            return LevelBuilder.ToWorld(p);
        }

        static string PathStatus(Vector3 a, Vector3 b)
        {
            if (!NavMesh.SamplePosition(a, out var ha, 1f, NavMesh.AllAreas)) return "A offmesh";
            if (!NavMesh.SamplePosition(b, out var hb, 1f, NavMesh.AllAreas)) return "B offmesh";
            var path = new NavMeshPath();
            NavMesh.CalculatePath(ha.position, hb.position, NavMesh.AllAreas, path);
            return path.status.ToString();
        }
    }
}
