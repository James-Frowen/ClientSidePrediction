using Mirage;
using UnityEngine;

namespace JamesFrowen.CSP
{
    public class MainCameraFollow : NetworkBehaviour
    {
        public Vector3 positionOffset;
        public Vector3 eularOffset;

        [Range(0, 1)]
        public float smooth = 0.2f;

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

            Vector3 target = transform.position + positionOffset;
            follower.position = Vector3.Lerp(follower.position, target, smooth);
            //follower.rotation = Quaternion.Euler(eularOffset);
        }
    }
}
