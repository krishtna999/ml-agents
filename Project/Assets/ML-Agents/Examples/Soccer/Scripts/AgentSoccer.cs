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
    
    // Vars for defensive
    bool m_IsDefensiveTeam;
    GameObject m_Ball;
    GameObject m_OwnGoal;
    GameObject m_OpponentGoal;
    float m_LastDistanceToBall;
    float m_LastDistanceToOwnGoal;


    [HideInInspector]
    public Rigidbody agentRb;
    SoccerSettings m_SoccerSettings;
    BehaviorParameters m_BehaviorParameters;
    public Vector3 initialPos;
    public float rotSign;

    EnvironmentParameters m_ResetParams;

    public override void Initialize()
    {
        SoccerEnvController envController = GetComponentInParent<SoccerEnvController>();
        if (envController != null)
        {
            m_Existential = 1f / envController.MaxEnvironmentSteps;
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
        }
        else
        {
            team = Team.Purple;
            initialPos = new Vector3(transform.position.x + 5f, .5f, transform.position.z);
            rotSign = -1f;
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
        
        // Purple is going to be our defensive team
        m_IsDefensiveTeam = (team == Team.Purple);
        
        if (envController != null)
        {
            m_Ball = envController.ball;
            
            Transform envTransform = envController.transform;
            foreach (Transform child in envTransform)
            {
                if (child.CompareTag("blueGoal"))
                {
                    if (team == Team.Blue)
                        m_OwnGoal = child.gameObject;
                    else
                        m_OpponentGoal = child.gameObject;
                }
                else if (child.CompareTag("purpleGoal"))
                {
                    if (team == Team.Purple)
                        m_OwnGoal = child.gameObject;
                    else
                        m_OpponentGoal = child.gameObject;
                }
            }
        }
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

    public override void OnActionReceived(ActionBuffers actionBuffers)

    {

        if (m_IsDefensiveTeam)
        {
            ApplyDefensiveRewards();
        }
        else
        {
            // Normal rewards for Blue team
            if (position == Position.Goalie)
            {
                AddReward(m_Existential);
            }
            else if (position == Position.Striker)
            {
                AddReward(-m_Existential);
            }
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
            if (m_IsDefensiveTeam)
            {
                float defensiveReward = CalculateDefensiveBallTouchReward();
                AddReward(defensiveReward);
            }
            else
            {
                AddReward(.2f * m_BallTouch);
            }
            var dir = c.contacts[0].point - transform.position;
            dir = dir.normalized;
            c.gameObject.GetComponent<Rigidbody>().AddForce(dir * force);
        }
    }

    public override void OnEpisodeBegin()
    {
        m_BallTouch = m_ResetParams.GetWithDefault("ball_touch", 0);
        m_LastDistanceToBall = 0f;
        m_LastDistanceToOwnGoal = 0f;
    }
    
    void ApplyDefensiveRewards()
    {
        if (m_Ball == null || m_OwnGoal == null || m_OpponentGoal == null) return;
        
        Vector3 ballPos = m_Ball.transform.position;
        Vector3 ownGoalPos = m_OwnGoal.transform.position;
        Vector3 oppGoalPos = m_OpponentGoal.transform.position;
        Vector3 agentPos = transform.position;
        
        // 1. Reward for being in the middle of ball and own goal.
        float distBallToOwnGoal = Vector3.Distance(ballPos, ownGoalPos);
        float distAgentToOwnGoal = Vector3.Distance(agentPos, ownGoalPos);
        float distAgentToBall = Vector3.Distance(agentPos, ballPos);
        
        if (distAgentToOwnGoal < distBallToOwnGoal && distAgentToBall < 10f)
        {
            AddReward(0.001f);
        }
        
        // 2. Reward for moving towards the ball when it's in defending half.
        bool ballInDefensiveHalf = (team == Team.Purple && ballPos.x > 0) || 
                                   (team == Team.Blue && ballPos.x < 0);
        
        if (ballInDefensiveHalf && m_LastDistanceToBall > 0)
        {
            float deltaDistance = m_LastDistanceToBall - distAgentToBall;
            if (deltaDistance > 0)
            {
                AddReward(0.002f * deltaDistance);
            }
        }
        
        // 3. Negative reward if ball gets too close to own goal.
        if (distBallToOwnGoal < 5f)
        {
            AddReward(-0.01f * (5f - distBallToOwnGoal));
        }
        
        // 4. Reward for ball being on opponent's side - trying something like the best defense is a good offense.
        float distBallToOppGoal = Vector3.Distance(ballPos, oppGoalPos);
        if (distBallToOppGoal < 15f)
        {
            AddReward(0.003f * (15f - distBallToOppGoal) / 15f);
        }
        
        if (position == Position.Goalie)
        {
            if (distAgentToOwnGoal < 3f)
            {
                AddReward(0.002f);
            }
            // Existential for goalie, helps a bit when goalie is idling waiting for ball.
            AddReward(m_Existential * 0.5f);
        }
        else if (position == Position.Striker)
        {
            bool ballInOffensiveHalf = !ballInDefensiveHalf;
            // Reward for striker to pressure enemy offense.
            if (ballInOffensiveHalf && distAgentToBall < 5f)
            {
                AddReward(0.002f);
            }
        }
        else
        {
            // Generic position - balanced defense
            AddReward(m_Existential * 0.2f);
        }
        
        m_LastDistanceToBall = distAgentToBall;
        m_LastDistanceToOwnGoal = distAgentToOwnGoal;
    }
    
    float CalculateDefensiveBallTouchReward()
    {
        if (m_Ball == null || m_OwnGoal == null || m_OpponentGoal == null)
            return 0.2f * m_BallTouch;
        
        Vector3 ballPos = m_Ball.transform.position;
        Vector3 ownGoalPos = m_OwnGoal.transform.position;
        
        float distBallToOwnGoal = Vector3.Distance(ballPos, ownGoalPos);
        
        // Higher reward for touching ball when it's close to own goal (defensive clearance)
        if (distBallToOwnGoal < 8f)
        {
            return 0.5f * m_BallTouch;
        }
        // Good reward for touching ball in middle field
        else if (distBallToOwnGoal < 15f)
        {
            return 0.3f * m_BallTouch;
        }
        // Standard reward for offensive touches (pushing toward opponent goal)
        else
        {
            return 0.25f * m_BallTouch;
        }
    }

}
