using System;
using UnityEngine;

// ---------------------------------------------------------------------------------------------------------------
// Tuning revision 2 (2026-11-12, playtest: "clips through walls", "pathfinding broken", "fails to kill").
// Player reference speeds: walk 5.0 m/s, sprint 8.0 m/s, health 100.
//
//   Setting                                   before   after   why
//   NavigationSettings.bodyRadius             0.45     0.6     collision capsule matches the (narrowed) model
//   StalkerSettings.chaseSpeed                6.3      7.4     closes on a walker, nearly holds a sprinter
//   StalkerSettings.chaseDuration             12       25      chase cap ("max chase duration")
//   StalkerSettings.disengagementCooldown     6        4       shorter lull after a disengage
//   StalkerSettings.attackRange               1.65     2.4     windup starts from further out
//   StalkerSettings.attackWindup              0.9      0.35    windup is a short lunge, not a long tell
//   StalkerSettings.attackCooldown            2.5      1.2
//   StalkerSettings.attackDamage              25       50      two hits kill (100 health)
//   StalkerSettings.attackLungeSpeed          (new)    9       boss dashes at the player during the windup
//   StalkerSettings.attackReleaseReach        (new)    0.6     strike connects within attackRange + this
//   StalkerSettings.pointBlankRange           (new)    1.8     inside this, strike ignores FOV/eye-line
//   AStarDirector.modelHeight                 2.8      2.5     head clears 3.0 m ceilings while breathing
//   AStarDirector.modelWidthScale             (new)    0.8     visual X/Z squeeze toward the capsule
//   AStarDirector.useNavMesh                  (new)    true    NavMeshAgent motor when a NavMesh is baked
//
// Scenes saved before revision 2 serialize the old numbers; AStarDirector.MigrateTuning() moves any field that
// still holds its old default to the new one, once (tuningRevision), and leaves deliberate edits alone.
// ---------------------------------------------------------------------------------------------------------------

namespace CS462.StalkerAI
{
    [Serializable]
    public sealed class NavigationSettings
    {
        [Min(0.2f)] public float cellSize = 0.75f;
        [Min(0.1f)] public float bodyRadius = 0.6f;
        [Min(0.5f)] public float bodyHeight = 2.6f;
        [Min(0.01f)] public float wallMargin = 0.06f;
        [Tooltip("The inspected LevelLayout is flat, with floors at world Y=0.")]
        public float floorY = 0f;
        [Range(0.01f, 0.15f)] public float floorTolerance = 0.025f;
        [Min(0.01f)] public float groundClearance = 0.05f;
        public LayerMask worldMask = ~0;
        [Min(100)] public int buildCellsPerFrame = 400;
        [Min(1000)] public int maxGridCells = 80000;
        [Min(1)] public int searchExpansionsPerFrame = 64;
        [Min(0.1f)] public float repathInterval = 0.4f;
        [Min(0.1f)] public float endpointSnapDistance = 1.5f;
        [Tooltip("Reject newly obstructed edges during search. Opened passages need a grid rebuild.")]
        public bool checkLiveObstacles = true;
        public bool drawGrid;
        public bool drawBlockedNodes;
        [Min(1)] public int maxGizmoNodes = 6000;

        public NavigationSettings Snapshot() => (NavigationSettings)MemberwiseClone();

        public void Validate()
        {
            cellSize = Mathf.Max(0.2f, cellSize); bodyRadius = Mathf.Max(0.1f, bodyRadius);
            bodyHeight = Mathf.Max(2f * bodyRadius + 0.1f, bodyHeight);
            wallMargin = Mathf.Max(0.01f, wallMargin);
            // Floor and capsule lift must agree; this avoids treating slightly raised floor surfaces as walls.
            floorTolerance = Mathf.Clamp(floorTolerance, 0.01f, 0.15f);
            groundClearance = Mathf.Max(floorTolerance + 0.015f, groundClearance);
            bodyHeight = Mathf.Max(bodyHeight, 2f * (bodyRadius + wallMargin) + groundClearance);
            buildCellsPerFrame = Mathf.Max(100, buildCellsPerFrame);
            maxGridCells = Mathf.Max(1000, maxGridCells);
            searchExpansionsPerFrame = Mathf.Max(1, searchExpansionsPerFrame);
            repathInterval = Mathf.Max(0.1f, repathInterval);
            endpointSnapDistance = Mathf.Max(cellSize, endpointSnapDistance);
            maxGizmoNodes = Mathf.Max(1, maxGizmoNodes);
        }
    }

