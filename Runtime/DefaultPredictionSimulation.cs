/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using System;
using System.ComponentModel;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JamesFrowen.CSP.Simulations
{
    public class DefaultPredictionSimulation : IPredictionSimulation
    {
        readonly SimulationMode mode;
        readonly PhysicsScene local3d;
        readonly PhysicsScene2D local2d;

        public DefaultPredictionSimulation(SimulationMode mode, Scene scene)
        {
            this.mode = mode;
            // todo maybe use Physics.defaultPhysicsScene for non-local physics
            switch (mode)
            {
                case SimulationMode.Physics3D:
                    Physics.autoSimulation = false;
                    break;
                case SimulationMode.Physics2D:
#if UNITY_2020_1_OR_NEWER
                    // Unity 2020.1+ deprecated Physics2D.autoSimulation, using that causes a Compile Error
                    // when building the player. So we set the simulation mode for 2D Physics to be controlled
                    // via Script.
                    Physics2D.simulationMode = SimulationMode2D.Script;
#else
                    // Stock standard disabling of 2D Physics auto simulation.
                    Physics2D.autoSimulation = false;
#endif
                    break;
                case SimulationMode.Local3D:
                    local3d = scene.GetPhysicsScene();
                    break;
                case SimulationMode.Local2D:
                    local2d = scene.GetPhysicsScene2D();
                    break;
                default:
                    throw new InvalidEnumArgumentException();
            }
        }
        public DefaultPredictionSimulation(SimulationMode mode)
        {
            this.mode = mode;
            // todo maybe use Physics.defaultPhysicsScene for non-local physics
            switch (mode)
            {
                case SimulationMode.Physics3D:
                    Physics.autoSimulation = false;
                    break;
                case SimulationMode.Physics2D:
#if UNITY_2020_1_OR_NEWER
                    // Unity 2020.1+ deprecated Physics2D.autoSimulation. See previous comments above in DefaultPredictionSimulation(mode, scene)
                    Physics2D.simulationMode = SimulationMode2D.Script;
#else
                    // Stock standard disabling of 2D Physics auto simulation.
                    Physics2D.autoSimulation = false;
#endif
                    break;
                case SimulationMode.Local3D:
                case SimulationMode.Local2D:
                    throw new ArgumentException("Scene should be passed in when using local physics", nameof(mode));
                default:
                    throw new InvalidEnumArgumentException();
            }
        }

        public void Simulate(float fixedDelta)
        {
            switch (mode)
            {
                case SimulationMode.Physics3D:
                    Physics.Simulate(fixedDelta);
                    break;
                case SimulationMode.Physics2D:
                    Physics2D.Simulate(fixedDelta);
                    break;
                case SimulationMode.Local3D:
                    local3d.Simulate(fixedDelta);
                    break;
                case SimulationMode.Local2D:
                    local2d.Simulate(fixedDelta);
                    break;
            }
        }
    }
}
