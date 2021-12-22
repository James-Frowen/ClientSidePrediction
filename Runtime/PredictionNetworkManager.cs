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
                clone.GetComponent<Renderer>().material.color = Color.red;

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

                GameObject clone2 = Instantiate(prefab);
                PredictionBehaviour behaviour2 = clone2.GetComponent<PredictionBehaviour>();
                clone.GetComponent<PredictionBehaviour>().Copy = behaviour2;
                behaviour2.physics = clientScene2.GetPhysicsScene();
                behaviour2.GetComponent<Renderer>().material.color = Color.blue;
                behaviour2.tickRunner = GetComponent<TickRunner>();

                SceneManager.MoveGameObjectToScene(clone2, clientScene2);

                clone.GetComponent<Renderer>().enabled = true;
                clone2.GetComponent<Renderer>().enabled = true;

                return clone.GetComponent<NetworkIdentity>();
            }, (spawned) => Destroy(spawned));

            Client.Connect();
        }
    }
}
