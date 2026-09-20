using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Unity.Templates.IndustryFundamentals;
using Random = UnityEngine.Random;

namespace Unity.Templates.IndustryFundamentals
{
    public class RandomAgentMovement : MonoBehaviour
    {
        public Transform[] agentNavTargets;

        public float minWaitTime = 1f;
        public float maxWaitTime = 3f;

        private NavMeshAgent[] _navMeshAgent;
        private readonly Dictionary<NavMeshAgent, Queue<int>> _agentRecentTargets = new();
        private readonly Dictionary<NavMeshAgent, float> _agentMaxSpeeds = new();
        private readonly Dictionary<NavMeshAgent, Coroutine> _movementCoroutines = new();

        private bool _isAgent0Running;
        private Coroutine _animateColorRoutine;

        private void OnEnable()
        {
            RobotManager.Instance.RobotListChanged += SetNavMeshAgentList;
        }

        private void SetNavMeshAgentList(List<Robot> robots)
        {
            _navMeshAgent = new NavMeshAgent[robots.Count];
            for (int i = 0; i < robots.Count; i++)
                _navMeshAgent[i] = robots[i].NavMeshAgent;
        }

        private void Start()
        {
            for (int i = 0; i < _navMeshAgent.Length; i++)
            {
                NavMeshAgent agent = _navMeshAgent[i];

                _agentMaxSpeeds[agent] = agent.speed;
                _agentRecentTargets[agent] = new Queue<int>();
                AssignRandomTarget(agent);
            }
        }

        private void Update()
        {
            for (int i = 0; i < _navMeshAgent.Length; i++)
            {
                NavMeshAgent agent = _navMeshAgent[i];

                if (agent.pathPending) continue;

                if (agent.enabled && agent.isOnNavMesh)
                {
                    float remainingDistance = agent.remainingDistance;

                    if (remainingDistance <= agent.stoppingDistance &&
                        (!agent.hasPath || agent.velocity.sqrMagnitude == 0f))
                        if (!_movementCoroutines.ContainsKey(agent) || _movementCoroutines[agent] == null)
                            _movementCoroutines[agent] = StartCoroutine(WaitAndAssignTarget(agent, i));
                }
            }
        }

        private IEnumerator WaitAndAssignTarget(NavMeshAgent agent, int agentIndex)
        {
            float waitTime = Random.Range(minWaitTime, maxWaitTime);
            yield return new WaitForSeconds(waitTime);
            AssignRandomTarget(agent);
            _movementCoroutines[agent] = null;
        }

        private void AssignRandomTarget(NavMeshAgent agent)
        {
            int newIndex = Random.Range(0, agentNavTargets.Length);
            Vector3 targetPos = agentNavTargets[newIndex].transform.position;

            agent.SetDestination(targetPos);
        }

        private void OnDisable()
        {
            RobotManager.Instance.RobotListChanged -= SetNavMeshAgentList;
        }
    }
}