using System;
using System.Collections;
using System.Linq;
using Mirage;
using Mirage.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JamesFrowen.CSP.Examples
{
    public class PredictionNetworkManager : NetworkManager
    {
        static PredictionNetworkManager instance;

        public GameObject prefab;
        [Scene] public string scene;

        public bool ShowServer;
        public bool ShowClient;
        public bool ShowNoNetwork;

        public Color ServerColor = new Color(1, 0, 0, 0.7f);
        public Color ClientColor = Color.green;

        private void Awake()
        {
            LogFactory.ReplaceLogHandler(new Handler { inner = Debug.unityLogger });
            Physics.autoSimulation = false;
            if (instance == null)
            {
                instance = this;
                StartCoroutine(Setup());
            }
        }

        class Handler : ILogHandler
        {
            public ILogHandler inner;

            public void LogException(Exception exception, UnityEngine.Object context)
            {
                inner.LogException(exception, context);
            }

            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
            {
                inner.LogFormat(logType, context, $"[{DateTime.Now:HH:mm:ss.ffff}] {format}", args);
            }
        }
        static PredictionManager CreateManager(NetworkClient client, NetworkServer server, Scene scene)
        {
            string nameSuffix = server != null ? "Server" : "Client";
            var go = new GameObject($"PredictionManager {nameSuffix}");
            go.SetActive(false);
            PredictionManager manager = go.AddComponent<PredictionManager>();
            manager.Client = client;
            manager.Server = server;
            manager.physicsMode = SimulationMode.Local3D;
            SceneManager.MoveGameObjectToScene(go, scene);
            go.SetActive(true);
            return manager;
        }
        private IEnumerator Setup()
        {
            yield return SetupServer();
            yield return SetupClient();
        }
        private IEnumerator SetupServer()
        {
            UnityEngine.AsyncOperation serverOp = SceneManager.LoadSceneAsync(scene, new LoadSceneParameters { loadSceneMode = LoadSceneMode.Additive, localPhysicsMode = LocalPhysicsMode.Physics3D });
            yield return serverOp;
            Scene serverScene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);


            Server.StartServer();
            Action<NetworkIdentity> ChangeObjectColor = ni =>
            {
                Renderer renderer = ni.GetComponent<Renderer>();
                Color color = renderer.material.color;
                renderer.material.color = color * ServerColor;
            };
            Server.World.onSpawn += ChangeObjectColor;
            Server.World.SpawnedIdentities.ToList().ForEach(ChangeObjectColor);
            Server.Connected.AddListener(player =>
            {
                GameObject clone = Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(clone, serverScene);
                _ = CreateManager(null, Server, serverScene);
                ServerObjectManager.AddCharacter(player, clone);

                clone.GetComponent<Renderer>().enabled = ShowServer;
            });

            // wait for 2 frames so that SOM spawns only objects in first scene
            yield return null;
            yield return null;
        }

        private IEnumerator SetupClient()
        {
            UnityEngine.AsyncOperation clientOp = SceneManager.LoadSceneAsync(scene, new LoadSceneParameters { loadSceneMode = LoadSceneMode.Additive, localPhysicsMode = LocalPhysicsMode.Physics3D });
            yield return clientOp;
            Scene clientScene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);

            Scene clientScene2 = default;
            if (ShowNoNetwork)
            {
                UnityEngine.AsyncOperation clientOp2 = SceneManager.LoadSceneAsync(scene, new LoadSceneParameters { loadSceneMode = LoadSceneMode.Additive, localPhysicsMode = LocalPhysicsMode.Physics3D });
                yield return clientOp2;
                clientScene2 = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);
            }


            ClientObjectManager.RegisterSpawnHandler(prefab.GetComponent<NetworkIdentity>().PrefabHash, (msg) =>
            {
                GameObject clone = Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(clone, clientScene);
                PredictionManager manager = CreateManager(Client, null, clientScene);
                clone.GetComponent<Renderer>().enabled = ShowClient;

                if (ShowNoNetwork)
                {
                    GameObject clone2 = Instantiate(prefab);
                    SceneManager.MoveGameObjectToScene(clone2, clientScene2);
                    IDebugPredictionBehaviour behaviour2 = clone2.GetComponent<IDebugPredictionBehaviour>();
                    clone.GetComponent<IDebugPredictionBehaviour>().Copy = behaviour2;
                    behaviour2.Setup(1f / manager.TickRate);
                    clone2.GetComponent<Renderer>().material.color = Color.blue;

                    clone2.GetComponent<Renderer>().enabled = true;
                }

                return clone.GetComponent<NetworkIdentity>();
            }, (spawned) => Destroy(spawned));
            Client.Started.AddListener(() =>
            {
                // need lower frequency so RTT updates faster
                Client.World.Time.PingInterval = 0.1f;

                Action<NetworkIdentity> ChangeObjectColor = ni =>
                {
                    Renderer renderer = ni.GetComponent<Renderer>();
                    Color color = renderer.material.color;
                    renderer.material.color = color * ClientColor;
                };
                Client.World.onSpawn += ChangeObjectColor;
                Client.World.SpawnedIdentities.ToList().ForEach(ChangeObjectColor);
            });

            Client.Connect();
        }
    }
}
