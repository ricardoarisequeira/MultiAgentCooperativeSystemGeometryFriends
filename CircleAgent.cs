﻿using System;
using System.Linq;
using System.Drawing;
using System.Threading.Tasks;
using System.Collections.Generic;

using GeometryFriends.AI;
using GeometryFriends.AI.Interfaces;
using GeometryFriends.AI.Communication;
using GeometryFriends.AI.Perceptions.Information;
using static GeometryFriendsAgents.GameInfo;
using static GeometryFriendsAgents.Graph;
using static GeometryFriendsAgents.LevelRepresentation;

namespace GeometryFriendsAgents
{
    /// <summary>
    /// A circle agent implementation for the GeometryFriends game that demonstrates prediction and history keeping capabilities.
    /// </summary>
    public class CircleAgent : AbstractCircleAgent
    {
        //Sensors Information
        private LevelRepresentation levelInfo;
        private CircleRepresentation circleInfo;
        private RectangleRepresentation rectangleInfo;

        // Graph
        private GraphCircle graph;

        // Search Algorithm
        private SubgoalAStar subgoalAStar;

        // Reinforcement Learning
        private ActionSelector actionSelector;

        // Cooperation
        private bool rectangle_ping;
        private CooperationStatus cooperation;
        private List<AgentMessage> messages;

        // Auxiliary Variables
        private Moves currentAction;
        private Move? nextMove;
        private DateTime lastMoveTime;
        private int targetPointX_InAir;
        private bool[] previousCollectibles;
        private Platform? previousPlatform, currentPlatform;

        public CircleAgent()
        {
            nextMove = null;
            currentPlatform = null;
            previousPlatform = null;
            lastMoveTime = DateTime.Now;
            currentAction = Moves.NO_ACTION;

            graph = new GraphCircle();
            subgoalAStar = new SubgoalAStar();
            actionSelector = new ActionSelector();
            levelInfo = new LevelRepresentation();

            rectangle_ping = false;
            messages = new List<AgentMessage>();
            cooperation = CooperationStatus.SINGLE;
        }

        //implements abstract circle interface: used to setup the initial information so that the agent has basic knowledge about the level
        public override void Setup(CountInformation nI, RectangleRepresentation rI, CircleRepresentation cI, ObstacleRepresentation[] oI, ObstacleRepresentation[] rPI, ObstacleRepresentation[] cPI, CollectibleRepresentation[] colI, Rectangle area, double timeLimit)
        {
        
            messages.Add(new AgentMessage(IST_CIRCLE_PLAYING));

            // Create Level Array
            levelInfo.CreateLevelArray(colI, oI, rPI, cPI);

            // Create Graph
            graph.initialRectangleInfo = rI;
            graph.SetupGraph(levelInfo.GetLevelArray(), colI.Length);
            graph.SetPossibleCollectibles(cI);

            if (rI.X < 0 || rI.Y < 0)
            {
                graph.DeleteCooperationPlatforms();
                //cooperation = CooperationStatus.NOT_COOPERATING;
            }

            // Initial Information
            circleInfo = cI;
            rectangleInfo = rI;
            targetPointX_InAir = (int)circleInfo.X;
            previousCollectibles = levelInfo.GetObtainedCollectibles();
        }

        //implements abstract circle interface: registers updates from the agent's sensors that it is up to date with the latest environment information
        /*WARNING: this method is called independently from the agent update - Update(TimeSpan elapsedGameTime) - so care should be taken when using complex 
         * structures that are modified in both (e.g. see operation on the "remaining" collection)      
         */
        public override void SensorsUpdated(int nC, RectangleRepresentation rI, CircleRepresentation cI, CollectibleRepresentation[] colI)
        {
            circleInfo = cI;
            rectangleInfo = rI;
            levelInfo.collectibles = colI;
        }

        //implements abstract circle interface: signals if the agent is actually implemented or not
        public override bool ImplementedAgent()
        {
            return true;
        }

        //implements abstract circle interface: provides the name of the agent to the agents manager in GeometryFriends
        public override string AgentName()
        {
            return "IST Circle";
        }

