using System.Collections;
using Mirage;
using UnityEngine;

namespace JamesFrowen.CSP
{
    public class TickDebuggerSetup : MonoBehaviour
    {
        public NetworkManager managerPrefab;

        private IEnumerator Start()
        {
            NetworkManager server = Instantiate(managerPrefab);
            NetworkManager client = Instantiate(managerPrefab);

            yield return null;
            yield return null;
            server.Server.StartServer();
            yield return new WaitForSeconds(1);
            client.Client.Connect();
        }
    }
}
