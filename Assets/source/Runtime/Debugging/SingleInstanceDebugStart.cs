using System;
using System.Collections;
using System.Linq;
using Mirage;
using Mirage.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JamesFrowen.CSP.Debugging
{
    /// <summary>
    /// Starts a server and client inside the game unity instance. Used by the examples to show copies of same object.
    /// </summary>
    public class SingleInstanceDebugStart : NetworkManager
    {
        private static SingleInstanceDebugStart instance;

        public GameObject prefab;
        [Scene] public string scene;

        public bool ShowServer;
        public bool ShowClient;
        public bool ShowNoNetwork;
        public bool ShowGui;

        public SimulationMode simulationMode;
        public LocalPhysicsMode localPhysicsMode;
        public Color ServerColor = new Color(1, 0, 0, 0.7f);
        public Color ClientColor = Color.green;

        private PredictionManager ClientManager;
        private PredictionManager ServerManager;

        private void Awake()
        {
            LogFactory.ReplaceLogHandler(new Handler { inner = Debug.unityLogger });

            if (instance == null)
            {
                instance = this;
                StartCoroutine(Setup());
            }
        }

        private class Handler : ILogHandler
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

        private PredictionManager CreateManager(NetworkClient client, NetworkServer server, Scene scene)
        {
            var nameSuffix = server != null ? "Server" : "Client";
            var go = new GameObject($"PredictionManager {nameSuffix}");
            go.SetActive(false);
            var manager = go.AddComponent<PredictionManager>();
            if (ShowGui)
            {
                manager.DebugOutput = go.AddComponent<TickDebuggerGui>();
            }
            manager.Setup(server, client);
            manager.PhysicsMode = simulationMode;
            SceneManager.MoveGameObjectToScene(go, scene);
            go.SetActive(true);
            return manager;
        }
        private IEnumerator Setup()
        {
            if (localPhysicsMode == LocalPhysicsMode.Physics2D)
            {
#if UNITY_2020_1_OR_NEWER
                Physics2D.simulationMode = SimulationMode2D.Script;
#else
                Physics2D.autoSimulation = false;
#endif
            }
            if (localPhysicsMode == LocalPhysicsMode.Physics3D)
            {
#if UNITY_2022_2_OR_NEWER
                Physics.simulationMode = UnityEngine.SimulationMode.Script;
#else
                Physics.autoSimulation = false;
#endif
            }


            yield return SetupServer();
            yield return SetupClient();

            while (!Client.IsConnected || ClientManager == null)
                yield return null;

            ClientManager.SetClientReady(true);
            ServerManager.SetServerRunning(true);
        }

        private IEnumerator SetupServer()
        {
            var serverOp = LoadScene(scene, new LoadSceneParameters { loadSceneMode = LoadSceneMode.Additive, localPhysicsMode = localPhysicsMode });
            yield return serverOp;
            var serverScene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);


            Server.StartServer();
            Action<NetworkIdentity> ChangeObjectColor = ni =>
            {
                var renderer = ni.GetComponent<Renderer>();
                var color = renderer.material.color;
                renderer.material.color = color * ServerColor;
            };
            Server.World.onSpawn += ChangeObjectColor;
            Server.World.SpawnedIdentities.ToList().ForEach(ChangeObjectColor);
            Server.Authenticated.AddListener(player =>
            {
                var clone = Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(clone, serverScene);
                ServerManager = CreateManager(null, Server, serverScene);
                ServerObjectManager.AddCharacter(player, clone);

                clone.GetComponent<Renderer>().enabled = ShowServer;
            });

            // wait for 2 frames so that SOM spawns only objects in first scene
            yield return null;
            yield return null;
        }

        private IEnumerator SetupClient()
        {
            var clientOp = LoadScene(scene, new LoadSceneParameters { loadSceneMode = LoadSceneMode.Additive, localPhysicsMode = localPhysicsMode });
            yield return clientOp;
            var clientScene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);

            Scene clientScene2 = default;
            if (ShowNoNetwork)
            {
                var clientOp2 = LoadScene(scene, new LoadSceneParameters { loadSceneMode = LoadSceneMode.Additive, localPhysicsMode = localPhysicsMode });
                yield return clientOp2;
                clientScene2 = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);
            }


            ClientObjectManager.RegisterSpawnHandler(prefab.GetComponent<NetworkIdentity>().PrefabHash, (msg) =>
            {
                var clone = Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(clone, clientScene);
                ClientManager = CreateManager(Client, null, clientScene);
                clone.GetComponent<Renderer>().enabled = ShowClient;

                if (ShowNoNetwork)
                {
                    var clone2 = Instantiate(prefab);
                    SceneManager.MoveGameObjectToScene(clone2, clientScene2);
                    var behaviour2 = clone2.GetComponent<IDebugPredictionLocalCopy>();
                    clone.GetComponent<IDebugPredictionLocalCopy>().Copy = behaviour2;
                    var tickRunner = new TickRunner(ClientManager.TickRate);
                    var predictionTime = new PredictionTime(tickRunner);
                    behaviour2.Setup(predictionTime);
                    clone2.GetComponent<Renderer>().material.color = Color.blue;

                    clone2.GetComponent<Renderer>().enabled = true;
                }

                return clone.GetComponent<NetworkIdentity>();
            }, (spawned) => Destroy(spawned));
            Client.Started.AddListener(() =>
            {
                Action<NetworkIdentity> ChangeObjectColor = ni =>
                {
                    var renderer = ni.GetComponent<Renderer>();
                    var color = renderer.material.color;
                    renderer.material.color = color * ClientColor;
                };
                Client.World.onSpawn += ChangeObjectColor;
                Client.World.SpawnedIdentities.ToList().ForEach(ChangeObjectColor);
            });

            Client.Connect();
        }

        private static AsyncOperation LoadScene(string scene, LoadSceneParameters parameters)
        {
            return SceneManager.LoadSceneAsync(scene, parameters);
            //#if UNITY_EDITOR
            //            return EditorSceneManager.LoadSceneAsyncInPlayMode(scene, parameters);
            //#else
            //#endif
        }
    }
}