        //implements abstract circle interface: GeometryFriends agents manager gets the current action intended to be actuated in the enviroment for this agent
        public override Moves GetAction()
        {
            return currentAction;
        }

        //implements abstract circle interface: updates the agent state logic and predictions
        public override void Update(TimeSpan elapsedGameTime)
        {

            if (!rectangle_ping)
            {
                graph.DeleteCooperationPlatforms();
                rectangle_ping = false;
                //cooperation = CooperationStatus.NOT_COOPERATING;
            }

            if ((DateTime.Now - lastMoveTime).TotalMilliseconds >= 20)
            {

                currentPlatform = graph.GetPlatform(new LevelRepresentation.Point((int)circleInfo.X, (int)circleInfo.Y), MAX_CIRCLE_HEIGHT, (int)circleInfo.VelocityY);

                if (!currentPlatform.HasValue && cooperation == CooperationStatus.IN_POSITION && IsRidingRectangle())
                {
                    currentPlatform = previousPlatform;  
                }

                if (currentPlatform.HasValue)
                {

                    if (IsDifferentPlatform() || IsGetCollectible())
                    {
                        targetPointX_InAir = (currentPlatform.Value.leftEdge + currentPlatform.Value.rightEdge) / 2;
                        Task.Factory.StartNew(SetNextEdge);
                    }

                    if (nextMove.HasValue)
                    {

                        if (cooperation == CooperationStatus.RIDING)
                        {
                            currentAction = actionSelector.GetCurrentAction(circleInfo, (int)rectangleInfo.X, 0, true);
                        }

                        else if (cooperation == CooperationStatus.AWAITING)
                        {
                            currentAction = Moves.NO_ACTION;
                        }

                        else if (-MAX_VELOCITYY <= circleInfo.VelocityY && circleInfo.VelocityY <= MAX_VELOCITYY)
                        {
                            if (nextMove.Value.type == movementType.STAIR_GAP)
                            {
                                currentAction = nextMove.Value.precondition.right_direction ? Moves.ROLL_RIGHT : Moves.ROLL_LEFT;
                            }
                            else
                            {
                                currentAction = actionSelector.GetCurrentAction(circleInfo, nextMove.Value.precondition.position.x, nextMove.Value.precondition.horizontal_velocity, nextMove.Value.precondition.right_direction);
                            }
                        }
                        else
                        {
                            currentAction = actionSelector.GetCurrentAction(circleInfo, targetPointX_InAir, 0, true);
                        }

                    }

                    else if (cooperation != CooperationStatus.SINGLE && currentPlatform.Value.type != platformType.COOPERATION)
                    {
                        cooperation = CooperationStatus.SINGLE;
                        messages.Add(new AgentMessage(COOPERATION_FINISHED));
                    }
                }

                else
                {
                    if (nextMove.HasValue)
                    {
                        if ((cooperation == CooperationStatus.IN_POSITION && nextMove.Value.reachablePlatform.type == platformType.COOPERATION) || cooperation == CooperationStatus.RIDE_HELP)
                        {
                            currentAction = actionSelector.GetCurrentAction(circleInfo, (int)rectangleInfo.X, 0, true);
                        }
                        else if (nextMove.Value.type == movementType.JUMP || nextMove.Value.type == movementType.FALL)
                        {
                            currentAction = Moves.NO_ACTION;
                        }
                        else if (nextMove.Value.type == movementType.STAIR_GAP)
                        {
                            currentAction = nextMove.Value.precondition.right_direction ? Moves.ROLL_RIGHT : Moves.ROLL_LEFT;
                        }
                        else
                        {
                            if (nextMove.Value.collideCeiling && circleInfo.VelocityY < 0)
                            {
                                currentAction = Moves.NO_ACTION;
                            }
                            else
                            {
                                currentAction = actionSelector.GetCurrentAction(circleInfo, targetPointX_InAir, 0, true);
                            }
                        }
                    }
                }

                if (!nextMove.HasValue)
                {
                    currentAction = actionSelector.GetCurrentAction(circleInfo, (int)circleInfo.X, 0, false);
                }

                lastMoveTime = DateTime.Now;
            }

            if (nextMove.HasValue)
            {

                if ((cooperation == CooperationStatus.IN_POSITION && actionSelector.IsGoal(circleInfo, (int)rectangleInfo.X, 0, true)) ||
                    (cooperation == CooperationStatus.RIDE_HELP && currentPlatform.HasValue && currentPlatform.Value.type == platformType.COOPERATION && IsRidingRectangle()))
                {
                    cooperation = CooperationStatus.RIDING;
                }

                if (!actionSelector.IsGoal(circleInfo, nextMove.Value.precondition.position.x, nextMove.Value.precondition.horizontal_velocity, nextMove.Value.precondition.right_direction))
                {
                    return;
                }

                if (-MAX_VELOCITYY <= circleInfo.VelocityY && circleInfo.VelocityY <= MAX_VELOCITYY)
                {
                    targetPointX_InAir = (nextMove.Value.reachablePlatform.leftEdge + nextMove.Value.reachablePlatform.rightEdge) / 2;


                    if (nextMove.Value.type == movementType.JUMP)
                    {
                        currentAction = Moves.JUMP;

                        if (cooperation == CooperationStatus.IN_POSITION)
                        {
                            messages.Add(new AgentMessage(JUMPED));
                        }

                        if (cooperation == CooperationStatus.IN_POSITION && nextMove.Value.collideCeiling)
                        {
                            cooperation = CooperationStatus.RIDE_HELP;
                            messages.Add(new AgentMessage(RIDE_HELP));
                        }

                    }

                }
            }

        }

