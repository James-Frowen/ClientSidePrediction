/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using UnityEngine;

namespace JamesFrowen.Mirage.DebugScripts
{
    [RequireComponent(typeof(LagSocketFactory))]
    public class LagSocketFactoryGUI : MonoBehaviour
    {
        LagSocketFactory _factory;
        public Rect guiOffset;
        public Color guiColor;
        private LagSocketGUI drawer;

        private void Awake()
        {
            _factory = GetComponent<LagSocketFactory>();
        }

        private void OnGUI()
        {
            if (drawer == null) drawer = new LagSocketGUI();
            drawer.OnGUI(guiOffset, guiColor, _factory.settings);
        }
    }
}
