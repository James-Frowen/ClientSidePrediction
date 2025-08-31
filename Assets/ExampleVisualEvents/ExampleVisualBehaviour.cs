/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using Mirage;
using UnityEngine;

namespace JamesFrowen.CSP.VisualEvents
{
    public class ExampleVisualBehaviour : PredictionBehaviour<NoValues>, IVisualEventBehaviour
    {
        VisualEventManager IVisualEventBehaviour.VisualEventManager { get; set; }

        public void Awake()
        {
            this.RegisterEventBehaviour<ExampleVisualBehaviour, AudioEvent>(HandleAudioEvent);
        }

        private static void HandleAudioEvent(ExampleVisualBehaviour behaviour, AudioEvent data)
        {
            behaviour.HandleAudioEvent(data);
        }

        private void HandleAudioEvent(AudioEvent data)
        {
            Debug.Log($"Audio event! {data.clipName} at {data.position}");
        }

        public void PlayAudio(AudioEvent data)
        {
            this.QueueEventBehaviour(data);
        }

        [NetworkMessage]
        public struct AudioEvent
        {
            public string clipName;
            public Vector3 position;
        }
    }
}