        private bool IsGetCollectible()
        {

            bool[] currentCollectibles = levelInfo.GetObtainedCollectibles();

            if (previousCollectibles.SequenceEqual(currentCollectibles))
            {
                return false;
            }


            for (int previousC = 0; previousC < previousCollectibles.Length; previousC++)
            {

                if (!previousCollectibles[previousC])
                {

                    for (int currentC = 0; currentC < currentCollectibles.Length; currentC++)
                    {

                        if (currentCollectibles[currentC])
                        {

                            foreach (Graph.Platform p in graph.platforms)
                            {

                                foreach (Graph.Move m in p.moves)
                                {

                                    m.collectibles_onPath[currentC] = false;

                                }

                            }

                        }
                    }
                }

            }

            previousCollectibles = currentCollectibles;

            return true;
        }

        private bool IsDifferentPlatform()
        {

            if (currentPlatform.HasValue)
            {
                if (!previousPlatform.HasValue)
                {
                    previousPlatform = currentPlatform;
                    return true;
                }
                else if (currentPlatform.Value.id != previousPlatform.Value.id)
                {
                    previousPlatform = currentPlatform;
                    return true;
                }
            }

            previousPlatform = currentPlatform;
            return false;
        }

        private void SetNextEdge()
        {

            Move? previousMove = nextMove;

            nextMove = null;
            nextMove = subgoalAStar.CalculateShortestPath(currentPlatform.Value, new LevelRepresentation.Point((int)circleInfo.X, (int)circleInfo.Y),
                Enumerable.Repeat<bool>(true, levelInfo.initialCollectibles.Length).ToArray(),
                levelInfo.GetObtainedCollectibles(), levelInfo.initialCollectibles);

            if (nextMove.HasValue)
            {
                if (previousMove.HasValue &&
                (previousMove.Value.type != nextMove.Value.type ||
                previousMove.Value.precondition.position.x != nextMove.Value.precondition.position.x ||
                previousMove.Value.landPoint.x != nextMove.Value.landPoint.x) &&
                cooperation == CooperationStatus.AWAITING)
                {
                    cooperation = CooperationStatus.SINGLE;
                    messages.Add(new AgentMessage(CLEAN_RIDES));
                }

                //if (nextMove.Value.reachablePlatform.type == platformType.COOPERATION && cooperation == CooperationStatus.NOT_COOPERATING)
                if (nextMove.Value.reachablePlatform.type == platformType.COOPERATION || IsRidingRectangle())
                {
                    Cooperate((Move)nextMove);
                }


                if (currentPlatform.HasValue && currentPlatform.Value.type != platformType.COOPERATION && nextMove.Value.reachablePlatform.type != platformType.COOPERATION)
                {
                    cooperation = CooperationStatus.SINGLE;
                    messages.Add(new AgentMessage(COOPERATION_FINISHED));
                }

                if (nextMove.Value.reachablePlatform.type != platformType.COOPERATION && cooperation == CooperationStatus.SINGLE)
                {
                    messages.Add(new AgentMessage(INDIVIDUAL_MOVE, nextMove));
                }

            }

        }

