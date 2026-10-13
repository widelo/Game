// The seam between "an enemy body in the level" (EnemyAgent, EnemySpawner) and "how it decides what to do"
// (a brain). The demo ships StalkerBrain; the real AI replaces it by implementing this interface.
using System;
using LevelGen.Core;
using LevelGen.Unity;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Enemy
{
    /// <summary>
    /// An enemy's decision-maker. The host (<see cref="EnemyAgent"/>) calls <see cref="Initialise"/> once when
    /// the enemy spawns and again after every catch (so a brain must fully reset itself there), then
    /// <see cref="Tick"/> every frame it is not on its post-catch cooldown.
    /// </summary>
    public interface IEnemyBrain
    {
        void Initialise(EnemyContext ctx);
        void Tick(float dt);
    }

    /// <summary>Everything a brain may touch. Built by the spawner; valid until the next level rebuild.</summary>
    public sealed class EnemyContext
    {
        /// <summary>The generated level: Rooms, Corridors, Neighbours(id), BfsDistances(id), SpawnRoom, KeyRooms.</summary>
        public LevelLayout Layout;
        /// <summary>World helpers: RoomWorldCenter(id), PlayerSpawnPoint, RoomObjects, LevelRoot.</summary>
        public LevelBuilder Builder;
        /// <summary>Player root (carries CharacterController + FirstPersonController).</summary>
        public Transform Player;
        /// <summary>The enemy's agent, already warped onto the baked NavMesh.</summary>
        public NavMeshAgent Agent;
        /// <summary>The enemy root transform.</summary>
        public Transform Self;
        /// <summary>
        /// Report to the host. "caught" = the player was caught (host teleports player, resets enemy).
        /// "state:&lt;Name&gt;" = the brain changed state (host shows Name on the HUD). Anything else is forwarded
        /// to EnemyAgent.OnEnemyEvent listeners (audio, UI) untouched.
        /// </summary>
        public Action<string> OnEvent;
    }
}
