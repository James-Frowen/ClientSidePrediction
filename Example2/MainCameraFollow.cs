using Mirage;
using UnityEngine;

namespace JamesFrowen.CSP
{
    public class MainCameraFollow : NetworkBehaviour
    {
        public Vector3 positionOffset;
        public Vector3 eularOffset;
        private Transform follower;

        private void Awake()
        {
            Identity.OnStartLocalPlayer.AddListener(StartLocalPlayer);

        }

        private void StartLocalPlayer()
        {
            follower = Camera.main.transform;
            follower.rotation = Quaternion.Euler(eularOffset);
        }

        private void Update()
        {
            if (follower == null)
                return;

            Vector3 pos = transform.position + positionOffset;
            follower.position = pos;
            //follower.rotation = Quaternion.Euler(eularOffset);
        }
    }
}