        private void Cooperate(Move move)
        {
            Move manipulatedMove = CopyMove(move);
            manipulatedMove.collectibles_onPath = graph.GetCollectiblesFromCooperation(move);

            Platform fromPlatform = currentPlatform.HasValue ? (Platform) currentPlatform : (Platform) previousPlatform;

            if (fromPlatform.type == platformType.COOPERATION)
            {
                int rectangle_height = 50 + (fromPlatform.height - (move.precondition.position.y + CIRCLE_RADIUS));
                manipulatedMove.partner_precondition = new PreCondition(move.precondition.position, rectangle_height, 0, move.precondition.right_direction);
                messages.Add(new AgentMessage(RIDING, manipulatedMove));
                cooperation = CooperationStatus.RIDING;
            }
            else
            {

                bool rectangleIsRight = (rectangleInfo.X - circleInfo.X > 0);

                if (rectangleIsRight != move.precondition.right_direction)
                {
                    foreach (Move m in fromPlatform.moves)
                    {
                        if (m.reachablePlatform.id == move.reachablePlatform.id && m.type == movementType.JUMP && m.precondition.right_direction != move.precondition.right_direction)
                        {
                            nextMove = m;
                            manipulatedMove = CopyMove(m);
                            manipulatedMove.collectibles_onPath = graph.GetCollectiblesFromCooperation(m);
                            break;
                        }
                    }
                }

                manipulatedMove.precondition.height = 50 + (move.reachablePlatform.height - (move.landPoint.y + CIRCLE_RADIUS));
                manipulatedMove.type = movementType.RIDE;
                messages.Add(new AgentMessage(AWAITING, manipulatedMove));
                cooperation = CooperationStatus.AWAITING;
            }

        }

        //implements abstract circle interface: signals the agent the end of the current level
        public override void EndGame(int collectiblesCaught, int timeElapsed)
        {
            Console.WriteLine("CIRCLE - Collectibles caught = {0}, Time elapsed - {1}", collectiblesCaught, timeElapsed);
        }

        public override List<AgentMessage> GetAgentMessages()
        {
            List<AgentMessage> toSent = new List<AgentMessage>(messages);
            messages.Clear();
            return toSent;
        }

        //implememts abstract agent interface: receives messages from the circle agent
        public override void HandleAgentMessages(List<GeometryFriends.AI.Communication.AgentMessage> newMessages)
        {
            foreach (AgentMessage item in newMessages)
            {

                if (item.Message.Equals(IST_RECTANGLE_PLAYING))
                {
                    rectangle_ping = true;
                    //cooperation = CooperationStatus.NOT_COOPERATING;
                }

                if (item.Message.Equals(IN_POSITION))
                {
                    cooperation = CooperationStatus.IN_POSITION;
                }

            }

            return;
        }

        public static bool IsObstacle_onPixels(int[,] levelArray, List<ArrayPoint> checkPixels)
        {
            if (checkPixels.Count == 0)
            {
                return true;
            }

            foreach (ArrayPoint i in checkPixels)
            {
                if (levelArray[i.yArray, i.xArray] == BLACK || levelArray[i.yArray, i.xArray] == GREEN)
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsRidingRectangle()
        {

            int rectangleWidth = RECTANGLE_AREA / (int)rectangleInfo.Height;

            if (Math.Abs(circleInfo.VelocityY) < MAX_VELOCITYY &&
                (circleInfo.X >= rectangleInfo.X - (rectangleWidth / 2)) &&
                (circleInfo.X <= rectangleInfo.X + (rectangleWidth / 2)) &&
                 Math.Abs(rectangleInfo.Y - (rectangleInfo.Height / 2) - CIRCLE_RADIUS - circleInfo.Y) <= 8)
            {
                return true;
            }

            return false;
        }

    }
}

