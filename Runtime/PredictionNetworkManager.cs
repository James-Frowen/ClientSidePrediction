using System.Collections;
using Mirage;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JamesFrowen.CSP
{
    public class PredictionNetworkManager : NetworkManager
    {
        static PredictionNetworkManager instance;

        public GameObject prefab;
        [Scene] public string scene;

        private void Awake()
        {
            Physics.autoSimulation = false;
            if (instance == null)
            {
                instance = this;
                StartCoroutine(Setup());
            }
        }
        private IEnumerator Setup()
        {
            UnityEngine.AsyncOperation serverOp = SceneManager.LoadSceneAsync(scene, new LoadSceneParameters { loadSceneMode = LoadSceneMode.Additive, localPhysicsMode = LocalPhysicsMode.Physics3D });
            yield return serverOp;
            Scene serverScene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);

            Server.StartServer();
            Server.Connected.AddListener(player =>
            {
                GameObject clone = Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(clone, serverScene);
                ServerObjectManager.AddCharacter(player, clone);
                clone.GetComponent<Renderer>().material.color = new Color(1, 0, 0, 0.7f);

                clone.GetComponent<Renderer>().enabled = true;
            });


            UnityEngine.AsyncOperation clientOp = SceneManager.LoadSceneAsync(scene, new LoadSceneParameters { loadSceneMode = LoadSceneMode.Additive, localPhysicsMode = LocalPhysicsMode.Physics3D });
            yield return clientOp;
            Scene clientScene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);

            UnityEngine.AsyncOperation clientOp2 = SceneManager.LoadSceneAsync(scene, new LoadSceneParameters { loadSceneMode = LoadSceneMode.Additive, localPhysicsMode = LocalPhysicsMode.Physics3D });
            yield return clientOp2;
            Scene clientScene2 = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);

            ClientObjectManager.RegisterSpawnHandler(prefab.GetComponent<NetworkIdentity>().PrefabHash, (msg) =>
            {
                GameObject clone = Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(clone, clientScene);
                clone.GetComponent<Renderer>().material.color = Color.green;
                clone.GetComponent<Renderer>().enabled = true;

                GameObject clone2 = Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(clone2, clientScene2);
                IDebugPredictionBehaviour behaviour2 = clone2.GetComponent<IDebugPredictionBehaviour>();
                clone.GetComponent<IDebugPredictionBehaviour>().Copy = behaviour2;
                behaviour2.Setup(GetComponent<TickRunner>());
                clone2.GetComponent<Renderer>().material.color = Color.blue;


                clone2.GetComponent<Renderer>().enabled = true;

                return clone.GetComponent<NetworkIdentity>();
            }, (spawned) => Destroy(spawned));
            Client.Started.AddListener(() =>
            {
                // need lower frequency so RTT updates faster
                Client.World.Time.PingInterval = 0.1f;
            });

            Client.Connect();
        }
    }
}
