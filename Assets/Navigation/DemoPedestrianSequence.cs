using ShipRobot.ObstacleAvoidance;
using UnityEngine;

namespace ShipRobot.Navigation
{
    /// <summary>Reuses one pedestrian for several encounters on the equipment route.</summary>
    [DisallowMultipleComponent]
    public sealed class DemoPedestrianSequence : MonoBehaviour
    {
        private static readonly PlantNodeId[] LegStarts =
        {
            PlantNodeId.UnderMid, PlantNodeId.UpperLeft,
            PlantNodeId.UnderRight, PlantNodeId.UpperMid
        };
        private static readonly PlantNodeId[] LegEnds =
        {
            PlantNodeId.UpperMid, PlantNodeId.UnderLeft,
            PlantNodeId.UpperRight, PlantNodeId.UnderMid
        };

        [SerializeField] private NavigationCoordinator mission;
        [SerializeField] private PlantRouteGraph routeGraph;
        [SerializeField] private HumanAvoidanceAgent avoidanceAgent;
        [SerializeField] private Transform robot;
        [SerializeField] private TrainingPedestrianMover person;
        [SerializeField] private GameObject extraScenePerson;

        [SerializeField] private Vector2 firstStartFraction = new Vector2(0.55f, 0.68f);
        [SerializeField] private Vector2 laterStartFraction = new Vector2(0.55f, 0.72f);
        [SerializeField, Min(0f)] private float appearanceDelaySeconds = 1.5f;
        [SerializeField, Min(0f)] private float minimumLegProgressBeforeAppearance = 0.8f;
        [SerializeField, Range(0f, 1f)] private float laterEncounterChance = 0.70f;
        [SerializeField] private float lateralVariation = 0.30f;
        [SerializeField] private Vector2 walkingSpeed = new Vector2(0.60f, 0.85f);
        [SerializeField] private float passBeyondLegStart = 1.5f;
        [SerializeField] private float hideAfterPassingDistance = 2.5f;
        [SerializeField] private Vector3 offstageOffset = new Vector3(100f, 0f, 100f);

        private readonly bool[] selectedLegs = new bool[4];
        private readonly bool[] spawnedLegs = new bool[4];
        private readonly float[] legEnteredAt = new float[4];
        private bool personWalking;
        private Vector3 activeLegDirection;
        private Vector3 parkingPosition;
        private int laterEncountersStarted;

        private void Awake()
        {
            for (int i = 0; i < legEnteredAt.Length; i++)
                legEnteredAt[i] = -1f;

            // Keep the scripted demo out of ML-Agents training episodes.
            if (avoidanceAgent != null && avoidanceAgent.IsTraining)
            {
                enabled = false;
                return;
            }

            if (person != null)
            {
                Vector3 reference = robot != null ? robot.position : person.transform.position;
                parkingPosition = reference + offstageOffset;
                person.enabled = true;
                person.ParkOffCamera(parkingPosition);
            }
            if (extraScenePerson != null)
                extraScenePerson.SetActive(false);

            // The first straight always has a pedestrian. Later straights vary
            // per Play run, but at least one later encounter is guaranteed.
            selectedLegs[0] = true;
            bool laterSelected = false;
            for (int i = 1; i < selectedLegs.Length; i++)
            {
                selectedLegs[i] = Random.value < laterEncounterChance;
                laterSelected |= selectedLegs[i];
            }
            if (!laterSelected)
                selectedLegs[Random.Range(1, selectedLegs.Length)] = true;
            Debug.Log($"Demo pedestrian legs: initial=on, A={selectedLegs[1]}, " +
                      $"B={selectedLegs[2]}, return={selectedLegs[3]}", this);
        }

        private void Update()
        {
            if (mission == null || routeGraph == null || robot == null || person == null)
                return;

            if (mission.State == NavigationCoordinator.MissionState.Fault ||
                mission.State == NavigationCoordinator.MissionState.Completed)
            {
                HidePedestrian();
                return;
            }

            if (personWalking && (person.HasCompletedRoute ||
                Vector3.Dot(person.transform.position - robot.position, activeLegDirection) <
                -hideAfterPassingDistance))
                HidePedestrian();

            if (personWalking)
                return;

            for (int i = 0; i < selectedLegs.Length; i++)
            {
                bool forceFinalEncounter = i == selectedLegs.Length - 1 && laterEncountersStarted == 0;
                bool readyOnLeg = mission.IsAlignedOnEquipmentLeg(LegStarts[i], LegEnds[i]) &&
                                  HasReachedAppearancePoint(i);
                if (!readyOnLeg)
                {
                    legEnteredAt[i] = -1f;
                    continue;
                }
                if ((!selectedLegs[i] && !forceFinalEncounter) || spawnedLegs[i])
                    continue;

                if (legEnteredAt[i] < 0f)
                    legEnteredAt[i] = Time.time;
                if (Time.time - legEnteredAt[i] < appearanceDelaySeconds)
                    return;

                if (StartEncounter(i))
                {
                    spawnedLegs[i] = true;
                    personWalking = true;
                    if (i > 0) laterEncountersStarted++;
                }
                return;
            }
        }

        private bool HasReachedAppearancePoint(int legIndex)
        {
            if (!routeGraph.TryGetMarker(LegStarts[legIndex], out NavigationMarker startMarker) ||
                !routeGraph.TryGetMarker(LegEnds[legIndex], out NavigationMarker endMarker))
                return false;

            Vector3 direction = Vector3.ProjectOnPlane(
                endMarker.transform.position - startMarker.transform.position, Vector3.up).normalized;
            Vector3 fromStart = Vector3.ProjectOnPlane(
                robot.position - startMarker.transform.position, Vector3.up);
            return Vector3.Dot(fromStart, direction) >= minimumLegProgressBeforeAppearance;
        }

        private bool StartEncounter(int legIndex)
        {
            if (!routeGraph.TryGetMarker(LegStarts[legIndex], out NavigationMarker startMarker) ||
                !routeGraph.TryGetMarker(LegEnds[legIndex], out NavigationMarker endMarker))
                return false;

            Vector3 direction = Vector3.ProjectOnPlane(
                endMarker.transform.position - startMarker.transform.position, Vector3.up).normalized;
            if (direction.sqrMagnitude < 0.5f)
                return false;

            Vector2 fractionRange = legIndex == 0 ? firstStartFraction : laterStartFraction;
            float fraction = Random.Range(fractionRange.x, fractionRange.y);
            Vector3 side = Vector3.Cross(Vector3.up, direction);
            float lateral = Random.Range(-lateralVariation, lateralVariation);
            Vector3 start = Vector3.Lerp(startMarker.transform.position,
                endMarker.transform.position, fraction) + side * lateral;
            Vector3 end = startMarker.transform.position -
                direction * passBeyondLegStart + side * lateral;
            float speed = Random.Range(walkingSpeed.x, walkingSpeed.y);

            // The person is parked far outside every camera view between encounters.
            activeLegDirection = direction;
            person.ConfigureApproachScenario(robot, start, end, 0f, speed, 0f);
            Debug.Log($"Demo pedestrian encounter: {LegStarts[legIndex]} -> {LegEnds[legIndex]}, " +
                      $"start={start}, speed={speed:F2} m/s", this);
            return true;
        }

        private void HidePedestrian()
        {
            if (!personWalking)
                return;
            person.ParkOffCamera(parkingPosition);
            personWalking = false;
            Debug.Log("Demo pedestrian parked off camera until the next selected straight leg.", this);
        }
    }
}
