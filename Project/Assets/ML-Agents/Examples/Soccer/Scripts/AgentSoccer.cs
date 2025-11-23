using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;

public enum Team
{
    Blue = 0,
    Purple = 1
}

public class AgentSoccer : Agent
{
    // Note that that the detectable tags are different for the blue and purple teams. The order is
    // * ball
    // * own goal
    // * opposing goal
    // * wall
    // * own teammate
    // * opposing player

    public enum Position
    {
        Striker,
        Goalie,
        Generic
    }

    [HideInInspector]
    public Team team;
    float m_KickPower;
    // The coefficient for the reward for colliding with a ball. Set using curriculum.
    float m_BallTouch;
    public Position position;

    const float k_Power = 2000f;
    float m_Existential;
    float m_LateralSpeed;
    float m_ForwardSpeed;


    [HideInInspector]
    public Rigidbody agentRb;
    SoccerSettings m_SoccerSettings;
    BehaviorParameters m_BehaviorParameters;
    public Vector3 initialPos;
    public float rotSign;

    EnvironmentParameters m_ResetParams;

    // For defensive reward tracking.
    private GameObject m_Ball;
    // Note: ownGoal mean's the team's own goal and not the "own goal" scoring terminology.
    private Transform m_OwnGoal;
    private Transform m_OpposingGoal;
    private static AgentSoccer s_LastBallPossessor;
    private float m_DefensiveHalfBoundary;
    private const float k_OpponentMarkingDistance = 3f;
    private SoccerEnvController m_EnvController;

    public override void Initialize()
    {
        m_EnvController = GetComponentInParent<SoccerEnvController>();
        if (m_EnvController != null)
        {
            m_Existential = 1f / m_EnvController.MaxEnvironmentSteps;
            m_Ball = m_EnvController.ball;
        }
        else
        {
            m_Existential = 1f / MaxStep;
        }

        m_BehaviorParameters = gameObject.GetComponent<BehaviorParameters>();
        if (m_BehaviorParameters.TeamId == (int)Team.Blue)
        {
            team = Team.Blue;
            initialPos = new Vector3(transform.position.x - 5f, .5f, transform.position.z);
            rotSign = 1f;

            // Find goals based on tag
            m_OwnGoal = GameObject.FindGameObjectWithTag("blueGoal")?.transform;
            m_OpposingGoal = GameObject.FindGameObjectWithTag("purpleGoal")?.transform;
            // Defensive half is closer to own goal
            m_DefensiveHalfBoundary = 0f;
        }
        else
        {
            team = Team.Purple;
            initialPos = new Vector3(transform.position.x + 5f, .5f, transform.position.z);
            rotSign = -1f;

            // Check blue comments for same snippet.
            m_OwnGoal = GameObject.FindGameObjectWithTag("purpleGoal")?.transform;
            m_OpposingGoal = GameObject.FindGameObjectWithTag("blueGoal")?.transform;
            m_DefensiveHalfBoundary = 0f;
        }
        if (position == Position.Goalie)
        {
            m_LateralSpeed = 1.0f;
            m_ForwardSpeed = 1.0f;
        }
        else if (position == Position.Striker)
        {
            m_LateralSpeed = 0.3f;
            m_ForwardSpeed = 1.3f;
        }
        else
        {
            m_LateralSpeed = 0.3f;
            m_ForwardSpeed = 1.0f;
        }
        m_SoccerSettings = FindFirstObjectByType<SoccerSettings>();
        agentRb = GetComponent<Rigidbody>();
        agentRb.maxAngularVelocity = 500;

        m_ResetParams = Academy.Instance.EnvironmentParameters;
    }

    public void MoveAgent(ActionSegment<int> act)
    {
        var dirToGo = Vector3.zero;
        var rotateDir = Vector3.zero;

        m_KickPower = 0f;

        var forwardAxis = act[0];
        var rightAxis = act[1];
        var rotateAxis = act[2];

        switch (forwardAxis)
        {
            case 1:
                dirToGo = transform.forward * m_ForwardSpeed;
                m_KickPower = 1f;
                break;
            case 2:
                dirToGo = transform.forward * -m_ForwardSpeed;
                break;
        }

        switch (rightAxis)
        {
            case 1:
                dirToGo = transform.right * m_LateralSpeed;
                break;
            case 2:
                dirToGo = transform.right * -m_LateralSpeed;
                break;
        }

        switch (rotateAxis)
        {
            case 1:
                rotateDir = transform.up * -1f;
                break;
            case 2:
                rotateDir = transform.up * 1f;
                break;
        }

        transform.Rotate(rotateDir, Time.deltaTime * 100f);
        agentRb.AddForce(dirToGo * m_SoccerSettings.agentRunSpeed,
            ForceMode.VelocityChange);
    }