    [Serializable]
    public sealed class StalkerSettings
    {
        [Header("Movement (player walks at 5 and sprints at 8 m/s)")]
        [Min(0.1f)] public float patrolSpeed = 2.0f;
        [Min(0.1f)] public float investigateSpeed = 3.0f;
        [Min(0.1f)] public float stalkSpeed = 2.6f;
        [Min(0.1f)] public float chaseSpeed = 7.4f;
        [Min(0.1f)] public float retreatSpeed = 3.8f;
        [Min(0.1f)] public float acceleration = 10f;
        [Min(1f)] public float turnSpeed = 200f;
        public float gravity = -22f;

        [Header("Sight and hearing")]
        [Min(0.1f)] public float sightDistance = 19f;
        [Range(1f, 360f)] public float fieldOfView = 110f;
        [Min(0.1f)] public float eyeHeight = 2.25f;
        [Min(0.05f)] public float senseInterval = 0.15f;
        [Min(0f)] public float detectionDelay = 0.6f;
        [Min(0.1f)] public float memoryDuration = 8f;
        [Min(0.1f)] public float hearingDistance = 18f;
        [Range(0f, 1f)] public float occludedHearingMultiplier = 0.45f;
        [Min(0f)] public float hearingUncertainty = 1.2f;
        [Min(0.05f)] public float hearingCooldown = 0.5f;

        [Header("Patrol and investigation")]
        public Vector2 patrolPause = new Vector2(1f, 3f);
        [Min(1f)] public float patrolLegTimeout = 30f;
        [Min(0.1f)] public float patrolDecisionInterval = 4f;
        [Range(0f, 1f)] public float pauseWhileWalkingChance = 0.2f;
        [Min(0.1f)] public float investigationDuration = 7f;
        [Min(0.1f)] public float searchAtPointDuration = 3f;
        [Min(0.1f)] public float searchRadius = 3f;

        [Header("Stalking")]
        [Min(1f)] public float preferredStalkDistance = 8f;
        [Min(0.1f)] public float stalkDistanceTolerance = 1.5f;
        public Vector2 stalkBeforeChase = new Vector2(8f, 15f);
        public Vector2 watchDuration = new Vector2(1.2f, 3f);
        [Range(0f, 1f)] public float watchChance = 0.35f;
        [Min(0.2f)] public float repositionInterval = 3f;
        [Range(4, 32)] public int positionCandidates = 12;
        [Min(0f)] public float concealmentPreference = 2.5f;

        [Header("Chase and escape")]
        [Min(0.1f)] public float closeAggroDistance = 3.2f;
        [Min(0.1f)] public float chaseDuration = 25f;
        [Min(0.1f)] public float loseSightDelay = 2.2f;
        [Min(0.1f)] public float chaseCooldown = 10f;
        [Min(0.1f)] public float disengagementCooldown = 4f;
        [Min(1f)] public float retreatDistance = 10f;

        [Header("Attack: windup, hit check, recovery")]
        [Min(0.1f)] public float attackRange = 2.4f;
        [Min(0.1f)] public float attackWindup = 0.35f;
        [Min(0.1f)] public float attackCooldown = 1.2f;
        [Min(0.1f)] public float attackRecovery = 0.75f;
        [Min(0.1f)] public float attackDamage = 50f;
        [Tooltip("Speed of the dash toward the player during the windup. 0 = stand still while winding up.")]
        [Min(0f)] public float attackLungeSpeed = 9f;
        [Tooltip("Extra reach when the strike resolves: it connects within attackRange + this.")]
        [Min(0f)] public float attackReleaseReach = 0.6f;
        [Tooltip("Within this flat distance the strike lands without FOV/eye-line (a wall between still blocks it).")]
        [Min(0f)] public float pointBlankRange = 1.8f;
        [Range(5f, 180f)] public float attackHalfAngle = 45f;
        [Min(0.1f)] public float attackVerticalTolerance = 1.25f;

        [Header("Development")]
        public bool logStateChanges = true;
        public bool drawGizmos = true;

