using UnityEngine;
using UnityEngine.AI;

public class RandomPatrol : MonoBehaviour
{
    public Transform[] points;
    public float randomRadius = 1.0f;
    public bool deactivateAfterOneCircuit;

    [Header("Walking Speed")]
    public float minSpeed = 0.8f;
    public float maxSpeed = 1.5f;

    private int currentPoint = 0;

    private NavMeshAgent agent;
    private Animator animator;

    void Start()
    {
        agent = GetComponent<NavMeshAgent>();
        animator = GetComponent<Animator>();

        if (points == null || points.Length == 0)
        {
            enabled = false;
            return;
        }
        SetNextDestination();
    }

    void Update()
    {
        animator.SetFloat("Speed", agent.velocity.magnitude);

        if (!agent.pathPending &&
            agent.remainingDistance <= agent.stoppingDistance)
        {
            currentPoint++;

            if (currentPoint >= points.Length)
            {
                if (deactivateAfterOneCircuit)
                {
                    gameObject.SetActive(false);
                    return;
                }
                currentPoint = 0;
            }

            SetNextDestination();
        }
    }

    void SetNextDestination()
    {
        agent.speed = Random.Range(minSpeed, maxSpeed);

        Vector3 randomOffset =
            Random.insideUnitSphere * randomRadius;

        randomOffset.y = 0;

        Vector3 target =
            points[currentPoint].position + randomOffset;

        NavMeshHit hit;

        if (NavMesh.SamplePosition(
            target,
            out hit,
            randomRadius,
            NavMesh.AllAreas))
        {
            agent.SetDestination(hit.position);
        }
        else
        {
            agent.SetDestination(
                points[currentPoint].position
            );
        }
    }
}