    // Reward for defensive positioning between ball and own goal.
    private float CalculateDefensivePositioningReward()
    {
        if (m_Ball == null || m_OwnGoal == null) return 0f;

        Vector3 ballPos = m_Ball.transform.position;
        Vector3 goalPos = m_OwnGoal.position;
        Vector3 agentPos = transform.position;

        Vector3 ballToGoal = goalPos - ballPos;
        Vector3 ballToAgent = agentPos - ballPos;

        // Project agent position onto ball->goal line.
        float projection = Vector3.Dot(ballToAgent, ballToGoal.normalized);
        
        float reward = 0f;

        // Agent is between ball and goal if projection is positive and less than distance to goal
        if (projection > 0 && projection < ballToGoal.magnitude)
        {
            // Check how close agent is to the ideal blocking line
            Vector3 projectedPoint = ballPos + ballToGoal.normalized * projection;
            float distanceFromLine = Vector3.Distance(agentPos, projectedPoint);

            // Reward decreases with distance from ideal line (max reward at <1 unit away)
            if (distanceFromLine < 2f)
            {
                reward += 0.01f;
            }
        }

        // Bonus for being in defending half,
        bool inDefensiveHalf = (team == Team.Blue && ballPos.x < m_DefensiveHalfBoundary) ||
                               (team == Team.Purple && ballPos.x > m_DefensiveHalfBoundary);
        
        if (inDefensiveHalf)
        {
            reward += 0.02f;
        }

        return reward;
    }

    // Adding reward for "marking" opponents.
    private float CalculateOpponentMarkingReward()
    {
        if (m_EnvController == null || m_Ball == null) return 0f;

        float reward = 0f;
        AgentSoccer closestOpponent = null;
        float closestOpponentDist = float.MaxValue;
        AgentSoccer opponentClosestToBall = null;
        float closestDistToBall = float.MaxValue;

        // Find all opponents and determine closest ones
        foreach (var playerInfo in m_EnvController.AgentsList)
        {
            AgentSoccer agent = playerInfo.Agent;
            if (agent.team != this.team)
            {
                float distToOpponent = Vector3.Distance(transform.position, agent.transform.position);
                float distToBall = Vector3.Distance(agent.transform.position, m_Ball.transform.position);

                // Track closest opponent to this agent
                if (distToOpponent < closestOpponentDist)
                {
                    closestOpponentDist = distToOpponent;
                    closestOpponent = agent;
                }

                // Track opponent closest to ball
                if (distToBall < closestDistToBall)
                {
                    closestDistToBall = distToBall;
                    opponentClosestToBall = agent;
                }

                // Reward for being within marking distance of any opponent
                if (distToOpponent < k_OpponentMarkingDistance)
                {
                    reward += 0.015f;
                }
            }
        }

        // Bonus for marking the opponent closest to ball
        if (opponentClosestToBall != null && closestOpponent == opponentClosestToBall)
        {
            reward += 0.025f;
        }

        return reward;
    }

    public override void OnActionReceived(ActionBuffers actionBuffers)

    {

        if (position == Position.Goalie)
        {
            // Existential bonus for Goalies.
            AddReward(m_Existential);
        }
        else if (position == Position.Striker)
        {
            // Existential penalty for Strikers
            AddReward(-m_Existential);
        }

        // Apply defensive rewards for all agents
        float defensivePositionReward = CalculateDefensivePositioningReward();
        if (defensivePositionReward > 0)
        {
            AddReward(defensivePositionReward);
        }

        float opponentMarkingReward = CalculateOpponentMarkingReward();
        if (opponentMarkingReward > 0)
        {
            AddReward(opponentMarkingReward);
        }

        MoveAgent(actionBuffers.DiscreteActions);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var discreteActionsOut = actionsOut.DiscreteActions;
        //forward
        if (Input.GetKey(KeyCode.W))
        {
            discreteActionsOut[0] = 1;
        }
        if (Input.GetKey(KeyCode.S))
        {
            discreteActionsOut[0] = 2;
        }
        //rotate
        if (Input.GetKey(KeyCode.A))
        {
            discreteActionsOut[2] = 1;
        }
        if (Input.GetKey(KeyCode.D))
        {
            discreteActionsOut[2] = 2;
        }
        //right
        if (Input.GetKey(KeyCode.E))
        {
            discreteActionsOut[1] = 1;
        }
        if (Input.GetKey(KeyCode.Q))
        {
            discreteActionsOut[1] = 2;
        }
    }
    /// <summary>
    /// Used to provide a "kick" to the ball.
    /// </summary>
    void OnCollisionEnter(Collision c)
    {
        var force = k_Power * m_KickPower;
        if (position == Position.Goalie)
        {
            force = k_Power;
        }
        if (c.gameObject.CompareTag("ball"))
        {
            // Reward for interception of the ball,
            if (s_LastBallPossessor != null && s_LastBallPossessor.team != this.team)
            {
                AddReward(0.5f);
            }

            bool ballInDefensiveZone = (team == Team.Blue && c.transform.position.x < m_DefensiveHalfBoundary) ||
                                       (team == Team.Purple && c.transform.position.x > m_DefensiveHalfBoundary);
            
            if (ballInDefensiveZone)
            {
                // Calculate direction of ball kick w.r.t to the goal.
                var dir = c.contacts[0].point - transform.position;
                dir = dir.normalized;
                bool clearingTowardOpponent = (team == Team.Blue && dir.x > 0) ||
                                              (team == Team.Purple && dir.x < 0);
                
                if (clearingTowardOpponent)
                {
                    AddReward(0.3f);
                }
            }

            AddReward(.2f * m_BallTouch);

            s_LastBallPossessor = this;

            var kickDir = c.contacts[0].point - transform.position;
            kickDir = kickDir.normalized;
            c.gameObject.GetComponent<Rigidbody>().AddForce(kickDir * force);
        }
    }

    public override void OnEpisodeBegin()
    {
        m_BallTouch = m_ResetParams.GetWithDefault("ball_touch", 0);
        // Reset this on new ep.
        s_LastBallPossessor = null;
    }

}