        public void Validate()
        {
            patrolSpeed = Mathf.Max(0.1f, patrolSpeed); investigateSpeed = Mathf.Max(0.1f, investigateSpeed);
            stalkSpeed = Mathf.Max(0.1f, stalkSpeed); chaseSpeed = Mathf.Max(0.1f, chaseSpeed);
            retreatSpeed = Mathf.Max(0.1f, retreatSpeed); acceleration = Mathf.Max(0.1f, acceleration);
            turnSpeed = Mathf.Max(1f, turnSpeed); gravity = Mathf.Min(-0.1f, gravity);
            sightDistance = Mathf.Max(0.1f, sightDistance); fieldOfView = Mathf.Clamp(fieldOfView, 1f, 360f);
            eyeHeight = Mathf.Max(0.1f, eyeHeight); senseInterval = Mathf.Max(0.05f, senseInterval);
            detectionDelay = Mathf.Max(0f, detectionDelay); memoryDuration = Mathf.Max(0.1f, memoryDuration);
            hearingDistance = Mathf.Max(0.1f, hearingDistance); hearingCooldown = Mathf.Max(0.05f, hearingCooldown);
            occludedHearingMultiplier = Mathf.Clamp01(occludedHearingMultiplier);
            hearingUncertainty = Mathf.Max(0f, hearingUncertainty);
            patrolPause = Ordered(patrolPause); stalkBeforeChase = Ordered(stalkBeforeChase);
            watchDuration = Ordered(watchDuration);
            patrolLegTimeout = Mathf.Max(1f, patrolLegTimeout); patrolDecisionInterval = Mathf.Max(0.1f, patrolDecisionInterval);
            investigationDuration = Mathf.Max(0.1f, investigationDuration);
            searchAtPointDuration = Mathf.Max(0.1f, searchAtPointDuration); searchRadius = Mathf.Max(0.1f, searchRadius);
            preferredStalkDistance = Mathf.Max(1f, preferredStalkDistance);
            stalkDistanceTolerance = Mathf.Max(0.1f, stalkDistanceTolerance);
            repositionInterval = Mathf.Max(0.2f, repositionInterval); positionCandidates = Mathf.Clamp(positionCandidates, 4, 32);
            closeAggroDistance = Mathf.Max(0.1f, closeAggroDistance); chaseDuration = Mathf.Max(0.1f, chaseDuration);
            loseSightDelay = Mathf.Max(0.1f, loseSightDelay); chaseCooldown = Mathf.Max(0.1f, chaseCooldown);
            disengagementCooldown = Mathf.Max(0.1f, disengagementCooldown); retreatDistance = Mathf.Max(1f, retreatDistance);
            attackRange = Mathf.Max(0.1f, attackRange); attackWindup = Mathf.Max(0.1f, attackWindup);
            attackCooldown = Mathf.Max(0.1f, attackCooldown); attackRecovery = Mathf.Max(0.1f, attackRecovery);
            attackDamage = Mathf.Max(0.1f, attackDamage); attackHalfAngle = Mathf.Clamp(attackHalfAngle, 5f, 180f);
            attackVerticalTolerance = Mathf.Max(0.1f, attackVerticalTolerance);
            attackLungeSpeed = Mathf.Max(0f, attackLungeSpeed); attackReleaseReach = Mathf.Max(0f, attackReleaseReach);
            pointBlankRange = Mathf.Max(0f, pointBlankRange);
        }

        /// <summary>Revision 1 -> 2: replace values that still equal the old defaults; keep deliberate edits.</summary>
        public void MigrateFromRevision1()
        {
            static float Swap(float value, float old, float next) => Mathf.Approximately(value, old) ? next : value;
            chaseSpeed = Swap(chaseSpeed, 6.3f, 7.4f);
            chaseDuration = Swap(chaseDuration, 12f, 25f);
            disengagementCooldown = Swap(disengagementCooldown, 6f, 4f);
            attackRange = Swap(attackRange, 1.65f, 2.4f);
            attackWindup = Swap(attackWindup, 0.9f, 0.35f);
            attackCooldown = Swap(attackCooldown, 2.5f, 1.2f);
            attackDamage = Swap(attackDamage, 25f, 50f);
        }

        static Vector2 Ordered(Vector2 value) => new Vector2(Mathf.Max(0.1f, value.x), Mathf.Max(0.1f, Mathf.Max(value.x, value.y)));
    }
}
